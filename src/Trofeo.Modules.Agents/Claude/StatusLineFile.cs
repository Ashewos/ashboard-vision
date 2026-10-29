using System.Text.Json;

namespace Trofeo.Modules.Agents;

/// <summary>
/// Fichiers de capture : un JSON par session, <c>&lt;dossier&gt;/&lt;sessionId&gt;.json</c>, en camelCase, écrit par le mod
/// <c>ashboard-quotas</c> (<c>mods/ashboard-quotas/hooks/register.ts</c>). Le logiciel ne fait que les lire et nettoyer les vieilles.
/// </summary>
public static class StatusLineFile
{
    /// <summary>Supprime les captures plus vieilles que <paramref name="age"/> (pour un fichier illisible, la date de dernière écriture fait foi).</summary>
    public static void DeleteOlderThan(string dir, DateTimeOffset now, TimeSpan age)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var path in EnumerateCaptures(dir))
        {
            try
            {
                var when = Read(path)?.CapturedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (now - when > age) File.Delete(path);
            }
            catch (IOException)
            {
                // Fichier en cours d'utilisation : on réessaiera plus tard.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Identifiant de session sûr comme nom de fichier : <c>[A-Za-z0-9-]</c>, 1 à 64 caractères.</summary>
    internal static bool IsValidSessionId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (var ch in id)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-') return false;
        }
        return true;
    }

    private static IEnumerable<string> EnumerateCaptures(string dir) =>
        Directory.EnumerateFiles(dir, "*.json").Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Lit une capture ; <c>null</c> si le fichier est absent, illisible ou n'est pas une capture valide.</summary>
    public static StatusLineRecord? Read(string path)
    {
        try
        {
            // Partage large : le mod remplace le fichier pendant qu'on le lit.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Date(root, "capturedAt") is not { } capturedAt) return null;
            var sessionId = Text(root, "sessionId");
            if (!IsValidSessionId(sessionId)) return null;

            return new StatusLineRecord(
                capturedAt,
                sessionId!,
                Text(root, "modelId"),
                Number(root, "contextWindowSize") is { } size ? (long)size : null,
                Number(root, "contextUsedPercent"),
                Quota(root, "fiveHour"),
                Quota(root, "sevenDay"));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CapturedQuota? Quota(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var q) || q.ValueKind != JsonValueKind.Object) return null;
        return Number(q, "usedPercent") is { } used ? new CapturedQuota(used, Date(q, "resetsAt")) : null;
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static DateTimeOffset? Date(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;
}
