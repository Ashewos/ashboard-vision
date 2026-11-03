using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Trofeo.Core;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings;

public partial class App : Application
{
    /// <summary>Le verrou « une seule fenêtre » ; posé par <see cref="Program"/> avant le démarrage de l'interface.</summary>
    internal static SingleInstance? Instance { get; set; }

    /// <summary>Le filet des exceptions non gérées ; posé par <see cref="Program"/>. Sans lui (aperçus, tests), rien n'est branché.</summary>
    internal static CrashGuard? Guard { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Sans durée de vie de bureau (tests sans affichage, aperçus), l'appelant monte sa propre fenêtre.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var link = new ServiceLink(post: action => Dispatcher.UIThread.Post(action));
            var store = new LayoutStore(Path.Combine(AppPaths.SettingsDir, "layout.json"));
            var document = new LayoutDocument(store, link);
            document.Load();
            var preferencesPath = Path.Combine(AppPaths.SettingsDir, "settings-app.json");
            var preferences = EditorPreferences.Load(preferencesPath);

            var model = new MainViewModel(
                link, document, preferences, secrets: VaultSecretAccess.Default(),
                preferencesPath: preferencesPath, system: SystemServices.Windows());
            var window = new MainWindow { DataContext = model };
            desktop.MainWindow = window;

            // Trofeo.Settings.exe <chemin.avmod> : la boîte d'installation s'ouvre dès que le service est joint (rien ne s'installe sans clic).
            if (PackageArgument.Find(desktop.Args) is { } package)
                model.OpenPackage(package);

            // Une exception de l'interface ne ferme plus la fenêtre : elle va au journal et sur la ligne ambre.
            if (Guard is { } guard)
            {
                guard.Show = message => model.Session.Report(message);
                guard.InstallUiHandler();
            }

            if (Instance is { } instance)
                instance.ShowRequested += () => Dispatcher.UIThread.Post(window.BringToFront);

            desktop.Exit += (_, _) =>
            {
                model.Dispose();
                link.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
