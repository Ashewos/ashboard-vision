namespace Trofeo.Modules.Agents;

internal static class ClaudeCliLocator
{
    internal static IReadOnlyList<string> FindCandidates() => FindCandidates(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    // Le CLI embarqué n'est pas l'application graphique Claude.exe de WindowsApps.
    // Ne parcourir que Claude-Data/<paquet>/claude-code/<version>/<build>/claude.exe.
    internal static IReadOnlyList<string> FindCandidates(string userProfile, string localAppData)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var native = Path.Combine(userProfile, ".local", "bin", "claude.exe");
            if (File.Exists(native)) result.Add(native);
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var bundled = new List<(Version Version, string Path)>();
            foreach (var package in Subdirectories(Path.Combine(localAppData, "Claude-Data")))
            foreach (var directory in Subdirectories(Path.Combine(package, "claude-code")))
            {
                if (!Version.TryParse(Path.GetFileName(directory), out var version)) continue;
                foreach (var build in Subdirectories(directory))
                {
                    var cli = Path.Combine(build, "claude.exe");
                    if (File.Exists(cli)) bundled.Add((version, cli));
                }
            }
            result.AddRange(bundled.OrderByDescending(c => c.Version)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(c => c.Path));
        }
        return result;
    }

    private static string[] Subdirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
