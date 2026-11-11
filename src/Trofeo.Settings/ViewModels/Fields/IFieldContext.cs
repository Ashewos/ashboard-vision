using System.Text.Json;
using System.Text.Json.Nodes;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels.Fields;

/// <summary>
/// Tout ce qu'un champ de formulaire demande au monde extérieur, pour une instance de module : lire ses réglages, les
/// écrire (un pas d'annulation), chercher, lancer une action, prévenir qu'un secret a changé. Les champs ne connaissent ni
/// l'onglet Modules ni la fenêtre : l'assistant de première ouverture les construit avec le même contexte.
/// </summary>
public interface IFieldContext
{
    /// <summary>Le type de module : la portée de ses secrets et de ses recherches.</summary>
    string TypeId { get; }

    /// <summary>Le temps, pour attendre 400 ms après la dernière frappe d'une recherche.</summary>
    TimeProvider Time { get; }

    ISecretAccess Secrets { get; }

    /// <summary>Faux pour un module retiré de l'écran : le service ne le fait pas tourner, donc aucune action possible.</summary>
    bool CanRunActions { get; }

    /// <summary>Les réglages de l'instance, tels que la disposition les porte.</summary>
    JsonElement Settings { get; }

    /// <summary>
    /// Change les réglages : <paramref name="change"/> reçoit ceux du moment (pas ceux d'il y a un instant) et rend les
    /// nouveaux. Un seul pas d'annulation ; des réglages inchangés n'écrivent rien.
    /// </summary>
    Task EditAsync(Func<JsonElement, JsonElement> change);

    Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query);

    /// <summary>
    /// Lance l'action sur les réglages écrits à l'instant. Le service recrée lui-même l'instance après une action réussie.
    /// </summary>
    Task<ActionResult> RunActionAsync(string actionKey);

    /// <summary>Un secret du coffre a changé : les modules placés de ce type doivent le relire.</summary>
    Task SecretChangedAsync();
}

/// <summary>Le contexte d'une instance de la disposition (placée ou retirée), au-dessus de l'historique partagé de la fenêtre.</summary>
public sealed class InstanceFieldContext(
    EditSession session, IServiceLink link, ISecretAccess secrets, string typeId, Guid instanceId, TimeProvider? time = null) : IFieldContext
{
    private static readonly JsonElement EmptySettings = JsonDocument.Parse("{}").RootElement.Clone();

    // SetSettings ne consulte pas les types : un éditeur sans descripteurs suffit.
    private static readonly LayoutEditor Editor = new(new Dictionary<string, ModuleDescriptor>());

    public string TypeId { get; } = typeId;

    public TimeProvider Time { get; } = time ?? TimeProvider.System;

    public ISecretAccess Secrets { get; } = secrets;

    public bool CanRunActions => session.Document.Current.AllPlacements.Any(p => p.InstanceId == instanceId);

    public JsonElement Settings => Find(session.Document.Current)?.Settings is { ValueKind: JsonValueKind.Object } settings ? settings : EmptySettings;

    public async Task EditAsync(Func<JsonElement, JsonElement> change)
    {
        await session.ApplyAsync(layout =>
        {
            if (Find(layout) is not { } instance)
                return null; // l'instance a disparu entre-temps

            var current = instance.Settings.ValueKind == JsonValueKind.Object ? instance.Settings : EmptySettings;
            var edited = change(current);
            if (Normalize(current) == Normalize(edited))
                return null;

            var result = Editor.SetSettings(layout, instanceId, edited);
            return result.Ok ? result.Layout : null;
        }).ConfigureAwait(true);
    }

    public Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query) => link.LookupAsync(TypeId, fieldKey, query);

    public async Task<ActionResult> RunActionAsync(string actionKey)
    {
        // Une modification encore en route (une adresse tapée à l'instant) est écrite et rechargée avant l'action.
        await session.ApplyAsync(_ => null).ConfigureAwait(true);
        return await link.RunActionAsync(instanceId, actionKey).ConfigureAwait(true);
    }

    public async Task SecretChangedAsync()
    {
        // Un module gardé au rechargement ne relit jamais le coffre : on recrée chaque instance placée de ce type. Une
        // instance retirée ne tourne pas, elle lira le coffre quand on la remettra.
        var placed = session.Document.Current.AllPlacements.Where(p => p.TypeId == TypeId).Select(p => p.InstanceId).ToList();
        string? problem = null;
        foreach (var id in placed)
        {
            try
            {
                if (!await link.RestartModuleAsync(id).ConfigureAwait(true))
                    problem ??= Texts.ModulesRestartMissing;
            }
            catch (ControlException ex)
            {
                problem = string.Format(Texts.ModulesRestartFailed, ex.Message);
            }
        }

        if (problem is not null)
            session.Report(problem);
    }

    private ModulePlacement? Find(DashboardLayout layout) =>
        layout.AllPlacements.FirstOrDefault(p => p.InstanceId == instanceId) ?? (layout.Shelf ?? []).FirstOrDefault(p => p.InstanceId == instanceId);

    /// <summary>Le JSON compact, pour comparer deux réglages sans que la mise en forme compte.</summary>
    private static string Normalize(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Undefined ? "{}" : JsonNode.Parse(settings.GetRawText())?.ToJsonString() ?? "{}";
}
