using System.Text.Json;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk;

/// <summary>
/// Ce que le logiciel offre à une instance de module : ses réglages, son coffre à secrets, son journal, son dossier de
/// données, les données du service (avec la permission) et un client HTTP qui sait traverser l'enfermement.
/// </summary>
public sealed class ModuleServices
{
    private readonly HashSet<ModulePermission> _granted;

    internal ModuleServices(
        JsonElement settings, IModuleLog log, ISecretStore secrets, TimeProvider time, string dataDirectory,
        IEnumerable<ModulePermission> granted, IModuleBackend backend)
    {
        Settings = settings;
        Log = log ?? throw new ArgumentNullException(nameof(log));
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        Time = time ?? throw new ArgumentNullException(nameof(time));
        DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _granted = [.. granted];
    }

    /// <summary>Les réglages de cette case, tels que l'utilisateur les a saisis (un objet JSON).</summary>
    public JsonElement Settings { get; }

    /// <summary>Le journal du module : l'hôte le limite à 20 messages par seconde.</summary>
    public IModuleLog Log { get; }

    /// <summary>
    /// Le coffre à secrets du module (jetons, mots de passe), chiffré par le logiciel et propre à ce module. Chaque appel
    /// passe par le service : à utiliser quand on en a besoin, pas à chaque image.
    /// </summary>
    public ISecretStore Secrets { get; }

    /// <summary>La source de temps (à utiliser plutôt que l'horloge du système, pour pouvoir tester).</summary>
    public TimeProvider Time { get; }

    /// <summary>Un dossier où le module peut lire et écrire, propre à lui et privé.</summary>
    public string DataDirectory { get; }

    internal IModuleBackend Backend { get; }

    /// <summary>Vrai si l'utilisateur a accordé cette permission au module.</summary>
    public bool Has(ModulePermission permission) => _granted.Contains(permission);

    /// <summary>
    /// Un instantané des capteurs. Sans la permission « capteurs » : <see cref="DataStatus.Denied"/>, sans rien demander au service.
    /// </summary>
    public Task<DataResult<SensorReading>> GetSensorsAsync(CancellationToken ct) =>
        Has(ModulePermission.Sensors)
            ? Backend.GetSensorsAsync(ct)
            : Task.FromResult(new DataResult<SensorReading>(DataStatus.Denied, null));

    /// <summary>
    /// La musique en cours. Sans la permission « musique » : <see cref="DataStatus.Denied"/>, sans rien demander au service.
    /// </summary>
    public Task<DataResult<NowPlayingInfo>> GetNowPlayingAsync(CancellationToken ct) =>
        Has(ModulePermission.NowPlaying)
            ? Backend.GetNowPlayingAsync(ct)
            : Task.FromResult(new DataResult<NowPlayingInfo>(DataStatus.Denied, null));

    /// <summary>
    /// Ouvre une connexion vers un service lancé sur ce PC (le relais passe par le logiciel, car Windows interdit à un module
    /// enfermé de joindre <c>localhost</c>). Le port doit être la valeur d'un champ <c>port</c> des réglages de cette case, et la
    /// permission « Cet ordinateur » doit être accordée ; sinon (ou si le port n'est pas entre 1 et 65535) le résultat est <see cref="DataStatus.Denied"/>. Le flux
    /// renvoyé se lit et s'écrit comme une connexion TCP ; le fermer ferme la connexion.
    /// </summary>
    public Task<DataResult<Stream>> ConnectLocalAsync(int port, CancellationToken ct) =>
        Has(ModulePermission.ThisComputer) && port is >= 1 and <= 65535
            ? Backend.ConnectLocalAsync(port, ct)
            : Task.FromResult(new DataResult<Stream>(DataStatus.Denied, null));

    /// <summary>
    /// Un client HTTP. Les adresses <c>localhost</c>, <c>127.0.0.1</c> et <c>::1</c> passent par <see cref="ConnectLocalAsync"/>
    /// (le port de l'adresse doit donc être celui d'un champ <c>port</c> des réglages) ; le reste passe par le réseau, selon les
    /// permissions « réseau local » et « Internet ». Il n'utilise pas le proxy du système.
    /// <para>
    /// Avec <paramref name="certificateKey"/>, un certificat qu'aucune autorité ne signe (un appareil local à certificat
    /// auto-signé) est accepté s'il est le premier vu, et son empreinte est rangée dans le coffre du module sous
    /// <c>cert:&lt;certificateKey&gt;</c> ; tout autre certificat présenté ensuite pour cette clé est refusé. Sans clé, seuls
    /// les certificats valides sont acceptés.
    /// </para>
    /// </summary>
    public HttpClient CreateHttpClient(string? certificateKey = null) => PinnedHttpClient.Create(this, certificateKey);
}
