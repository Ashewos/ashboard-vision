using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Trofeo.Core;
using Trofeo.Device;
using Trofeo.Distribution;

namespace Trofeo.Display;

/// <summary>Ce que fait un élément du menu de l'icône.</summary>
internal enum TrayMenuAction { InstallUpdate, OpenSettings, Quit }

/// <summary>
/// Icône de la zone de notification : l'état de l'écran (et une mise à jour disponible) en info-bulle, « Installer la mise à jour »
/// quand il y en a une, « Ouvrir les réglages » (menu et double-clic) et « Quitter » dans le menu. L'icône suit le thème de la barre
/// des tâches.
/// </summary>
public sealed class TrayIcon : ApplicationContext
{
    private readonly ScreenDevice _screen;
    private readonly UpdateService? _updates;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Font _boldFont;
    private readonly SynchronizationContext? _ui;
    private readonly UserPreferenceChangedEventHandler _onUserPreferenceChanged;
    private readonly CancellationTokenSource _lifetime = new(); // annulé quand on quitte : une mise à jour lancée d'ici s'arrête avec elle
    private Icon? _ownedIcon; // l'icône incorporée en cours ; SystemIcons.Application n'est jamais libérée
    private bool _lightTaskbar;
    private bool _disposed;

    /// <summary>À créer sur le thread de l'interface, juste avant <c>Application.Run</c>.</summary>
    public TrayIcon(ScreenDevice screen, UpdateService? updates = null)
    {
        _screen = screen;
        _updates = updates;

        _menu = new ContextMenuStrip(); // un contrôle : installe le contexte de synchronisation WinForms
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        // Le menu se refait à chaque ouverture : « Installer la mise à jour » n'y est que si une version est proposée à ce moment-là.
        _menu.Opening += (_, _) => RebuildMenu();
        RebuildMenu();
        _ui = SynchronizationContext.Current;

        _icon = new NotifyIcon
        {
            Text = CurrentTooltip(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        ApplyIcon(TaskbarTheme.IsTaskbarLight());

        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenSettings();
        };

        // S'abonner d'abord, lire l'état ensuite : un « prêt » atteint pendant la construction n'est pas perdu.
        screen.StateChanged += OnStateChanged;
        if (updates is not null) updates.Changed += OnStateChanged;
        _onUserPreferenceChanged = OnUserPreferenceChanged;
        SystemEvents.UserPreferenceChanged += _onUserPreferenceChanged;
        _icon.Text = CurrentTooltip();
    }

    internal const string OpenSettingsText = "Ouvrir les réglages";
    internal const string QuitText = "Quitter";
    internal const string SettingsNotFoundText = "Le logiciel de réglage est introuvable.";
    internal const string SettingsFailedText = "Le logiciel de réglage n'a pas pu démarrer.";
    internal const string InstallFailedText = "La mise à jour n'a pas pu être lancée.";
    private const string SettingsExe = "Trofeo.Settings.exe";

    /// <summary>Longueur maximale d'une info-bulle de la zone de notification (127 caractères + le zéro final).</summary>
    private const int MaxTooltipLength = 127;

    internal static string InstallUpdateText(string version) => $"Installer la mise à jour {version}";

    private static string UpdateTooltipSuffix(string version) => $" — Mise à jour {version} disponible";

    /// <summary>
    /// Texte de l'info-bulle : l'état de l'écran et, quand il est déconnecté, le motif ; puis, si une version est proposée
    /// (<paramref name="updateVersion"/>), « Mise à jour {version} disponible ». Les 127 caractères tiennent : c'est l'état de l'écran
    /// qui est coupé, pas la mise à jour.
    /// </summary>
    public static string Tooltip(ScreenState state, string? reason, string? updateVersion = null)
    {
        var text = state switch
        {
            ScreenState.Ready => $"{ProductIdentity.DisplayName} — écran : prêt",
            ScreenState.Recovering => $"{ProductIdentity.DisplayName} — écran : reprise…",
            _ => $"{ProductIdentity.DisplayName} — écran : " + (reason ?? "non branché"),
        };

        var suffix = updateVersion is null ? "" : UpdateTooltipSuffix(updateVersion);
        var room = Math.Max(0, MaxTooltipLength - suffix.Length);
        var tooltip = (text.Length > room ? text[..room] : text) + suffix;
        return tooltip.Length > MaxTooltipLength ? tooltip[^MaxTooltipLength..] : tooltip;
    }

    /// <summary>Les éléments du menu, dans l'ordre : « Installer la mise à jour {version} » en tête quand une version est proposée.</summary>
    internal static IReadOnlyList<(TrayMenuAction Action, string Text)> MenuEntries(string? updateVersion)
    {
        var entries = new List<(TrayMenuAction, string)>();
        if (updateVersion is not null) entries.Add((TrayMenuAction.InstallUpdate, InstallUpdateText(updateVersion)));
        entries.Add((TrayMenuAction.OpenSettings, OpenSettingsText));
        entries.Add((TrayMenuAction.Quit, QuitText));
        return entries;
    }

    /// <summary>
    /// La version proposée, seulement quand on peut l'installer : « disponible », ou « échec » avec l'offre gardée (pour réessayer).
    /// Pendant le téléchargement et l'installation, elle n'est plus proposée.
    /// </summary>
    internal static string? OfferedVersion(UpdateStatus? status) =>
        status is { State: UpdateState.Available or UpdateState.Failed, Version: { } version } ? version : null;

    private string? AvailableVersion => OfferedVersion(_updates?.Current);

    private string CurrentTooltip()
    {
        var status = _screen.Status; // d'un bloc : l'affichage peut le remplacer pendant qu'on lit depuis l'interface
        return Tooltip(status.State, status.Reason, AvailableVersion);
    }

    private void RebuildMenu()
    {
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        foreach (var item in old) item.Dispose();

        foreach (var (action, text) in MenuEntries(AvailableVersion))
        {
            switch (action)
            {
                case TrayMenuAction.InstallUpdate:
                    _menu.Items.Add(text, null, (_, _) => _ = InstallUpdateAsync());
                    break;
                case TrayMenuAction.OpenSettings:
                    // L'élément par défaut (double-clic sur l'icône) est en gras, comme dans les menus Windows.
                    _menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => OpenSettings()) { Font = _boldFont });
                    break;
                case TrayMenuAction.Quit:
                    _menu.Items.Add(new ToolStripSeparator());
                    _menu.Items.Add(text, null, (_, _) => ExitThread());
                    break;
            }
        }
    }

    /// <summary>
    /// Installe la version proposée. Un retour à une version plus ancienne demande d'abord ton accord. La fenêtre de réglage n'est pas
    /// rouverte ensuite (seule une installation lancée depuis la fenêtre le demande). Une bulle dit pourquoi si l'installateur n'est
    /// pas lancé.
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        var status = _updates?.Current; // d'un bloc : l'état peut changer pendant qu'on lit
        if (_updates is null || status is null || OfferedVersion(status) is not { } version) return;

        if (status.IsDowngrade)
        {
            var answer = MessageBox.Show(
                $"Revenir à la version {version} ? Elle est plus ancienne que celle que tu utilises : tes réglages sont copiés avant.",
                ProductIdentity.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }

        try
        {
            var (ok, message) = await _updates.InstallAsync(status.IsDowngrade, reopenSettings: false, _lifetime.Token);
            if (!ok && !_disposed) ShowBalloon(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError($"Mise à jour lancée depuis l'icône : {ex}");
            if (!_disposed) ShowBalloon(InstallFailedText);
        }
    }

    /// <summary>Lance le logiciel de réglage ; une bulle dit pourquoi si on ne le trouve pas ou s'il ne démarre pas.</summary>
    private void OpenSettings()
    {
        var path = AppLocator.Find(SettingsExe);
        if (path is null)
        {
            ShowBalloon(SettingsNotFoundText);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Trace.TraceWarning($"Le logiciel de réglage n'a pas démarré ({path}) : {ex.Message}");
            ShowBalloon(SettingsFailedText);
        }
    }

    /// <summary>Ouvre le logiciel de réglage, comme « Ouvrir les réglages » (le démarrage s'en sert pour rouvrir la fenêtre après une mise à jour).</summary>
    internal void ShowSettings() => OpenSettings();

    private void ShowBalloon(string text) => _icon.ShowBalloonTip(5000, ProductIdentity.DisplayName, text, ToolTipIcon.Warning);

    // Le symbole sombre sur une barre claire, le clair sur une barre sombre. Sans le fichier incorporé : l'icône de secours.
    private void ApplyIcon(bool lightTaskbar)
    {
        _lightTaskbar = lightTaskbar;
        var next = TaskbarTheme.TryLoadIcon(TaskbarTheme.IconResourceFor(lightTaskbar));
        _icon.Icon = next ?? SystemIcons.Application;
        _ownedIcon?.Dispose();
        _ownedIcon = next;
    }

    private void Unsubscribe()
    {
        _screen.StateChanged -= OnStateChanged; // plus rien à poster une fois la boucle d'interface terminée
        if (_updates is not null) _updates.Changed -= OnStateChanged;
        SystemEvents.UserPreferenceChanged -= _onUserPreferenceChanged; // sinon le gestionnaire fuit
        _lifetime.Cancel(); // une mise à jour en cours depuis le menu s'arrête avec l'icône
    }

    protected override void ExitThreadCore()
    {
        Unsubscribe();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Unsubscribe();
            _icon.Visible = false;
            _icon.Dispose();
            _ownedIcon?.Dispose();
            _menu.Dispose();
            _lifetime.Dispose();
            _boldFont.Dispose();
        }

        base.Dispose(disposing);
    }

    // Levé sur le thread de la boucle d'affichage (écran) ou de celui qui a changé l'état (mises à jour) : on repasse par l'interface.
    private void OnStateChanged(object? sender, EventArgs e) => PostToUi(UpdateText);

    // Levé sur un fil de Windows : on relit le thème ici, on change l'icône sur l'interface seulement si la barre a changé.
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        var light = TaskbarTheme.IsTaskbarLight();
        PostToUi(() =>
        {
            if (!_disposed && light != _lightTaskbar) ApplyIcon(light);
        });
    }

    private void PostToUi(Action action)
    {
        if (_ui is null)
        {
            action();
            return;
        }

        try
        {
            _ui.Post(_ => action(), null);
        }
        catch (InvalidOperationException)
        {
            // La boucle d'interface est terminée : plus rien à mettre à jour.
        }
    }

    private void UpdateText()
    {
        if (!_disposed) _icon.Text = CurrentTooltip();
    }
}
