using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trofeo.Modules.Agents;

/// <summary>Résultat du retrait de l'ancienne commande de barre d'état.</summary>
public enum StatusLineInstallResult { Uninstalled, NotInstalled, InvalidSettings }

/// <summary>
/// Débranche l'ancien <c>Trofeo.StatusLine.exe</c> du <c>settings.json</c> de Claude Code et remet la configuration d'avant : les
/// quotas viennent maintenant du mod <c>ashboard-quotas</c>, et le logiciel n'installe plus de barre d'état. Une commande qui
/// enveloppait celle d'origine dans un tube (<c>"exe" --tee | ( commande )</c>) est remplacée par l'originale, gardée dans
/// <c>statusline-previous.json</c>. Si un fichier n'est pas lisible, rien n'est écrit.
/// </summary>
public sealed class StatusLineInstaller(string claudeHome)
{
    /// <summary>Ce que la commande de l'ancienne barre d'état contient toujours : le nom de son exécutable.</summary>
    private const string Marker = "Trofeo.StatusLine";
    private const string ChainSeparator = " --tee | ";

    /// <summary>UTF-8 strict : un octet invalide fait échouer la lecture plutôt que d'être remplacé puis réécrit.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // garde lisibles |, &, accents…
    };

    /// <summary>Le <c>settings.json</c> de Claude Code, tel que ce programme le lit et le réécrit.</summary>
    public string SettingsPath => Path.Combine(claudeHome, "settings.json");

    private string PreviousPath => ClaudeHome.LegacyStatusLinePreviousPath(claudeHome);

    /// <summary>
    /// Retire notre commande et remet l'ancienne (ou aucune s'il n'y en avait pas). <see cref="StatusLineInstallResult.InvalidSettings"/> :
    /// un fichier illisible, ou une commande chaînée dont l'originale est introuvable ; rien n'a été modifié.
    /// </summary>
    public StatusLineInstallResult Uninstall()
    {
        if (!TryReadSettings(out var root)) return StatusLineInstallResult.InvalidSettings;
        if (!IsOurs(root["statusLine"])) return StatusLineInstallResult.NotInstalled;

        var chained = CommandOf(root["statusLine"])!.Contains(ChainSeparator, StringComparison.Ordinal);
        JsonNode? previous = null;
        if (File.Exists(PreviousPath))
        {
            // Une sauvegarde abîmée : on ne devine pas, on ne touche à rien.
            if (!TryParseObject(PreviousPath, out var saved)) return StatusLineInstallResult.InvalidSettings;
            previous = saved["previous"]?.DeepClone();
        }

        // Notre commande enveloppe celle d'origine, et la sauvegarde manque ou n'en dit rien : ne pas jeter l'originale.
        if (previous is null && chained) return StatusLineInstallResult.InvalidSettings;

        if (previous is null) root.Remove("statusLine"); else root["statusLine"] = previous;
        WriteAtomic(SettingsPath, root);
        if (File.Exists(PreviousPath)) File.Delete(PreviousPath); // pas de sauvegarde (ni de dossier) : rien à effacer
        return StatusLineInstallResult.Uninstalled;
    }

    /// <summary>Vrai si la commande de barre d'état de <c>settings.json</c> est l'ancienne nôtre (fichier illisible : faux).</summary>
    public bool IsInstalled() => TryReadSettings(out var root) && IsOurs(root["statusLine"]);

    /// <summary>Fichier absent : objet vide. Sinon un objet JSON (commentaires et virgules finales admis).</summary>
    private bool TryReadSettings(out JsonObject root)
    {
        if (!File.Exists(SettingsPath))
        {
            root = new JsonObject();
            return true;
        }
        return TryParseObject(SettingsPath, out root);
    }

    private static bool TryParseObject(string path, out JsonObject root)
    {
        try
        {
            if (JsonNode.Parse(ReadStrictUtf8(path), documentOptions: ReadOptions) is JsonObject obj)
            {
                RejectDuplicateKeys(obj);
                root = obj;
                return true;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or DecoderFallbackException
                                      or ArgumentException) // ArgumentException : clé dupliquée
        {
            // Illisible : traité comme invalide.
        }
        root = new JsonObject();
        return false;
    }

    /// <summary>
    /// <c>JsonNode</c> ne voit une clé dupliquée (<c>{"a":1,"a":2}</c>) qu'au premier accès à l'objet, où il lève une
    /// <see cref="ArgumentException"/> : on parcourt tout l'arbre tout de suite pour que l'erreur survienne ici, avant
    /// toute écriture.
    /// </summary>
    private static void RejectDuplicateKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj) RejectDuplicateKeys(child.Value);
                break;
            case JsonArray array:
                foreach (var child in array) RejectDuplicateKeys(child);
                break;
        }
    }

    private static string ReadStrictUtf8(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0; // BOM éventuel
        return StrictUtf8.GetString(bytes, start, bytes.Length - start);
    }

    private static bool IsOurs(JsonNode? statusLine) =>
        CommandOf(statusLine) is { } command && command.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    private static string? CommandOf(JsonNode? statusLine) =>
        statusLine is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var command) ? command : null;

    private static void WriteAtomic(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, node.ToJsonString(WriteOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
