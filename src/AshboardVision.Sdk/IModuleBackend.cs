using Trofeo.ModuleKit;

namespace AshboardVision.Sdk;

/// <summary>
/// Ce que l'hôte (ou, en test, un faux) fait pour une instance de module : les services qui passent par le service
/// d'Ashboard Vision et les signaux de la classe de base. Interne : seuls l'hôte et les aides de test le voient.
/// Les permissions sont déjà vérifiées par <see cref="ModuleServices"/> quand ces méthodes sont appelées.
/// </summary>
internal interface IModuleBackend
{
    /// <summary>Le module demande à être redessiné (depuis n'importe quel thread).</summary>
    void Invalidate();

    /// <summary>Le module change d'état (depuis n'importe quel thread).</summary>
    void SetStatus(ModuleHealth health, string? message);

    Task<DataResult<SensorReading>> GetSensorsAsync(CancellationToken ct);

    Task<DataResult<NowPlayingInfo>> GetNowPlayingAsync(CancellationToken ct);

    /// <summary>Ouvre le relais vers un service de ce PC ; <see cref="DataStatus.Denied"/> si le service refuse ce port.</summary>
    Task<DataResult<Stream>> ConnectLocalAsync(int port, CancellationToken ct);
}
