namespace Trofeo.Modules.Agents;

/// <summary>Emplacement du dossier de configuration de Claude Code et de nos fichiers dedans.</summary>
public static class ClaudeHome
{
    /// <summary>Notre dossier sous celui de Claude Code (le projet s'appelle Ashboard Vision depuis le plan 6).</summary>
    private const string FolderName = "ashboard-vision";

    /// <summary>Notre dossier d'avant le changement de nom : il garde d'anciennes captures et la sauvegarde de l'ancienne barre d'état.</summary>
    private const string LegacyFolderName = "trofeo-HB-dashboard";

    /// <summary>
    /// <c>%CLAUDE_CONFIG_DIR%</c> s'il est défini, sinon <c>~/.claude</c>. On écrit sous ce dossier (et non dans
    /// <c>%AppData%</c>) car l'appli Claude empaquetée redirige <c>%AppData%</c> vers une copie privée.
    /// </summary>
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    /// <summary>Dossier des captures, écrites par le mod <c>ashboard-quotas</c> : <c>&lt;claudeHome&gt;/ashboard-vision/statusline</c>.</summary>
    public static string StatusLineDir(string claudeHome) => Path.Combine(claudeHome, FolderName, "statusline");

    /// <summary>
    /// Dossier des captures d'avant le changement de nom, <c>&lt;claudeHome&gt;/trofeo-HB-dashboard/statusline</c> : plus rien n'y est
    /// écrit, mais ses captures récentes comptent encore pendant la transition.
    /// </summary>
    public static string LegacyStatusLineDir(string claudeHome) => Path.Combine(claudeHome, LegacyFolderName, "statusline");

    /// <summary>La sauvegarde de l'ancienne barre d'état, que <see cref="StatusLineInstaller"/> restaure en la retirant.</summary>
    internal static string LegacyStatusLinePreviousPath(string claudeHome) =>
        Path.Combine(claudeHome, LegacyFolderName, "statusline-previous.json");
}
