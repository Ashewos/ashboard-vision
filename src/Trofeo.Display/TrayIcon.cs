using System.ComponentModel;
using System.Diagnostics;
using Trofeo.Core;
using Trofeo.Device;

namespace Trofeo.Display;

/// <summary>
/// Icône de la zone de notification : l'état de l'écran en info-bulle, « Ouvrir les réglages » (menu et double-clic) et
/// « Quitter » dans le menu.
/// </summary>
public sealed class TrayIcon : ApplicationContext
{
    private readonly ScreenDevice _screen;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Font _boldFont;
    private readonly SynchronizationContext? _ui;
    private bool _disposed;

    /// <summary>À créer sur le thread de l'interface, juste avant <c>Application.Run</c>.</summary>
    public TrayIcon(ScreenDevice screen)
    {
        _screen = screen;

        _menu = new ContextMenuStrip(); // un contrôle : installe le contexte de synchronisation WinForms
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        // L'élément par défaut (double-clic sur l'icône) est en gras, comme dans les menus Windows.
        _menu.Items.Add(new ToolStripMenuItem(OpenSettingsText, null, (_, _) => OpenSettings()) { Font = _boldFont });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quitter", null, (_, _) => ExitThread());
        _ui = SynchronizationContext.Current;

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = CurrentTooltip(),
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenSettings();
        };

        // S'abonner d'abord, lire l'état ensuite : un « prêt » atteint pendant la construction n'est pas perdu.
        screen.StateChanged += OnStateChanged;
        _icon.Text = CurrentTooltip();
    }

    internal const string OpenSettingsText = "Ouvrir les réglages";
    internal const string SettingsNotFoundText = "Le logiciel de réglage est introuvable.";
    internal const string SettingsFailedText = "Le logiciel de réglage n'a pas pu démarrer.";
    private const string SettingsExe = "Trofeo.Settings.exe";

    /// <summary>Longueur maximale d'une info-bulle de la zone de notification (127 caractères + le zéro final).</summary>
    private const int MaxTooltipLength = 127;

    /// <summary>Texte de l'info-bulle : l'état de l'écran et, quand il est déconnecté, le motif.</summary>
    public static string Tooltip(ScreenState state, string? reason)
    {
        var text = state switch
        {
            ScreenState.Ready => $"{ProductIdentity.DisplayName} — écran : prêt",
            ScreenState.Recovering => $"{ProductIdentity.DisplayName} — écran : reprise…",
            _ => $"{ProductIdentity.DisplayName} — écran : " + (reason ?? "non branché"),
        };

        return text.Length > MaxTooltipLength ? text[..MaxTooltipLength] : text;
    }

    private string CurrentTooltip()
    {
        var status = _screen.Status; // d'un bloc : l'affichage peut le remplacer pendant qu'on lit depuis l'interface
        return Tooltip(status.State, status.Reason);
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

    private void ShowBalloon(string text) => _icon.ShowBalloonTip(5000, ProductIdentity.DisplayName, text, ToolTipIcon.Warning);

    protected override void ExitThreadCore()
    {
        _screen.StateChanged -= OnStateChanged; // plus rien à poster une fois la boucle d'interface terminée
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _screen.StateChanged -= OnStateChanged;
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _boldFont.Dispose();
        }

        base.Dispose(disposing);
    }

    // Levé sur le thread de la boucle d'affichage : on repasse par le thread de l'interface.
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_ui is null)
        {
            UpdateText();
            return;
        }

        try
        {
            _ui.Post(_ => UpdateText(), null);
        }
        catch (InvalidOperationException)
        {
            // La boucle d'interface est terminée : plus d'info-bulle à mettre à jour.
        }
    }

    private void UpdateText()
    {
        if (!_disposed) _icon.Text = CurrentTooltip();
    }
}
