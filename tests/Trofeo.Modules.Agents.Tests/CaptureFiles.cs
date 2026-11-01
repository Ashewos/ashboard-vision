using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Trofeo.Modules.Agents.Tests;

/// <summary>
/// Écrit une capture comme le mod <c>ashboard-quotas</c> (<c>mods/ashboard-quotas/hooks/register.ts</c>) : un JSON par session,
/// <c>&lt;dossier&gt;/&lt;sessionId&gt;.json</c>, en camelCase. Le logiciel ne fait que les lire : cette écriture ne sert qu'aux tests.
/// </summary>
internal static class CaptureFiles
{
    public static void Write(string dir, StatusLineRecord record)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, record.SessionId + ".json"), Serialize(record));
    }

    public static byte[] Serialize(StatusLineRecord record)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("capturedAt", record.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("sessionId", record.SessionId);
            WriteString(w, "modelId", record.ModelId);
            if (record.ContextWindowSize is { } size) w.WriteNumber("contextWindowSize", size); else w.WriteNull("contextWindowSize");
            if (record.ContextUsedPercent is { } pct) w.WriteNumber("contextUsedPercent", pct); else w.WriteNull("contextUsedPercent");
            WriteQuota(w, "fiveHour", record.FiveHour);
            WriteQuota(w, "sevenDay", record.SevenDay);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    private static void WriteQuota(Utf8JsonWriter w, string name, CapturedQuota? quota)
    {
        if (quota is null) { w.WriteNull(name); return; }
        w.WriteStartObject(name);
        w.WriteNumber("usedPercent", quota.UsedPercent);
        if (quota.ResetsAt is { } at) w.WriteString("resetsAt", at.ToString("O", CultureInfo.InvariantCulture)); else w.WriteNull("resetsAt");
        w.WriteEndObject();
    }
}
