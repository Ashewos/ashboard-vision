using System.Diagnostics;
using Trofeo.Core;
using Trofeo.Core.Logging;

namespace Trofeo.Settings.Services;

/// <summary>
/// Le journal de la fenêtre de réglage, <c>%AppData%\ashboard-vision\logs\settings.log</c> (comme <c>display.log</c> pour le
/// service) : de quoi comprendre un plantage sans le revoir. Un confort : s'il ne peut pas s'ouvrir, la fenêtre tourne sans.
/// </summary>
internal static class SettingsLog
{
    public const string FileName = "settings.log";

    /// <summary>Le journal de ce compte.</summary>
    public static string DefaultPath { get; } = Path.Combine(AppPaths.SettingsDir, "logs", FileName);

    /// <summary>
    /// Ouvre le journal et l'ajoute aux écouteurs de <see cref="Trace"/> ; rend l'écouteur (à retirer pour le fermer), ou
    /// <c>null</c> si le dossier ou le fichier est inaccessible (disque plein, droits).
    /// </summary>
    public static FileTraceListener? TryOpen(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var listener = new FileTraceListener(path);
            Trace.Listeners.Add(listener);
            Trace.AutoFlush = true;
            return listener;
        }
        catch (Exception)
        {
            return null; // pas de journal, mais la fenêtre s'ouvre
        }
    }
}
