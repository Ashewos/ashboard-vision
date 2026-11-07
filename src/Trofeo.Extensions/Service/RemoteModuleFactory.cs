using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Service;

/// <summary>
/// La fabrique d'un module de la communauté prêt à tourner : son descripteur et le schéma de ses réglages viennent du manifeste ;
/// chaque case est une <see cref="RemoteModule"/> servie par le <see cref="ModuleHostSupervisor"/> du module. Les recherches et les
/// actions de la fenêtre de réglage sont relayées à l'hôte (« Module arrêté. » s'il ne tourne pas : il ne tourne que tant qu'une
/// case du module est posée). La fabrique ne possède pas le superviseur.
/// </summary>
public sealed class RemoteModuleFactory : IConfigurableModuleFactory
{
    private readonly ModuleHostSupervisor _supervisor;
    private readonly ModuleManifest _manifest;

    /// <param name="entry">Le module (son manifeste est obligatoire).</param>
    /// <param name="supervisor">Le superviseur de ce module.</param>
    public RemoteModuleFactory(CommunityEntry entry, ModuleHostSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _manifest = entry.Manifest ?? throw new ArgumentException("Le module n'a pas de manifeste lisible.", nameof(entry));
    }

    /// <inheritdoc />
    public ModuleDescriptor Descriptor => _manifest.ToDescriptor();

    /// <inheritdoc />
    public SettingsSchema Schema => _manifest.Settings;

    /// <inheritdoc />
    public IModule Create(ModuleContext context) => _supervisor.Open(context);

    /// <inheritdoc />
    public Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query, CancellationToken ct) =>
        _supervisor.LookupAsync(fieldKey, query, ct);

    /// <inheritdoc />
    public Task<ActionResult> RunActionAsync(string actionKey, JsonElement settings, ISecretStore secrets, CancellationToken ct) =>
        _supervisor.RunActionAsync(actionKey, settings, ct);
}
