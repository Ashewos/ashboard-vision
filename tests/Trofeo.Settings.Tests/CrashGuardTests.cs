using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.Tests;

/// <summary>Le filet de la fenêtre (exceptions non gérées) et son journal <c>settings.log</c>.</summary>
public sealed class CrashGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-crash-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private CrashGuard NewGuard() => new(_log.Add);

    [Fact]
    public void UiThreadException_IsLogged_Shown_AndHandled()
    {
        using var guard = NewGuard();
        var shown = new List<string>();
        guard.Show = shown.Add;

        var handled = guard.OnUiThread(new TimeoutException("Le service n'a pas répondu."));

        Assert.True(handled);
        Assert.Single(_log);
        Assert.Contains("Exception de l'interface", _log[0]);
        Assert.Contains("TimeoutException", _log[0]);
        Assert.Equal(["Erreur inattendue : Le service n'a pas répondu.. Le détail est dans logs\\settings.log."], shown);
    }

    [Fact]
    public void UiThreadException_WithoutAWindow_IsStillLoggedAndHandled()
    {
        using var guard = NewGuard();

        Assert.True(guard.OnUiThread(new InvalidOperationException("tôt")));

        Assert.Single(_log);
    }

    [Fact]
    public void UiThreadException_WhenShowingFails_IsStillHandled()
    {
        using var guard = NewGuard();
        guard.Show = _ => throw new InvalidOperationException("fenêtre fermée");

        Assert.True(guard.OnUiThread(new InvalidOperationException("première")));

        Assert.Equal(2, _log.Count);
        Assert.Contains("fenêtre fermée", _log[1]);
    }

    [Fact]
    public void DomainException_IsLogged()
    {
        using var guard = NewGuard();

        guard.OnDomain(new InvalidOperationException("boum"), terminating: true);

        var line = Assert.Single(_log);
        Assert.Contains("Exception non gérée", line);
        Assert.Contains("arrêt de la fenêtre", line);
        Assert.Contains("boum", line);
    }

    [Fact]
    public void UnobservedTaskException_IsLogged_AndObserved()
    {
        using var guard = NewGuard();
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException("disque")));

        guard.OnUnobservedTask(args);

        Assert.True(args.Observed);
        Assert.Contains("disque", Assert.Single(_log));
    }

    [Fact]
    public void ProcessHandlers_CanBeInstalledTwiceAndRemoved()
    {
        var guard = NewGuard();

        guard.InstallProcessHandlers();
        guard.InstallProcessHandlers(); // sans effet la seconde fois
        guard.Dispose();
        guard.Dispose();

        Assert.Empty(_log);
    }

    [AvaloniaFact]
    public void UiHandler_TurnsAnExceptionThrownOnTheUiThreadIntoAMessage_InsteadOfClosingTheWindow()
    {
        using var guard = NewGuard();
        var shown = new List<string>();
        guard.Show = shown.Add;
        guard.InstallUiHandler();

        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("commande en échec"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Erreur inattendue : commande en échec. Le détail est dans logs\\settings.log."], shown);
        Assert.Contains("commande en échec", Assert.Single(_log));
    }

    // ---- Le journal ----

    [Fact]
    public void SettingsLog_Open_WritesToSettingsLog()
    {
        var path = Path.Combine(_dir, "logs", "settings.log");

        var listener = SettingsLog.TryOpen(path);
        Assert.NotNull(listener);
        try
        {
            Trace.TraceError("ligne de test du journal de la fenêtre");
            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        Assert.Contains("ligne de test du journal de la fenêtre", File.ReadAllText(path));
    }

    [Fact]
    public void SettingsLog_CannotBeOpened_ReturnsNull_WithoutThrowing()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "logs");
        File.WriteAllText(blocker, "un fichier là où le dossier devrait être");

        var listener = SettingsLog.TryOpen(Path.Combine(blocker, "settings.log"));

        Assert.Null(listener);
    }

    [Fact]
    public void SettingsLog_DefaultPath_IsLogsSettingsLogUnderTheSettingsDirectory()
    {
        Assert.EndsWith(Path.Combine("ashboard-vision", "logs", "settings.log"), SettingsLog.DefaultPath);
    }
}
