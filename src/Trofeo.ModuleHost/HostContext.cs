using AshboardVision.Sdk;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost;

/// <summary>Ce que toutes les instances d'un hôte partagent : le lien avec le service, ce que le module a le droit de faire, le thème et le plafond d'images.</summary>
internal sealed class HostContext
{
    private volatile Theme _theme;
    private int _maxFps;

    public HostContext(HostLink link, Type moduleType, IReadOnlyList<ModulePermission> granted, string dataDirectory, Theme theme, int maxFps)
    {
        Link = link;
        ModuleType = moduleType;
        Granted = granted;
        DataDirectory = dataDirectory;
        Secrets = new RemoteSecretStore(link);
        _theme = theme;
        _maxFps = maxFps;
    }

    public HostLink Link { get; }

    public Type ModuleType { get; }

    public IReadOnlyList<ModulePermission> Granted { get; }

    public string DataDirectory { get; }

    public RemoteSecretStore Secrets { get; }

    /// <summary>Le thème courant (il change quand l'utilisateur change d'apparence).</summary>
    public Theme Theme
    {
        get => _theme;
        set => _theme = value;
    }

    /// <summary>Le plafond d'images par seconde, toujours positif.</summary>
    public int MaxFps
    {
        get => Volatile.Read(ref _maxFps);
        set => Volatile.Write(ref _maxFps, value);
    }

    /// <summary>Une instance du module, reliée à ses services (réglages, journal, secrets, données). <see cref="CommunityModule.StartAsync"/> n'est pas appelé.</summary>
    public CommunityModule CreateModule(System.Text.Json.JsonElement settings, HostLog log, HostBackend backend)
    {
        var module = (CommunityModule)(Activator.CreateInstance(ModuleType) ?? throw new InvalidOperationException("Le module n'a pas pu être créé."));
        module.Bind(new ModuleServices(settings, log, Secrets, TimeProvider.System, DataDirectory, Granted, backend));
        return module;
    }
}
