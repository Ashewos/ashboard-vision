using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Trofeo.Modules.Agents;

namespace Trofeo.Distribution;

/// <summary>Ce que la préparation a fait : <see cref="Ran"/> est faux quand la version était déjà préparée.</summary>
public sealed record UserSetupReport(bool Ran, IReadOnlyList<string> Warnings);

/// <summary>
/// La préparation propre à l'utilisateur, au premier lancement d'une version : l'installateur copie des fichiers pour tout le monde,
/// mais la valeur <c>Run</c>, la barre d'état et le mod de Claude Code sont à l'utilisateur. Elle se fait dans l'ordre : valeur
/// <c>Run</c>, ancienne barre d'état, mod des quotas, puis enregistrement de la version dans <c>installation.json</c>
/// (<c>{ "versionPreparee": "...", "modInstalle": true|false }</c>). Une étape qui échoue ajoute un avertissement et laisse les suivantes
/// se faire ; seule une annulation interrompt le tout.
/// </summary>
public sealed class UserSetup(
    IRunKey runKey, QuotaModInstaller mods, StatusLineInstaller statusLine, string settingsDir, string modsDir, string serviceExePath)
{
    internal const string StateFileName = "installation.json";

    /// <summary>L'ancienne valeur <c>Run</c> et la nouvelle (les mêmes noms que <c>RunKeyStartup</c>, dans le logiciel de réglage).</summary>
    private const string LegacyRunValue = "trofeo-HB-dashboard";
    private const string RunValue = "ashboard-vision";

    private static readonly JsonSerializerOptions StateOptions = new() { WriteIndented = true };

    public async Task<UserSetupReport> RunAsync(AppVersion current, InstallOptions? options, CancellationToken ct)
    {
        var state = ReadState();
        if (state is not null && AppVersion.TryParse(state.VersionPreparee, out var prepared) && prepared == current)
        {
            return new UserSetupReport(false, []);
        }

        var warnings = new List<string>();
        PrepareRunValue(options, firstTime: state is null, warnings);
        RemoveOldStatusLine(warnings);
        var modWanted = await PrepareModAsync(state?.ModInstalle ?? false, warnings, ct);
        SaveState(new SetupState(current.ToString(), modWanted), warnings);
        return new UserSetupReport(true, warnings);
    }

    /// <summary>
    /// L'ancienne valeur est remplacée par la nouvelle, qui pointe sur le service d'ici (une valeur déjà là est remise à jour : le
    /// dossier d'installation a pu changer). Sans ancienne ni nouvelle valeur, c'est le choix de l'installateur qui décide, mais à la
    /// première préparation seulement (<paramref name="firstTime"/>) : plus tard, il rallumerait un démarrage que l'utilisateur a coupé
    /// dans les Paramètres. Un « non » ne touche à rien.
    /// </summary>
    private void PrepareRunValue(InstallOptions? options, bool firstTime, List<string> warnings)
    {
        try
        {
            var hadLegacy = runKey.Read(LegacyRunValue) is not null;
            if (hadLegacy || runKey.Read(RunValue) is not null || (firstTime && options?.StartWithWindows == true))
            {
                runKey.Write(RunValue, "\"" + serviceExePath + "\"");
            }

            if (hadLegacy)
            {
                runKey.Delete(LegacyRunValue);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            warnings.Add("Le démarrage avec Windows n'a pas pu être réglé : " + ex.Message);
        }
    }

    private void RemoveOldStatusLine(List<string> warnings)
    {
        try
        {
            if (statusLine.IsInstalled() && statusLine.Uninstall() == StatusLineInstallResult.InvalidSettings)
            {
                warnings.Add("L'ancienne barre d'état de Claude Code n'a pas pu être retirée : son fichier de réglages n'est pas lisible.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("L'ancienne barre d'état de Claude Code n'a pas pu être retirée : " + ex.Message);
        }
    }

    /// <summary>
    /// Retire l'ancien mod s'il est là. Le nouveau est ensuite rafraîchi s'il est déjà installé (par l'assistant, par l'action du module
    /// ou par la préparation précédente : <c>claude plugin list --json</c> le dit, même si aucun <c>installation.json</c> ne le sait), ou
    /// installé s'il était souhaité sans être là (l'ancien était là, ou la préparation précédente l'avait noté). Rend si le mod est
    /// souhaité, pour que la prochaine préparation le retrouve : un échec ou l'absence de Claude Code n'efface pas ce souhait.
    /// </summary>
    private async Task<bool> PrepareModAsync(bool wasWanted, List<string> warnings, CancellationToken ct)
    {
        var listing = await mods.ListAsync(ct);
        if (!listing.Ok && listing.Message == QuotaModInstaller.ClaudeMissingMessage)
        {
            warnings.Add("Claude Code est introuvable sur ce PC : le mod des quotas n'a pas été préparé.");
            return wasWanted;
        }

        if (!listing.Ok || !TryReadPluginIds(listing.Message, out var ids))
        {
            warnings.Add(listing.Ok
                ? "La liste des mods de Claude Code n'a pas pu être lue : le mod des quotas n'a pas été préparé."
                : listing.Message);
            return wasWanted;
        }

        var legacyInstalled = ids.Contains(QuotaModInstaller.LegacyPluginName);
        if (legacyInstalled)
        {
            // Un échec ici n'arrête rien : le nouveau mod s'installe quand même.
            var removal = await mods.UninstallAsync(QuotaModInstaller.LegacyPluginName, QuotaModInstaller.LegacyMarketplace, ct);
            if (!removal.Ok)
            {
                warnings.Add("L'ancien mod des quotas n'a pas pu être retiré de Claude Code : " + removal.Message);
            }
        }

        var newInstalled = ids.Contains(QuotaModInstaller.PluginName);
        var wanted = wasWanted || legacyInstalled || newInstalled;
        if (newInstalled)
        {
            var update = await mods.UpdateAsync(ct);
            if (!update.Ok)
            {
                warnings.Add("Le mod des quotas n'a pas pu être mis à jour : " + update.Message);
            }
        }
        else if (wanted && !Directory.Exists(modsDir))
        {
            warnings.Add($"Le dossier du mod des quotas est introuvable ({modsDir}) : le mod n'a pas été installé.");
        }
        else if (wanted)
        {
            var installation = await mods.InstallAsync(ct);
            if (!installation.Ok)
            {
                warnings.Add("Le mod des quotas n'a pas pu être installé : " + installation.Message);
            }
        }

        return wanted;
    }

    /// <summary>Les <c>id</c> du tableau JSON de <c>claude plugin list --json</c> (un même id peut y figurer deux fois, une par portée).</summary>
    private static bool TryReadPluginIds(string json, out HashSet<string> ids)
    {
        ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(json) is not JsonArray plugins)
            {
                return false;
            }

            foreach (var plugin in plugins)
            {
                if (plugin is JsonObject { } obj && obj["id"] is JsonValue value && value.TryGetValue<string>(out var id))
                {
                    ids.Add(id);
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private SetupState? ReadState()
    {
        try
        {
            var path = Path.Combine(settingsDir, StateFileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<SetupState>(File.ReadAllText(path), StateOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // illisible : on prépare de nouveau, ce qui est sans danger
        }
    }

    /// <summary>Écriture atomique : fichier temporaire puis remplacement.</summary>
    private void SaveState(SetupState state, List<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(settingsDir);
            var path = Path.Combine(settingsDir, StateFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, StateOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("La version préparée n'a pas pu être enregistrée : la préparation sera refaite au prochain démarrage. " + ex.Message);
        }
    }

    private sealed record SetupState(
        [property: JsonPropertyName("versionPreparee")] string? VersionPreparee,
        [property: JsonPropertyName("modInstalle")] bool ModInstalle);
}
