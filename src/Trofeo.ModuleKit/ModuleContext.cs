using System.Text.Json;

namespace Trofeo.ModuleKit;

/// <summary>Ce que l'hôte donne à une fabrique pour créer une instance de module. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
/// <param name="InstanceId">Identifiant de l'emplacement dans la disposition.</param>
/// <param name="Settings">Réglages JSON propres à l'instance.</param>
/// <param name="Time">Source de temps (mockable en test).</param>
/// <param name="Log">Journal de l'instance, déjà préfixé du type et de l'instance.</param>
/// <param name="Secrets">Coffre à secrets du type de module (isolé des autres types).</param>
public sealed record ModuleContext(
    Guid InstanceId, JsonElement Settings, TimeProvider Time, IModuleLog Log, ISecretStore Secrets);
