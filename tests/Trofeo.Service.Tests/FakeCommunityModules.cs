using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Rendering;
using Trofeo.Extensions;
using Trofeo.Extensions.Updates;
using Trofeo.ModuleKit;

namespace Trofeo.Display.Tests;

/// <summary>Les modules complémentaires du service, pilotés à la main : consigne ce qu'on lui demande.</summary>
internal sealed class FakeCommunityModules : ICommunityModules
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<(int MaxFps, AppearanceSettings Appearance)> _options = [];

    public IReadOnlyList<CommunityEntry> Entries { get; set; } = [];

    public CommunityStoreInfo StoreInfo { get; set; } = new(@"C:\Modules", false, [], false, false);

    /// <summary>Ce que <see cref="GrantedTo"/> rend, par identifiant.</summary>
    public Dictionary<string, IReadOnlyList<ModulePermission>> Grants { get; } = [];

    public PackageInspection Inspection { get; set; } = new(null, ["Archive illisible."], null, null);

    public ActionResult Result { get; set; } = new(true, "Fait.");

    public event EventHandler? Changed;

    /// <summary>Ce qu'on a demandé, dans l'ordre : « nom argument… ».</summary>
    public string[] Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    /// <summary>Les plafonds d'images et apparences transmis, dans l'ordre.</summary>
    public (int MaxFps, AppearanceSettings Appearance)[] Options
    {
        get
        {
            lock (_gate)
                return [.. _options];
        }
    }

    /// <summary>Vrai tant qu'un abonné écoute <see cref="Changed"/>.</summary>
    public bool HasSubscribers => Changed is not null;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private ActionResult Record(string call)
    {
        lock (_gate)
            _calls.Add(call);
        return Result;
    }

    private static string Join(IReadOnlyList<ModulePermission> granted) => string.Join(",", granted);

    public IReadOnlyList<ModulePermission> GrantedTo(CommunityEntry entry) =>
        Grants.TryGetValue(entry.Id, out var granted) ? granted : [];

    public void Refresh() => Record("refresh");

    public PackageInspection Inspect(string avmodPath)
    {
        Record($"inspect {avmodPath}");
        return Inspection;
    }

    public Task<ActionResult> InstallAsync(string avmodPath, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        Task.FromResult(Record($"install {avmodPath} {Join(granted)}"));

    public ActionResult Approve(string id, IReadOnlyList<ModulePermission> granted) => Record($"approve {id} {Join(granted)}");

    public Task<ActionResult> SetPermissionsAsync(string id, IReadOnlyList<ModulePermission> granted) =>
        Task.FromResult(Record($"setPermissions {id} {Join(granted)}"));

    public Task<ActionResult> UninstallAsync(string id) => Task.FromResult(Record($"uninstall {id}"));

    public Task<ActionResult> RelaunchAsync(string id) => Task.FromResult(Record($"relaunch {id}"));

    public ActionResult SetModulesRoot(string path) => Record($"setModulesRoot {path}");

    public ActionResult SetDeveloperMode(bool on) => Record($"setDeveloperMode {on}");

    public ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted) => Record($"addDevFolder {path} {Join(granted)}");

    public ActionResult RemoveDevFolder(string path) => Record($"removeDevFolder {path}");

    public Task<ActionResult> ReloadDevModuleAsync(string id) => Task.FromResult(Record($"reloadDevModule {id}"));

    public void SetDisplayOptions(int maxFps, AppearanceSettings appearance)
    {
        lock (_gate)
            _options.Add((maxFps, appearance));
    }

    /// <summary>Les mises à jour proposées, par identifiant.</summary>
    public Dictionary<string, ModuleUpdateOffer> Offers { get; } = [];

    /// <summary>Si elle est posée, la vérification des mises à jour attend qu'on la libère (ou qu'on l'annule).</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    /// <summary>Ce que <see cref="Consumption"/> rend.</summary>
    public IReadOnlyList<ModuleConsumption> ConsumptionRows { get; set; } = [];

    /// <summary>Le processus du service tel que le handler l'a remis à <see cref="Consumption"/>.</summary>
    public ProcessInfo? LastServiceProcess { get; private set; }

    public ModuleUpdateOffer? UpdateFor(string id) => Offers.TryGetValue(id, out var offer) ? offer : null;

    public Task CheckUpdatesAsync(CancellationToken ct)
    {
        Record("checkUpdates");
        return CheckGate?.Task.WaitAsync(ct) ?? Task.CompletedTask;
    }

    /// <summary>Ce que la préparation rend.</summary>
    public ModuleUpdatePreparation Preparation { get; set; } = new(true, null, "1.3.0", "Notes.", [], []);

    /// <summary>Si elle est posée, la préparation attend qu'on la libère (ou qu'on l'annule).</summary>
    public TaskCompletionSource? PrepareGate { get; set; }

    public Task<ModuleUpdatePreparation> PrepareUpdateAsync(string id, CancellationToken ct)
    {
        Record($"prepareUpdate {id}");
        return PrepareGate is { } gate ? gate.Task.WaitAsync(ct).ContinueWith(_ => Preparation, ct) : Task.FromResult(Preparation);
    }

    public void CancelUpdate(string id) => Record($"cancelUpdate {id}");

    public Task<ActionResult> InstallUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        Task.FromResult(Record($"installUpdate {id} {Join(granted)}"));

    public IReadOnlyList<ModuleConsumption> Consumption(ProcessInfo service)
    {
        Record("consumption");
        LastServiceProcess = service;
        return ConsumptionRows;
    }

    /// <summary>Un manifeste valide pour les tests.</summary>
    public static ModuleManifest Manifest(
        string id, string version = "1.2.0", IReadOnlyList<ModulePermission>? permissions = null, string? repository = "alex/demo") =>
        new(id, "Démo", "Un module de démonstration", "Alex", version, new SdkVersion(1, 0), "Mod.dll", permissions ?? [ModulePermission.Internet],
            repository, new GridSize(2, 1), new Dictionary<SizePreset, GridSize>(), SettingsSchema.Empty, null);

    /// <summary>Une entrée du catalogue, avec un manifeste sauf mention contraire.</summary>
    public static CommunityEntry Entry(
        string id, CommunityState state = CommunityState.Ready, string? message = null, string? folder = null, bool dev = false,
        ModuleManifest? manifest = null, bool noManifest = false, ApprovedModule? approval = null) =>
        new(id, folder ?? $@"C:\Modules\{id}\1.2.0", noManifest ? null : manifest ?? Manifest(id), state, message, approval, dev);
}
