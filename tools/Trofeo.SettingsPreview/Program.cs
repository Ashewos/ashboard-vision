using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using SkiaSharp;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Settings;
using Trofeo.Settings.Services;
using Trofeo.Settings.Tests;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;
using Trofeo.ThemePreview;

namespace Trofeo.SettingsPreview;

/// <summary>
/// Images PNG de la fenêtre de réglage : une par onglet, plus « service arrêté » et « mode émulé ». La fenêtre est la
/// vraie, rendue sans affichage (Avalonia.Headless + Skia) ; le service est un faux <see cref="IServiceLink"/>, et la
/// bande montre le vrai tableau de bord (les vrais modules de ThemePreview), tourné de 180° comme le service l'envoie.
/// <c>--out &lt;dossier&gt;</c> (par défaut : apercus-reglages).
/// </summary>
internal static class Program
{
    private const string Usage = "Usage : Trofeo.SettingsPreview [--out <dossier>]   (par défaut : apercus-reglages)";

    private static async Task<int> Main(string[] args)
    {
        var folder = "apercus-reglages";
        if (args.Length == 2 && args[0] == "--out")
            folder = args[1];
        else if (args.Length != 0)
        {
            Console.Error.WriteLine(Usage);
            return args is ["--help"] or ["-h"] ? 0 : 2;
        }

        Directory.CreateDirectory(folder);
        var jpeg = await ComposeScreenJpegAsync().ConfigureAwait(false);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // Les captures se font sur le fil de l'interface (celui-ci).
        var scratch = Path.Combine(Path.GetTempPath(), "trofeo-settings-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            RenderAll(folder, scratch, jpeg);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        return 0;
    }

    private static void RenderAll(string folder, string scratch, byte[] jpeg)
    {
        var connected = (Welcome: SampleData.Welcome(), Status: SampleData.Status());

        // Un onglet par image, sur le service branché.
        var tabs = new[] { "disposition", "personnalisation", "modules" };
        for (var i = 0; i < tabs.Length; i++)
        {
            var index = i;
            Render(folder, scratch, tabs[i] + ".png", 1440, 900, jpeg,
                link => link.Set(LinkState.Connected, connected.Welcome, connected.Status),
                (_, model) => model.SelectedTab = model.Tabs[index]);
        }

        Render(folder, scratch, "disposition-1200x760.png", 1200, 760, jpeg,
            link => link.Set(LinkState.Connected, connected.Welcome, connected.Status), (_, _) => { });

        RenderAppearance(folder, scratch, jpeg, connected.Welcome, connected.Status);
        RenderModules(folder, scratch, jpeg, connected.Welcome, connected.Status);
        RenderShortcuts(folder, scratch, jpeg, connected.Welcome, connected.Status);
        RenderSettings(folder, scratch, jpeg, connected.Welcome, connected.Status);
        RenderFirstRun(folder, scratch, jpeg, connected.Welcome, connected.Status);
        RenderCommunity(folder, scratch, jpeg, connected.Welcome, connected.Status);

        // L'onglet Disposition : un module choisi, des glissements (libre, refusé), l'ajout, l'oubli à confirmer.
        var layoutStatus = connected.Status with
        {
            Modules =
            [
                new ModuleState(ClaudeId, "claudecode", Trofeo.ModuleKit.ModuleHealth.Online, null, []),
                new ModuleState(MonitoringId, "monitoring", Trofeo.ModuleKit.ModuleHealth.Online, null, []),
                new ModuleState(NowPlayingId, "nowplaying", Trofeo.ModuleKit.ModuleHealth.NotConfigured, null, []),
                new ModuleState(ClockId, "clock", Trofeo.ModuleKit.ModuleHealth.Online, null, []),
                new ModuleState(NetworkId, "network", Trofeo.ModuleKit.ModuleHealth.Offline, null, []),
            ],
        };
        void Connected(FakeServiceLink link) => link.Set(LinkState.Connected, connected.Welcome, layoutStatus);

        Render(folder, scratch, "disposition-module-choisi.png", 1440, 900, jpeg, Connected,
            (_, model) => model.LayoutTab.Select(MonitoringId));

        Render(folder, scratch, "disposition-module-choisi-1200x760.png", 1200, 760, jpeg, Connected,
            (_, model) => model.LayoutTab.Select(ClaudeId));

        Render(folder, scratch, "disposition-glissement-refuse.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            model.LayoutTab.BeginMove(ClockId, CellCenter(12, 0));
            model.LayoutTab.UpdateDrag(CellCenter(9, 0));   // sur Monitoring PC
        });

        Render(folder, scratch, "disposition-glissement-libre.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            model.LayoutTab.BeginResize(ClaudeId, ResizeEdge.Right);
            model.LayoutTab.UpdateDrag(new Avalonia.Point(GridGeometry.ModuleRect(6, 0, 1, 1).Right + GridGeometry.Gap / 2.0, 200));   // Claude Code rétréci à 7 colonnes : libre
        });

        Render(folder, scratch, "disposition-glissement-trop-petit.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            model.LayoutTab.BeginResize(ClaudeId, ResizeEdge.Right);
            model.LayoutTab.UpdateDrag(CellCenter(3, 2));   // plus étroit que le minimum de Claude Code
        });

        Render(folder, scratch, "disposition-ajout.png", 1440, 900, jpeg, Connected, (window, _) =>
        {
            var add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddButton");
            add.Flyout!.ShowAt(add);
        });

        Render(folder, scratch, "disposition-oublier.png", 1440, 900, jpeg, Connected,
            (_, model) => model.LayoutTab.AskForget(model.LayoutTab.Shelved[0]));

        Render(folder, scratch, "disposition-message-du-service.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            model.LayoutTab.Select(ClockId);
            model.LayoutTab.SetTextSizeAsync(Trofeo.ModuleKit.TextSize.Large).GetAwaiter().GetResult();
        }, refuseReload: "Le service n'a pas pu appliquer la disposition : le fond choisi est introuvable.");

        Render(folder, scratch, "disposition-pixel.png", 1440, 900, jpeg, Connected,
            (_, model) => model.LayoutTab.Select(ClockId),
            layout: Base with { Appearance = new AppearanceSettings("pixel") });

        Render(folder, scratch, "mode-emule.png", 1440, 900, jpeg,
            link => link.Set(LinkState.Connected, connected.Welcome, SampleData.Status(LinkScreenState.Disconnected, emulated: true)),
            (_, _) => { });

        Render(folder, scratch, "ecran-non-branche.png", 1440, 900, jpeg,
            link => link.Set(LinkState.Connected, connected.Welcome, SampleData.Status(LinkScreenState.Disconnected)),
            (_, _) => { });

        Render(folder, scratch, "reprise-de-l-ecran.png", 1440, 900, jpeg,
            link => link.Set(LinkState.Connected, connected.Welcome, SampleData.Status(LinkScreenState.Recovering)),
            (_, _) => { });

        // Le focus au clavier : contour de 2 px en Accent (sur un onglet, puis sur le bouton principal).
        Render(folder, scratch, "focus-onglet.png", 1440, 900, jpeg,
            link => link.Set(LinkState.Connected, connected.Welcome, connected.Status),
            (window, _) => FocusByKeyboard(window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("tabs")).ContainerFromIndex(1)!));

        Render(folder, scratch, "focus-bouton.png", 1440, 900, null,
            link => link.Set(LinkState.ServiceStopped),
            (window, _) => FocusByKeyboard(window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible)));

        Render(folder, scratch, "service-arrete.png", 1440, 900, null,
            link => link.Set(LinkState.ServiceStopped), (_, _) => { });

        Render(folder, scratch, "service-introuvable.png", 1440, 900, null,
            link => { link.StartResult = false; link.Set(LinkState.ServiceStopped); },
            (_, model) => model.StartServiceCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        Render(folder, scratch, "connexion.png", 1440, 900, null,
            link => link.Set(LinkState.Connecting), (_, _) => { });
    }

    /// <summary>L'onglet Personnalisation : par défaut, une vidéo choisie (avec son conseil), des avertissements sous le réglage, un refus du service, la couleur.</summary>
    private static void RenderAppearance(string folder, string scratch, byte[] jpeg, Welcome welcome, ServiceStatus status)
    {
        void Connected(FakeServiceLink link) => link.Set(LinkState.Connected, welcome, status);
        void Open(MainViewModel model) => model.SelectedTab = model.AppearanceTab;
        static void Run(Task task) => task.GetAwaiter().GetResult();
        var fonts = new[] { "Arial", "Bahnschrift", "Calibri", "Cascadia Code", "Consolas", "Georgia", "Segoe UI", "Verdana" };

        Render(folder, scratch, "personnalisation.png", 1440, 900, jpeg, Connected, (_, model) => Open(model));
        Render(folder, scratch, "personnalisation-1200x760.png", 1200, 760, jpeg, Connected, (_, model) => Open(model));

        // Une vidéo choisie : « Choisir un fichier… », le nom du fichier et le conseil.
        Render(folder, scratch, "personnalisation-video.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            Open(model);
            model.AppearanceTab.Picker = new FakeFilePicker { Result = @"C:\Users\Alex\Videos\boucle-1920x462.mp4" };
            Run(model.AppearanceTab.SetBackgroundKindAsync(BackgroundKind.Video));
            Run(model.AppearanceTab.ChooseFileAsync());
        });

        // Un fond introuvable : l'avertissement du rechargement sous le choix du fond, en Amber.
        Render(folder, scratch, "personnalisation-avertissement-fond.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.OnReload = () => Task.FromResult(new Reloaded(true, null,
                    [@"Fond d'écran introuvable : C:\Users\Alex\Pictures\vacances.jpg. Fond du thème utilisé."]));
            },
            (_, model) =>
            {
                Open(model);
                model.AppearanceTab.Picker = new FakeFilePicker { Result = @"C:\Users\Alex\Pictures\vacances.jpg" };
                Run(model.AppearanceTab.SetBackgroundKindAsync(BackgroundKind.Image));
                Run(model.AppearanceTab.ChooseFileAsync());
            });

        // Une police absente (sous la liste), un thème choisi, une transparence imposée, la garde coupée.
        Render(folder, scratch, "personnalisation-police-absente.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.OnReload = () => Task.FromResult(new Reloaded(true, null,
                    ["Police « Comic Papyrus » introuvable : police du thème utilisée."]));
            },
            (_, model) =>
            {
                Open(model);
                Run(model.AppearanceTab.SetFontAsync(new FontItem("Comic Papyrus", "Comic Papyrus")));
            },
            layout: Base with { Appearance = new AppearanceSettings("liquid-glass", 0.4f, Legibility: false) },
            systemFonts: fonts);

        // Une couleur mal écrite, puis une couleur choisie dans la palette.
        Render(folder, scratch, "personnalisation-couleur.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            Open(model);
            Run(model.AppearanceTab.SetBackgroundKindAsync(BackgroundKind.Color));
            Run(model.AppearanceTab.SetColorTextAsync("#12345"));
        });
        Render(folder, scratch, "personnalisation-couleur-palette.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            Open(model);
            Run(model.AppearanceTab.SetColorAsync(model.AppearanceTab.Palette[5].Hex));
        });

        // Le thème Pixel, une police du système choisie.
        Render(folder, scratch, "personnalisation-pixel.png", 1440, 900, jpeg, Connected,
            (_, model) => Open(model),
            layout: Base with { Appearance = new AppearanceSettings("pixel", Font: "Consolas") },
            systemFonts: fonts);

        // Un refus du service : la ligne Amber sous la bande, ici dans l'onglet Personnalisation.
        Render(folder, scratch, "personnalisation-message-du-service.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            Open(model);
            Run(model.AppearanceTab.SetThemeAsync("pixel"));
        }, refuseReload: "Le service n'a pas pu appliquer le thème : le fond choisi est introuvable.");
    }

    /// <summary>L'onglet Modules : Réseau en UniFi (hôte, clé, action), Heure avec une ville et des résultats, Monitoring (lignes, cartes graphiques), En écoute sans réglage.</summary>
    private static void RenderModules(string folder, string scratch, byte[] jpeg, Welcome welcome, ServiceStatus status)
    {
        static void Run(Task task) => task.GetAwaiter().GetResult();
        static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        static DashboardLayout WithSettings(Guid id, string json) => Base with
        {
            Placements = [.. Base.Placements.Select(p => p.InstanceId == id ? p with { Settings = Json(json) } : p)],
        };
        static Trofeo.Control.ModuleState State(Guid id, string typeId, Trofeo.ModuleKit.ModuleHealth health, string? message = null, params string[] notices) =>
            new(id, typeId, health, message, notices);

        var modules = new[]
        {
            State(ClaudeId, "claudecode", Trofeo.ModuleKit.ModuleHealth.Online),
            State(MonitoringId, "monitoring", Trofeo.ModuleKit.ModuleHealth.Online, null, "3 lignes sur 5 tiennent à cette taille."),
            State(NowPlayingId, "nowplaying", Trofeo.ModuleKit.ModuleHealth.NotConfigured),
            State(ClockId, "clock", Trofeo.ModuleKit.ModuleHealth.Online),
            State(NetworkId, "network", Trofeo.ModuleKit.ModuleHealth.Offline, "Clé API refusée par la console : recopie-la depuis UniFi."),
        };
        void Connected(FakeServiceLink link) => link.Set(LinkState.Connected, welcome, status with { Modules = modules });
        void Open(MainViewModel model, Guid id)
        {
            model.SelectedTab = model.ModulesTab;
            model.ModulesTab.Select(id);
        }

        const string unifi = """{"source":"unifi","unifi":{"host":"192.168.1.1"}}""";

        // Réseau en UniFi : l'hôte, la clé (absente), l'action ; le message du module en Amber.
        Render(folder, scratch, "modules-reseau-unifi.png", 1440, 1240, jpeg, Connected, (_, model) => Open(model, NetworkId),
            layout: WithSettings(NetworkId, unifi));

        Render(folder, scratch, "modules-reseau-unifi-1440x900.png", 1440, 900, jpeg, Connected, (_, model) => Open(model, NetworkId),
            layout: WithSettings(NetworkId, unifi));

        // La clé en cours de saisie (masquée), enregistrée une première fois.
        var vault = new FakeSecretAccess();
        vault.Set("network", "unifi.apiKey", "valeur-deja-la");
        Render(folder, scratch, "modules-reseau-cle.png", 1440, 1240, jpeg, Connected, (_, model) =>
        {
            Open(model, NetworkId);
            var key = model.ModulesTab.Form!.Field<Trofeo.Settings.ViewModels.Fields.SecretFieldViewModel>("unifi.apiKey");
            key.Edit();
            key.Input = "a1b2c3d4e5f6";
        }, layout: WithSettings(NetworkId, unifi), secrets: vault);

        // La confirmation de l'action « Oublier le certificat ».
        Render(folder, scratch, "modules-reseau-confirmation.png", 1440, 1240, jpeg, Connected, (_, model) =>
        {
            Open(model, NetworkId);
            Run(model.ModulesTab.Form!.Field<Trofeo.Settings.ViewModels.Fields.ActionFieldViewModel>("forget-certificate").RequestAsync());
        }, layout: WithSettings(NetworkId, unifi), secrets: vault);

        // Réseau en « Ce PC » : seul le choix de la source.
        Render(folder, scratch, "modules-reseau-pc.png", 1440, 1240, jpeg, Connected, (_, model) => Open(model, NetworkId));

        // Heure : une ville choisie, des résultats de recherche ouverts (jusqu'à 5), le choix du vide en tête.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        Render(folder, scratch, "modules-heure-ville.png", 1440, 1240, jpeg,
            link =>
            {
                Connected(link);
                link.OnLookup = (_, _, _) => Task.FromResult<IReadOnlyList<Trofeo.ModuleKit.LookupResult>>(
                [
                    new("Lyon, Auvergne-Rhône-Alpes, France", Json("""{"name":"Lyon"}""")),
                    new("Lyon, Ontario, Canada", Json("""{"name":"Lyon"}""")),
                    new("Lyons, Illinois, États-Unis", Json("""{"name":"Lyons"}""")),
                ]);
            },
            (_, model) =>
            {
                Open(model, ClockId);
                var city = model.ModulesTab.Form!.Field<Trofeo.Settings.ViewModels.Fields.LookupFieldViewModel>("location");
                city.Query = "Lyon";
                time.Advance(TimeSpan.FromMilliseconds(400));
                Run(city.SearchTask);
            },
            layout: WithSettings(ClockId, """{"format":"12h","location":{"name":"Nantes","latitude":47.2,"longitude":-1.5}}"""), time: time);

        // Monitoring : le titre, les lignes (cochées d'abord, avec leurs flèches), les cartes graphiques à choisir.
        Render(folder, scratch, "modules-monitoring.png", 1440, 1240, jpeg,
            link =>
            {
                Connected(link);
                link.OnLookup = (_, _, _) => Task.FromResult<IReadOnlyList<Trofeo.ModuleKit.LookupResult>>(
                [
                    new("Intel(R) UHD Graphics 770", Json("\"Intel(R) UHD Graphics 770\"")),
                    new("NVIDIA GeForce RTX 5070", Json("\"NVIDIA GeForce RTX 5070\"")),
                ]);
            },
            (_, model) =>
            {
                Open(model, MonitoringId);
                Run(model.ModulesTab.Form!.Field<Trofeo.Settings.ViewModels.Fields.LookupFieldViewModel>("gpu").EnsureLoadedAsync());
            },
            layout: WithSettings(MonitoringId, """{"title":"Mon PC","rows":["gpu","cpu","vram","ram"],"gpu":"NVIDIA GeForce RTX 5070"}"""));

        Render(folder, scratch, "modules-monitoring-1200x760.png", 1200, 760, jpeg, Connected, (_, model) => Open(model, MonitoringId));

        // En écoute : rien à régler.
        Render(folder, scratch, "modules-en-ecoute.png", 1440, 1240, jpeg, Connected, (_, model) => Open(model, NowPlayingId));

        // Claude Code : choix, interrupteurs, et l'action du mod des quotas avec sa question.
        Render(folder, scratch, "modules-claude-code.png", 1440, 1240, jpeg, Connected, (_, model) =>
        {
            Open(model, ClaudeId);
            Run(model.ModulesTab.Form!.Field<Trofeo.Settings.ViewModels.Fields.ActionFieldViewModel>("install-quotas").RequestAsync());
        });

        // Un module retiré : ses réglages se changent, l'action attend qu'on le remette.
        Render(folder, scratch, "modules-retire.png", 1440, 1240, jpeg, Connected, (_, model) => Open(model, CodexId));

        // Un secret qui n'a pas pu relire : la ligne Amber de la fenêtre.
        Render(folder, scratch, "modules-message-de-la-fenetre.png", 1440, 1240, jpeg,
            link =>
            {
                Connected(link);
                link.OnRestart = _ => throw new ControlException("La liaison avec le service est coupée.");
            },
            (_, model) =>
            {
                Open(model, NetworkId);
                var key = model.ModulesTab.Form!.Field<Trofeo.Settings.ViewModels.Fields.SecretFieldViewModel>("unifi.apiKey");
                key.Edit();
                key.Input = "a1b2c3d4e5f6";
                Run(key.SaveAsync());
            },
            layout: WithSettings(NetworkId, unifi), secrets: new FakeSecretAccess());
    }


    /// <summary>L'onglet Raccourcis : la liste, une capture en cours, un doublon refusé, un raccourci qu'on ajoute, un refus de Windows.</summary>
    private static void RenderShortcuts(string folder, string scratch, byte[] jpeg, Welcome welcome, ServiceStatus status)
    {
        void Connected(FakeServiceLink link) => link.Set(LinkState.Connected, welcome, status);
        void Open(MainViewModel model) => model.SelectedTab = model.ShortcutsTab;
        static void Run(Task task) => task.GetAwaiter().GetResult();

        // Claude Code et Codex côte à côte : tous deux acceptent les raccourcis.
        var twoAgents = Base with
        {
            Placements =
            [
                Base.Placements[0] with { Column = 0, Columns = 8 },
                new Trofeo.Core.Layout.ModulePlacement(CodexId, "codex", 8, 0, 8, 4, Trofeo.ModuleKit.TextSize.Normal,
                    System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()),
            ],
            Shelf = [],
            Shortcuts =
            [
                .. Trofeo.Core.Shortcuts.ShortcutDefaults.All.Take(2),
                new Trofeo.Core.Shortcuts.ShortcutBinding("Ctrl+Alt+Shift+1", Trofeo.ModuleKit.ModuleCommandKind.SelectTab, 0, CodexId),
                new Trofeo.Core.Shortcuts.ShortcutBinding("Ctrl+Alt+Shift+2", Trofeo.ModuleKit.ModuleCommandKind.SelectTab, 1, CodexId),
                new Trofeo.Core.Shortcuts.ShortcutBinding("Ctrl+F5", Trofeo.ModuleKit.ModuleCommandKind.NextTab, 0, Guid.NewGuid()),
            ],
        };

        Render(folder, scratch, "raccourcis.png", 1440, 900, jpeg, Connected, (_, model) => Open(model));
        Render(folder, scratch, "raccourcis-1200x760.png", 1200, 760, jpeg, Connected, (_, model) => Open(model));
        Render(folder, scratch, "raccourcis-liste-complete.png", 1440, 1240, jpeg, Connected, (_, model) => Open(model));
        Render(folder, scratch, "raccourcis-cibles.png", 1440, 900, jpeg, Connected, (_, model) => Open(model), layout: twoAgents);

        // « Modifier » : la ligne attend la combinaison.
        Render(folder, scratch, "raccourcis-capture.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            Open(model);
            model.ShortcutsTab.BeginCapture(model.ShortcutsTab.Rows[1]);
        });

        // Une combinaison déjà prise : refusée avec son motif, la capture continue.
        Render(folder, scratch, "raccourcis-doublon.png", 1440, 900, jpeg, Connected, (_, model) =>
        {
            Open(model);
            model.ShortcutsTab.BeginCapture(model.ShortcutsTab.Rows[0]);
            Run(model.ShortcutsTab.CaptureAsync(Avalonia.Input.Key.Right, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt));
        });

        // « Ajouter un raccourci » : l'action se choisit, la combinaison s'attend.
        Render(folder, scratch, "raccourcis-ajout.png", 1440, 1240, jpeg, Connected, (_, model) =>
        {
            Open(model);
            model.ShortcutsTab.AddShortcut();
        });

        // Windows a refusé des raccourcis au rechargement : le texte du service, sur la ligne.
        Render(folder, scratch, "raccourcis-refus-de-windows.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.OnReload = () => Task.FromResult(new Reloaded(true, null,
                [
                    "Raccourci « Ctrl+Alt+Right » indisponible : déjà pris par une autre application.",
                    "Raccourci « Ctrl+F5 » indisponible : touches illisibles.",
                ]));
            },
            (_, model) =>
            {
                Open(model);
                Run(model.ShortcutsTab.SetTargetAsync(model.ShortcutsTab.Rows[0], model.ShortcutsTab.Rows[0].Targets[1]));
            }, layout: twoAgents);

        Render(folder, scratch, "raccourcis-vide.png", 1440, 900, jpeg, Connected, (_, model) => Open(model),
            layout: Base with { Shortcuts = [] });
    }

    /// <summary>L'onglet Paramètres : capteurs à installer, capteurs actifs, installation refusée, démarrage introuvable, import refusé.</summary>
    private static void RenderSettings(string folder, string scratch, byte[] jpeg, Welcome welcome, ServiceStatus status)
    {
        static void Run(Task task) => task.GetAwaiter().GetResult();
        void Open(MainViewModel model) => model.SelectedTab = model.SettingsTab;

        ServiceStatus Sensors(SensorInfo sensors, ProcessInfo? process = null) =>
            status with { Sensors = sensors, Process = process ?? new ProcessInfo(46_137_344, 60_000_000, 0.83) };
        void Link(FakeServiceLink link, SensorInfo sensors) => link.Set(LinkState.Connected, welcome, Sensors(sensors));

        // Windows de démonstration : le service est trouvé, le démarrage n'est pas encore inscrit.
        SystemServices System(bool displayFound = true, bool accept = true, bool startupOn = false) => new(
            new FakeStartupRegistration { IsEnabled = startupOn }, new FakeElevatedLauncher { Accept = accept },
            () => displayFound ? @"C:\Trofeo\Trofeo.Display.exe" : null, () => @"C:\Trofeo\Trofeo.SensorHelper.exe", () => 96L * 1024 * 1024);

        var notInstalled = new SensorInfo(false, false, false);

        Render(folder, scratch, "parametres.png", 1440, 900, jpeg, link => Link(link, notInstalled), (_, model) => Open(model), system: System());
        Render(folder, scratch, "parametres-1200x760.png", 1200, 760, jpeg, link => Link(link, notInstalled), (_, model) => Open(model), system: System());
        Render(folder, scratch, "parametres-complet.png", 1440, 1240, jpeg, link => Link(link, notInstalled), (_, model) => Open(model), system: System());

        // Retourné, 8 images par seconde, démarrage avec Windows allumé, capteurs actifs, rendu réel.
        Render(folder, scratch, "parametres-actifs.png", 1440, 1240, jpeg, link => Link(link, new SensorInfo(true, true, true)),
            (_, model) =>
            {
                Open(model);
                model.SettingsTab.RealTimePreview = true;
            },
            layout: Base with { Rotation = 0, MaxFps = 8 }, system: System(startupOn: true));

        // Le pilote est là, mais l'assistant ne tourne pas ; l'autorisation de Windows est refusée.
        Render(folder, scratch, "parametres-installation-refusee.png", 1440, 900, jpeg, link => Link(link, new SensorInfo(true, false, false)),
            (_, model) =>
            {
                Open(model);
                Run(model.SettingsTab.InstallSensorsAsync());
            }, system: System(accept: false));

        // Il manque le pilote : l'installeur est demandé d'abord, puis l'installation part.
        Render(folder, scratch, "parametres-installation-lancee.png", 1440, 900, jpeg, link => Link(link, notInstalled),
            (_, model) =>
            {
                Open(model);
                model.SettingsTab.Picker = new FakeFilePicker { Result = @"C:\Users\Alex\Downloads\PawnIO_setup.exe" };
                Run(model.SettingsTab.InstallSensorsAsync());
            }, system: System());

        // Trofeo.Display.exe introuvable : l'interrupteur est éteint et grisé, avec sa phrase.
        Render(folder, scratch, "parametres-service-introuvable.png", 1440, 900, jpeg, link => Link(link, notInstalled),
            (_, model) => Open(model), system: System(displayFound: false));

        // Mises à jour : une bêta est proposée ; l'autorisation de Windows refusée ; aucune stable ; le retour de la bêta à la stable et sa boîte.
        UpdateInfo Update(LinkUpdateState state, string? version, string? message, LinkUpdateChannel channel = LinkUpdateChannel.Stable, bool downgrade = false) =>
            new(state, version, downgrade, null, message, channel, false);
        void Updates(FakeServiceLink link, UpdateInfo info)
        {
            Link(link, new SensorInfo(true, true, true));
            link.Updates = info;
        }

        Render(folder, scratch, "parametres-mise-a-jour.png", 1440, 1520, jpeg,
            link => Updates(link, Update(LinkUpdateState.Available, "1.0.0-beta.3", "La version 1.0.0-beta.3 est disponible.", LinkUpdateChannel.Beta)),
            (_, model) => Open(model), system: System());

        Render(folder, scratch, "parametres-mise-a-jour-annulee.png", 1440, 1520, jpeg,
            link =>
            {
                Updates(link, Update(LinkUpdateState.Available, "1.0.0-beta.3", "La version 1.0.0-beta.3 est disponible.", LinkUpdateChannel.Beta));
                link.OnInstallUpdate = _ => Task.FromResult(new Trofeo.ModuleKit.ActionResult(false, "Mise à jour annulée : l'autorisation n'a pas été donnée."));
            },
            (_, model) =>
            {
                Open(model);
                Run(model.SettingsTab.InstallUpdateAsync());
            }, system: System());

        Render(folder, scratch, "parametres-aucune-stable.png", 1440, 1520, jpeg,
            link => Updates(link, Update(LinkUpdateState.NoStableYet, null, "Aucune version stable pour l'instant.")),
            (_, model) => Open(model), system: System());

        Render(folder, scratch, "parametres-retour-stable.png", 1440, 1520, jpeg,
            link =>
            {
                Updates(link, Update(LinkUpdateState.UpToDate, null, "Tu as la dernière version.", LinkUpdateChannel.Beta));
                link.OnSetUpdatePreferences = (channel, wait) =>
                {
                    link.SetUpdates(Update(LinkUpdateState.Available, "1.0.0", "La version 1.0.0 est disponible.", channel, downgrade: true));
                    return Task.CompletedTask;
                };
            },
            (_, model) =>
            {
                Open(model);
                Run(model.SettingsTab.SetChannelAsync(LinkUpdateChannel.Stable));
            }, system: System());

        // Un fichier qui n'est pas une disposition : son motif, sous les boutons.
        var invalid = Path.Combine(scratch, "pas-une-disposition.json");
        Render(folder, scratch, "parametres-import-refuse.png", 1440, 900, jpeg, link => Link(link, new SensorInfo(true, true, true)),
            (_, model) =>
            {
                Open(model);
                File.WriteAllText(invalid, "{}");
                model.SettingsTab.Picker = new FakeFilePicker { Result = invalid };
                Run(model.SettingsTab.ImportAsync());
            }, system: System());

        // Une disposition exportée.
        Render(folder, scratch, "parametres-export-reussi.png", 1440, 900, jpeg, link => Link(link, new SensorInfo(true, true, true)),
            (_, model) =>
            {
                Open(model);
                model.SettingsTab.Picker = new FakeFilePicker { SaveResult = Path.Combine(scratch, "ma-disposition.trofeo.json") };
                Run(model.SettingsTab.ExportAsync());
            }, system: System());
    }

    /// <summary>L'assistant de première ouverture : chaque étape, et les états qui changent ce qu'elle dit.</summary>
    private static void RenderFirstRun(string folder, string scratch, byte[] jpeg, Welcome welcome, ServiceStatus status)
    {
        static void Run(Task task) => task.GetAwaiter().GetResult();
        static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        var firstRun = new EditorPreferences(FirstRunDone: false);

        void Connected(FakeServiceLink link) => link.Set(LinkState.Connected, welcome, status);
        void Busy(FakeServiceLink link) => link.Set(LinkState.Connected, welcome,
            status with { Screen = new ScreenInfo(LinkScreenState.Disconnected, "Écran utilisé par un autre programme (ferme TRCC)") });
        void Missing(FakeServiceLink link) => link.Set(LinkState.Connected, welcome,
            status with { Screen = new ScreenInfo(LinkScreenState.Disconnected, null) });
        void Emulated(FakeServiceLink link) => link.Set(LinkState.Connected, welcome,
            status with { Screen = new ScreenInfo(LinkScreenState.Disconnected, null), Emulated = true });
        void NoDriver(FakeServiceLink link) => link.Set(LinkState.Connected, welcome, status with { Sensors = new SensorInfo(false, false, false) });
        SystemServices Windows(bool fails = false) => new(
            new FakeStartupRegistration { Failure = fails ? "Accès refusé" : null }, new FakeElevatedLauncher(),
            () => @"C:\Trofeo\Trofeo.Display.exe", () => @"C:\Trofeo\Trofeo.SensorHelper.exe", () => 96L * 1024 * 1024);
        void To(MainViewModel model, FirstRunStep step)
        {
            for (var guard = 0; model.FirstRun.Step != step && guard < 20; guard++)
                model.FirstRun.Skip();
        }

        void Shot(string name, int width, int height, Action<FakeServiceLink> state, FirstRunStep step,
            Action<MainViewModel>? more = null, DashboardLayout? layout = null, SystemServices? system = null) =>
            Render(folder, scratch, name, width, height, jpeg, state, (_, model) =>
            {
                To(model, step);
                more?.Invoke(model);
            }, layout: layout, system: system ?? Windows(), preferences: firstRun);

        Shot("assistant-1-ecran.png", 1440, 900, Connected, FirstRunStep.Screen);
        Shot("assistant-1-ecran-trcc.png", 1440, 900, Busy, FirstRunStep.Screen);
        Shot("assistant-1-ecran-debranche.png", 1440, 900, Missing, FirstRunStep.Screen);
        Shot("assistant-1-ecran-emule.png", 1440, 900, Emulated, FirstRunStep.Screen);
        Shot("assistant-2-sens.png", 1440, 900, Connected, FirstRunStep.Orientation);
        Shot("assistant-3-modules.png", 1440, 900, Connected, FirstRunStep.Modules);
        Shot("assistant-3-modules-1200x760.png", 1200, 760, Connected, FirstRunStep.Modules);

        // Plus de place : Claude Code occupe toute la largeur, Codex ne tient pas.
        var full = Base with { Placements = [Base.Placements[0] with { Columns = 16 }] };
        Shot("assistant-3-modules-sans-place.png", 1440, 900, Connected, FirstRunStep.Modules,
            model => Run(model.FirstRun.SetModuleAsync("codex", true)), full);

        Shot("assistant-4-claude.png", 1440, 900, Connected, FirstRunStep.ClaudeCode);
        Shot("assistant-4-claude-confirmation.png", 1440, 900, Connected, FirstRunStep.ClaudeCode,
            model => Run(model.FirstRun.ClaudeForm!.VisibleFields.OfType<Trofeo.Settings.ViewModels.Fields.ActionFieldViewModel>().Single().RequestAsync()));
        Shot("assistant-4-claude-confirmation-1200x760.png", 1200, 760, Connected, FirstRunStep.ClaudeCode,
            model => Run(model.FirstRun.ClaudeForm!.VisibleFields.OfType<Trofeo.Settings.ViewModels.Fields.ActionFieldViewModel>().Single().RequestAsync()));

        Shot("assistant-5-reseau.png", 1440, 900, Connected, FirstRunStep.Network);
        var unifi = Base with
        {
            Placements = [.. Base.Placements.Select(p => p.TypeId == "network" ? p with { Settings = Json("""{"source":"unifi","unifi":{"host":"192.168.1.1"}}""") } : p)],
        };
        Shot("assistant-5-reseau-unifi.png", 1440, 900, Connected, FirstRunStep.Network, layout: unifi);
        Shot("assistant-5-reseau-unifi-1200x760.png", 1200, 760, Connected, FirstRunStep.Network, layout: unifi);

        Shot("assistant-6-meteo.png", 1440, 900, Connected, FirstRunStep.Weather);
        Shot("assistant-7-temperatures.png", 1440, 900, NoDriver, FirstRunStep.Sensors);
        Shot("assistant-7-temperatures-actives.png", 1440, 900, Connected, FirstRunStep.Sensors);
        Shot("assistant-8-demarrage.png", 1440, 900, Connected, FirstRunStep.Startup);
        Shot("assistant-8-demarrage-1200x760.png", 1200, 760, Connected, FirstRunStep.Startup);
        Shot("assistant-8-demarrage-refus.png", 1440, 900, Connected, FirstRunStep.Startup,
            model => Run(model.FirstRun.ContinueAsync()), system: Windows(fails: true));

        // Sans Claude Code : sept étapes, et la quatrième est Réseau.
        var withoutClaude = Base with { Placements = [.. Base.Placements.Where(p => p.TypeId != "claudecode")] };
        Shot("assistant-sans-claude-reseau.png", 1440, 900, Connected, FirstRunStep.Network, layout: withoutClaude);
    }

    /// <summary>
    /// Les modules complémentaires : la section de l'onglet Paramètres, la boîte (installer, erreurs, valider, mise à jour avec ses
    /// nouvelles permissions, un nom démesuré), le point d'exclamation jaune dans les listes, l'en-tête et un angle de la case, et les
    /// champs numérique et de port d'un module.
    /// </summary>
    private static void RenderCommunity(string folder, string scratch, byte[] jpeg, Welcome welcome, ServiceStatus status)
    {
        static void Run(Task task) => task.GetAwaiter().GetResult();
        static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        const string path = @"C:\Users\Alex\Downloads\alex.minecraft-1.0.0.avmod";
        const string devPath = @"C:\Users\Alex\dev\horloge-mondiale";
        const string oldDevPath = @"C:\Users\Alex\dev\ancien-essai";

        var community = new CommunityTypeInfo(
            "Alex", "Surveille ton serveur Minecraft : joueurs connectés et latence.", "1.0.0",
            [AshboardVision.Sdk.ModulePermission.ThisComputer, AshboardVision.Sdk.ModulePermission.Internet],
            [AshboardVision.Sdk.ModulePermission.ThisComputer]);
        var minecraft = new ModuleTypeInfo(
            "alex.minecraft", "Serveur Minecraft", new Trofeo.ModuleKit.GridSize(3, 1),
            new Dictionary<Trofeo.ModuleKit.SizePreset, Trofeo.ModuleKit.GridSize> { [Trofeo.ModuleKit.SizePreset.Ideal] = new(4, 2) }, false,
            new Trofeo.ModuleKit.SettingsSchema(
            [
                new Trofeo.ModuleKit.TextField("name", "Nom affiché") { Placeholder = "Mon serveur" },
                new Trofeo.ModuleKit.PortField("port", "Port du serveur", 25565) { Help = "Le port sur lequel ton serveur écoute sur ce PC." },
                new Trofeo.ModuleKit.NumberField("refresh", "Actualiser toutes les (secondes)", 5, 300, 30),
            ]),
            community);
        var layoutWith = Base with
        {
            Placements =
            [
                .. Base.Placements.Where(p => p.TypeId != "clock"),
                new ModulePlacement(CommunityFixture.DemoId, "alex.minecraft", 12, 0, 4, 2, Trofeo.ModuleKit.TextSize.Normal, Json("""{"port":25565}""")),
            ],
            Shelf = [],
        };
        IReadOnlyList<ModuleTypeInfo> types = [.. SampleData.Types(), minecraft];
        void Connected(FakeServiceLink link)
        {
            link.Set(LinkState.Connected, welcome, status);
            link.SetTypes(types);
            link.Consumption.Add(new ModuleConsumption("originaux", "Modules d'origine (dans le service)", false, 0.8, 44L * 1024 * 1024, false));
            link.Package = CommunityFixture.Package(
                id: "alex.minecraft", name: "Serveur Minecraft", author: "Alex", version: "1.0.0", description: "Surveille ton serveur Minecraft : joueurs connectés et latence.",
                declared: [AshboardVision.Sdk.ModulePermission.ThisComputer, AshboardVision.Sdk.ModulePermission.Internet, AshboardVision.Sdk.ModulePermission.Sensors]);
        }

        CommunityModuleInfo Module(string id, string name, string author, LinkCommunityState state = LinkCommunityState.Ready, string? message = null,
            string? update = null, string? notes = null, string version = "1.0.0", bool dev = false, string? devPath = null) =>
            CommunityFixture.Module(id, state, version, [AshboardVision.Sdk.ModulePermission.ThisComputer, AshboardVision.Sdk.ModulePermission.Internet],
                [AshboardVision.Sdk.ModulePermission.ThisComputer], update, notes, message, dev, name, author, "Un module de la communauté.", devPath);
        var modules = new[]
        {
            Module("alex.minecraft", "Serveur Minecraft", "Alex", update: "1.1.0", notes: "Le nombre de joueurs se met à jour toutes les 5 secondes."),
            Module("bob.meteo", "Météo détaillée", "Bob", LinkCommunityState.SetAside, "Il s'est arrêté trois fois en dix minutes : relance-le quand tu auras vu pourquoi.", version: "2.3.1"),
            Module("carl.horloge", "Horloge mondiale", "Carl", LinkCommunityState.NeedsApproval, "À valider dans la fenêtre de réglage", version: "0.4.0"),
            Module("dora.dev", "Module en cours d'écriture", "Dora", dev: true, version: "0.0.1", devPath: devPath),
        };
        void Listed(FakeServiceLink link)
        {
            Connected(link);
            link.Community = new CommunityOverview(modules, @"C:\Users\Alex\Documents\Ashboard Vision\Modules", true, [devPath, oldDevPath], false);
            // Les mesures : les modules d'origine, un module léger, un module gourmand.
            link.Consumption.Add(new ModuleConsumption("alex.minecraft", "Serveur Minecraft", true, 0.3, 61L * 1024 * 1024, false));
            link.Consumption.Add(new ModuleConsumption("dora.dev", "Module en cours d'écriture", true, 3.4, 212L * 1024 * 1024, true));
            // Le dossier de travail déclare maintenant une permission de plus (Capteurs) que celles accordées.
            link.DevFolder = new PackageInfo("dora.dev", "Module en cours d'écriture", "Dora", "0.0.2", "Un module de la communauté.",
                [AshboardVision.Sdk.ModulePermission.ThisComputer, AshboardVision.Sdk.ModulePermission.Internet, AshboardVision.Sdk.ModulePermission.Sensors], [], null, null);
        }

        void OpenSettings(MainViewModel model) => model.SelectedTab = model.SettingsTab;

        // La section : un module actif avec une mise à jour proposée, un module mis de côté, un module à valider, un dossier de développement.
        Render(folder, scratch, "parametres-modules-complementaires.png", 1440, 2600, jpeg, Listed, (_, model) => OpenSettings(model), layout: layoutWith);

        Render(folder, scratch, "parametres-modules-complementaires-1200x760.png", 1200, 760, jpeg, Listed, (_, model) => OpenSettings(model), layout: layoutWith);

        Render(folder, scratch, "parametres-modules-complementaires-desinstaller.png", 1440, 2600, jpeg, Listed, (_, model) =>
        {
            OpenSettings(model);
            model.SettingsTab.Community.Modules[0].AskUninstallCommand.Execute(null);
        }, layout: layoutWith);

        Render(folder, scratch, "parametres-modules-complementaires-vide.png", 1440, 1900, jpeg, Connected, (_, model) => OpenSettings(model));

        Render(folder, scratch, "parametres-modules-complementaires-liste-alteree.png", 1440, 2000, jpeg,
            link =>
            {
                Listed(link);
                link.Community = link.Community with { StoreTampered = true, Modules = [Module("alex.minecraft", "Serveur Minecraft", "Alex", LinkCommunityState.NeedsApproval, "À valider dans la fenêtre de réglage")] };
            },
            (_, model) => OpenSettings(model));

        Render(folder, scratch, "parametres-modules-complementaires-refus.png", 1440, 2600, jpeg,
            link =>
            {
                Listed(link);
                link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Le dossier du module est utilisé par un autre programme : ferme-le puis recommence.");
            },
            (_, model) =>
            {
                OpenSettings(model);
                Run(model.SettingsTab.Community.Modules[0].ConfirmUninstallCommand.ExecuteAsync(null));
            }, layout: layoutWith);

        // Le mode développeur éteint : ni liste de dossiers, ni bandeau ; le dossier des modules et la consommation restent.
        Render(folder, scratch, "parametres-mode-developpeur-eteint.png", 1440, 2000, jpeg,
            link =>
            {
                Listed(link);
                link.Community = link.Community with { DeveloperMode = false, DevFolders = [], Modules = [.. modules.Where(m => !m.FromDevFolder)] };
            },
            (_, model) => OpenSettings(model), layout: layoutWith);

        // Le dossier des modules changé vers un dossier que le service refuse : son message tel quel, la section reste.
        Render(folder, scratch, "parametres-dossier-refus.png", 1440, 2600, jpeg,
            link =>
            {
                Listed(link);
                link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Choisis un dossier, pas la racine d'un disque.");
            },
            (_, model) =>
            {
                OpenSettings(model);
                Run(model.SettingsTab.Community.ChangeModulesFolderAsync());
            }, layout: layoutWith, picker: new FakeFilePicker { FolderResult = @"C:\" });

        // La consommation repliée : plus aucune mesure demandée.
        Render(folder, scratch, "parametres-consommation-repliee.png", 1440, 2600, jpeg, Listed,
            (_, model) =>
            {
                OpenSettings(model);
                model.SettingsTab.Community.Consumption.ToggleCommand.Execute(null);
            }, layout: layoutWith);

        // Le bandeau du mode développeur, dans un autre onglet.
        Render(folder, scratch, "disposition-mode-developpeur.png", 1440, 900, jpeg, Listed, (_, _) => { }, layout: layoutWith);

        // Ajouter un dossier de travail : le même avertissement et les mêmes cases qu'une installation, décochées ; le titre dit le mode.
        Render(folder, scratch, "dialogue-dossier-travail.png", 1440, 900, jpeg,
            link =>
            {
                Listed(link);
                link.DevFolder = new PackageInfo(
                    "alex.horloge", "Horloge mondiale", "Alex", "0.1.0", "Plusieurs fuseaux horaires sur une même case.",
                    [AshboardVision.Sdk.ModulePermission.Internet, AshboardVision.Sdk.ModulePermission.Sensors], [], null, null);
            },
            (_, model) =>
            {
                OpenSettings(model);
                Run(model.SettingsTab.Community.AddDevFolderAsync());
            }, picker: new FakeFilePicker { FolderResult = @"C:\Users\Alex\dev\nouveau-module" });

        Render(folder, scratch, "dialogue-dossier-travail-erreurs.png", 1440, 900, jpeg,
            link =>
            {
                Listed(link);
                link.DevFolder = new PackageInfo(
                    "alex.minecraft", "Horloge mondiale", "Alex", "0.1.0", "x", [AshboardVision.Sdk.ModulePermission.Internet],
                    ["Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant."], null, null);
            },
            (_, model) =>
            {
                OpenSettings(model);
                Run(model.SettingsTab.Community.AddDevFolderAsync());
            }, picker: new FakeFilePicker { FolderResult = @"C:\Users\Alex\dev\copie" });

        // Recharger un module dont le dossier demande une permission de plus : l'accordé est coché, la nouvelle décochée et signalée.
        Render(folder, scratch, "dialogue-dossier-travail-recharger.png", 1440, 900, jpeg, Listed, (_, model) =>
        {
            OpenSettings(model);
            Run(model.SettingsTab.Community.DevFolders[0].ReloadCommand.ExecuteAsync(null));
        });

        // La boîte d'installation : l'avertissement, les trois permissions déclarées, décochées.
        Render(folder, scratch, "dialogue-installation.png", 1440, 900, jpeg, Connected, (_, model) =>
            Run(model.SettingsTab.Community.OpenPackageAsync(path)));

        Render(folder, scratch, "dialogue-installation-1200x760.png", 1200, 760, jpeg, Connected, (_, model) =>
        {
            Run(model.SettingsTab.Community.OpenPackageAsync(path));
            model.SettingsTab.Community.Dialog!.Permissions[0].IsChecked = true;
        });

        Render(folder, scratch, "dialogue-installation-erreurs.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.Package = CommunityFixture.Package(
                    id: "alex.minecraft", name: "Serveur Minecraft", errors: ["Le paquet contient plus de 1 000 fichiers.", @"Chemin refusé : ..\evil.dll."],
                    conflict: "Un autre module porte déjà cet identifiant. Désinstalle-le d'abord.");
            },
            (_, model) => Run(model.SettingsTab.Community.OpenPackageAsync(path)));

        Render(folder, scratch, "dialogue-installation-refus.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Un autre module porte déjà cet identifiant.");
            },
            (_, model) =>
            {
                Run(model.SettingsTab.Community.OpenPackageAsync(path));
                Run(model.SettingsTab.Community.Dialog!.ConfirmAsync());
            });

        Render(folder, scratch, "dialogue-validation.png", 1440, 900, jpeg, Listed, (_, model) =>
        {
            OpenSettings(model);
            model.SettingsTab.Community.Modules[2].ApproveCommand.Execute(null);
        });

        Render(folder, scratch, "dialogue-permissions.png", 1440, 900, jpeg, Listed, (_, model) =>
        {
            OpenSettings(model);
            model.SettingsTab.Community.Modules[0].PermissionsCommand.Execute(null);
        });

        // La mise à jour en deux temps : le téléchargement (version et notes de la liste), puis les notes du paquet et les cases.
        var gate = new TaskCompletionSource<ModuleUpdatePreparation>();
        Render(folder, scratch, "dialogue-mise-a-jour-telechargement.png", 1440, 900, jpeg,
            link =>
            {
                Listed(link);
                link.OnPrepare = _ => gate.Task;
            },
            (_, model) =>
            {
                OpenSettings(model);
                var downloading = model.SettingsTab.Community.Modules[0].UpdateCommand.ExecuteAsync(null); // le téléchargement dure : on le photographie en route
                GC.KeepAlive(downloading);
            });

        Render(folder, scratch, "dialogue-mise-a-jour.png", 1440, 900, jpeg,
            link =>
            {
                Listed(link);
                link.Preparation = new ModuleUpdatePreparation(
                    true, null, "1.1.0", "Le nombre de joueurs se met à jour toutes les 5 secondes.\nNouveau : la latence s'affiche en millisecondes.",
                    [AshboardVision.Sdk.ModulePermission.ThisComputer, AshboardVision.Sdk.ModulePermission.Internet, AshboardVision.Sdk.ModulePermission.NowPlaying],
                    [AshboardVision.Sdk.ModulePermission.NowPlaying]);
            },
            (_, model) =>
            {
                OpenSettings(model);
                Run(model.SettingsTab.Community.Modules[0].UpdateCommand.ExecuteAsync(null));
            });

        Render(folder, scratch, "dialogue-mise-a-jour-refus.png", 1440, 900, jpeg,
            link =>
            {
                Listed(link);
                link.Preparation = new ModuleUpdatePreparation(false, "L'empreinte du paquet ne correspond pas à celle publiée : téléchargement abandonné.", null, null, [], []);
            },
            (_, model) =>
            {
                OpenSettings(model);
                Run(model.SettingsTab.Community.Modules[0].UpdateCommand.ExecuteAsync(null));
            });

        // Un nom, un auteur et une description démesurés : la mise en page tient.
        var longName = "Un-nom-de-module-sans-aucun-espace-" + new string('x', 300);
        Render(folder, scratch, "dialogue-nom-tres-long.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.Package = CommunityFixture.Package(
                    id: "alex.minecraft", name: longName, author: "Un auteur très " + new string('b', 200),
                    description: string.Join(' ', Enumerable.Repeat("Une description vraiment très longue qui n'en finit pas.", 40)));
            },
            (_, model) => Run(model.SettingsTab.Community.OpenPackageAsync(path)));

        Render(folder, scratch, "parametres-modules-complementaires-nom-tres-long.png", 1440, 2000, jpeg,
            link =>
            {
                Listed(link);
                link.Community = link.Community with { Modules = [Module("alex.minecraft", longName, "Un auteur très " + new string('b', 200), update: "1.1.0")] };
            },
            (_, model) => OpenSettings(model));

        // Le point d'exclamation jaune : dans un angle de la case, dans la liste « Ajouter un module » et dans la liste des modules placés.
        Render(folder, scratch, "disposition-module-complementaire.png", 1440, 900, jpeg, Connected,
            (_, model) => model.LayoutTab.Select(CommunityFixture.DemoId), layout: layoutWith);

        Render(folder, scratch, "disposition-ajout-complementaire.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.SetTypes([.. SampleData.Types(), minecraft, CommunityFixture.DemoType("bob.meteo", longName[..60], "Bob")]);
            },
            (window, _) =>
            {
                var add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddButton");
                add.Flyout!.ShowAt(add);
            }, layout: layoutWith);

        // Le nom démesuré dans les listes de l'onglet Disposition.
        Render(folder, scratch, "disposition-nom-tres-long.png", 1440, 900, jpeg,
            link =>
            {
                Connected(link);
                link.SetTypes([.. SampleData.Types(), minecraft with { DisplayName = longName }]);
            },
            (_, model) => model.LayoutTab.Select(CommunityFixture.DemoId), layout: layoutWith);

        // Les réglages d'un module complémentaire : l'en-tête avec le point d'exclamation, la mention, les permissions accordées, le port et le nombre.
        Render(folder, scratch, "modules-complementaire.png", 1440, 1240, jpeg, Connected,
            (_, model) =>
            {
                model.SelectedTab = model.ModulesTab;
                model.ModulesTab.Select(CommunityFixture.DemoId);
            }, layout: layoutWith);

        Render(folder, scratch, "modules-complementaire-port-invalide.png", 1440, 1240, jpeg, Connected,
            (_, model) =>
            {
                model.SelectedTab = model.ModulesTab;
                model.ModulesTab.Select(CommunityFixture.DemoId);
                var port = model.ModulesTab.Form!.Field<Trofeo.Settings.ViewModels.Fields.NumberFieldViewModel>("port");
                port.Text = "70000";
                Run(port.CommitAsync());
            }, layout: layoutWith);

        Render(folder, scratch, "modules-complementaire-nom-tres-long.png", 1440, 1240, jpeg,
            link =>
            {
                Connected(link);
                link.SetTypes([.. SampleData.Types(), minecraft with { DisplayName = longName, Community = community with { Author = "Un auteur très " + new string('b', 200), Description = string.Join(' ', Enumerable.Repeat("Une description vraiment très longue.", 40)) } }]);
            },
            (_, model) =>
            {
                model.SelectedTab = model.ModulesTab;
                model.ModulesTab.Select(CommunityFixture.DemoId);
            }, layout: layoutWith);
    }

    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid MonitoringId = new("246ac06d-eec2-4d64-9930-24b64603a03b");
    private static readonly Guid NowPlayingId = new("8ebcc647-ab4b-401d-b2b3-f7a0ebd97b4b");
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");
    private static readonly Guid NetworkId = new("a2002423-c807-4403-9c5a-2a983fb6a371");
    private static readonly Guid CodexId = new("c0de0000-0000-4000-8000-000000000001");

    /// <summary>La disposition par défaut (celle de la bande), avec Codex rangé parmi les modules retirés.</summary>
    private static readonly DashboardLayout Base = DashboardLayout.Default with
    {
        Shelf =
        [
            new ModulePlacement(new Guid("c0de0000-0000-4000-8000-000000000001"), "codex", 0, 0, 8, 4,
                Trofeo.ModuleKit.TextSize.Normal, System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()),
        ],
    };

    /// <summary>Le centre d'une case, en pixels de l'écran.</summary>
    private static Avalonia.Point CellCenter(int column, int row)
    {
        var rect = GridGeometry.ModuleRect(column, row, 1, 1);
        return new Avalonia.Point((rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0);
    }

    private static void FocusByKeyboard(Avalonia.Controls.Control control) => control.Focus(NavigationMethod.Tab);

    private static void Render(
        string folder, string scratch, string name, int width, int height, byte[]? jpeg,
        Action<FakeServiceLink> state, Action<MainWindow, MainViewModel> arrange,
        DashboardLayout? layout = null, string? refuseReload = null, IReadOnlyList<string>? systemFonts = null,
        ISecretAccess? secrets = null, TimeProvider? time = null, SystemServices? system = null, EditorPreferences? preferences = null,
        IFilePicker? picker = null)
    {
        var link = new FakeServiceLink();
        state(link);
        if (refuseReload is not null)
            link.OnReload = () => Task.FromResult(new Reloaded(false, refuseReload, []));
        var store = new LayoutStore(Path.Combine(scratch, "layout.json"));
        store.Save(layout ?? Base);
        var document = new LayoutDocument(store, link);
        document.Load();
        using var model = new MainViewModel(
            link, document, preferences ?? new EditorPreferences(FirstRunDone: true), picker, systemFonts ?? ["Arial", "Consolas", "Segoe UI"], secrets, time, null, system);
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show();
        if (jpeg is not null)
            link.Push(new PreviewFrame(jpeg, 180));
        arrange(window, model);

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var file = Path.Combine(folder, name);
        window.CaptureRenderedFrame()!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Console.WriteLine("Aperçu écrit : " + file);
        window.Close();
    }

    /// <summary>Le tableau de bord réel (thème Classique), tourné de 180° et encodé en JPEG : l'image que le service envoie.</summary>
    private static async Task<byte[]> ComposeScreenJpegAsync()
    {
        using var scene = await PreviewScene.CreateAsync(longTexts: false).ConfigureAwait(false);
        using var composer = new FrameComposer();
        var theme = AppearanceResolver.Resolve(new AppearanceSettings("classique"), new SkiaSystemFonts(), Warn);
        using var background = BackgroundFactory.Create(null, theme, null, Warn);
        using var frame = composer.Compose(PreviewScene.Layout(Trofeo.ModuleKit.TextSize.Normal), scene.ByPlacement, theme, background, PreviewScene.Now);

        using var rotated = new SKBitmap(frame.Width, frame.Height);
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Scale(-1, -1, frame.Width / 2f, frame.Height / 2f);
            canvas.DrawBitmap(frame, 0, 0);
        }

        using var image = SKImage.FromBitmap(rotated);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    private static void Warn(string message) => Console.Error.WriteLine("Avertissement : " + message);
}
