using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.Tests;

/// <summary>Un <see cref="IServiceLink"/> que le test (ou l'aperçu) pilote à la main.</summary>
public sealed class FakeServiceLink : IServiceLink
{
    public LinkState State { get; set; } = LinkState.Connected;
    public Welcome? Welcome { get; set; }
    public ServiceStatus? Status { get; set; }
    public UpdateInfo? Updates { get; set; }

    public event Action? Changed;
    public event Action<PreviewFrame>? FrameReceived;

    public bool RealTime { get; private set; }
    public int ReloadCount { get; private set; }
    public int StartCount { get; private set; }
    public bool StartResult { get; set; } = true;
    public Func<Task<Reloaded>> OnReload { get; set; } = () => Task.FromResult(new Reloaded(true, null, []));

    /// <summary>Change l'état et prévient les abonnés.</summary>
    public void Set(LinkState state, Welcome? welcome = null, ServiceStatus? status = null)
    {
        State = state;
        Welcome = welcome;
        Status = status;
        Changed?.Invoke();
    }

    public void Push(PreviewFrame frame) => FrameReceived?.Invoke(frame);

    public void SetRealTimePreview(bool realTime) => RealTime = realTime;

    public bool Paused { get; private set; }

    public void SetPreviewPaused(bool paused) => Paused = paused;

    public Task<Reloaded> ReloadAsync()
    {
        ReloadCount++;
        return OnReload();
    }

    /// <summary>Les recherches demandées, dans l'ordre.</summary>
    public List<(string TypeId, string FieldKey, string Query)> Lookups { get; } = [];

    public Func<string, string, string, Task<IReadOnlyList<LookupResult>>> OnLookup { get; set; } =
        (_, _, _) => Task.FromResult<IReadOnlyList<LookupResult>>([]);

    public Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query)
    {
        Lookups.Add((typeId, fieldKey, query));
        return OnLookup(typeId, fieldKey, query);
    }

    /// <summary>Les actions lancées, dans l'ordre.</summary>
    public List<(Guid InstanceId, string ActionKey)> Actions { get; } = [];

    public Func<Guid, string, Task<ActionResult>> OnAction { get; set; } = (_, _) => Task.FromResult(new ActionResult(true, ""));

    public Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey)
    {
        Actions.Add((instanceId, actionKey));
        return OnAction(instanceId, actionKey);
    }

    /// <summary>Les modules rechargés, dans l'ordre.</summary>
    public List<Guid> Restarts { get; } = [];

    public Func<Guid, Task<bool>> OnRestart { get; set; } = _ => Task.FromResult(true);

    public Task<bool> RestartModuleAsync(Guid instanceId)
    {
        Restarts.Add(instanceId);
        return OnRestart(instanceId);
    }

    /// <summary>Change l'état des mises à jour et prévient les abonnés.</summary>
    public void SetUpdates(UpdateInfo? updates)
    {
        Updates = updates;
        Changed?.Invoke();
    }

    /// <summary>Les recherches de mise à jour demandées.</summary>
    public int CheckUpdatesCount { get; private set; }

    public Func<Task> OnCheckUpdates { get; set; } = () => Task.CompletedTask;

    public Task CheckUpdatesAsync()
    {
        CheckUpdatesCount++;
        return OnCheckUpdates();
    }

    /// <summary>Les choix de canal envoyés, dans l'ordre.</summary>
    public List<(LinkUpdateChannel Channel, bool WaitForNextStable)> UpdatePreferences { get; } = [];

    /// <summary>Par défaut, comme le service : le choix est gardé dans l'état des mises à jour.</summary>
    public Func<LinkUpdateChannel, bool, Task>? OnSetUpdatePreferences { get; set; }

    public Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable)
    {
        UpdatePreferences.Add((channel, waitForNextStable));
        if (OnSetUpdatePreferences is not null)
            return OnSetUpdatePreferences(channel, waitForNextStable);
        if (Updates is { } current)
            SetUpdates(current with { Channel = channel, WaitForNextStable = waitForNextStable });
        return Task.CompletedTask;
    }

    /// <summary>Les installations demandées (la valeur de <c>confirmDowngrade</c>), dans l'ordre.</summary>
    public List<bool> UpdateInstalls { get; } = [];

    public Func<bool, Task<ActionResult>> OnInstallUpdate { get; set; } = _ => Task.FromResult(new ActionResult(true, ""));

    public Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade)
    {
        UpdateInstalls.Add(confirmDowngrade);
        return OnInstallUpdate(confirmDowngrade);
    }

    // ---- Modules complémentaires ----

    public IReadOnlyList<ModuleTypeInfo>? Types { get; set; }

    /// <summary>Change les types connus et prévient les abonnés.</summary>
    public void SetTypes(IReadOnlyList<ModuleTypeInfo>? types)
    {
        Types = types;
        Changed?.Invoke();
    }

    /// <summary>Ce que le service dit des modules complémentaires.</summary>
    public CommunityOverview Community { get; set; } = new([], @"C:\Modules", false, [], false);

    /// <summary>Ce que le service voit dans un paquet.</summary>
    public PackageInfo Package { get; set; } = new(null, null, null, null, null, [], ["Archive illisible."], null, null);

    /// <summary>Ce que le service répond à une demande qui change les modules.</summary>
    public ActionResult ModuleResult { get; set; } = new(true, "");

    public List<ModuleConsumption> Consumption { get; } = [];

    /// <summary>Les demandes sur les modules complémentaires, dans l'ordre : « nom argument… ».</summary>
    public List<string> ModuleCalls { get; } = [];

    private static string Join(IReadOnlyList<ModulePermission> granted) => string.Join(",", granted);

    /// <summary>Si posé, toute demande qui change les modules lève cette erreur (la liaison est coupée, le délai est passé…).</summary>
    public ControlException? ModuleFailure { get; set; }

    /// <summary>Si posé, toute demande qui change les modules attend cette tâche (une installation qui dure).</summary>
    public Task? ModuleGate { get; set; }

    private async Task<ActionResult> Gated(string call)
    {
        ModuleCalls.Add(call);
        await ModuleGate!;
        return ModuleFailure is null ? ModuleResult : throw ModuleFailure;
    }

    private Task<ActionResult> Module(string call)
    {
        if (ModuleGate is not null)
            return Gated(call);
        ModuleCalls.Add(call);
        return ModuleFailure is null ? Task.FromResult(ModuleResult) : Task.FromException<ActionResult>(ModuleFailure);
    }

    /// <summary>Si posé, la lecture des modules lève cette erreur.</summary>
    public ControlException? CommunityFailure { get; set; }

    public Task<CommunityOverview> GetCommunityAsync()
    {
        ModuleCalls.Add("community");
        if (CommunityFailure is not null)
            return Task.FromException<CommunityOverview>(CommunityFailure);
        return Task.FromResult(Community);
    }

    /// <summary>Si posé, rend l'inspection à la place de <see cref="Package"/> (un test la fait attendre).</summary>
    public Func<string, Task<PackageInfo>>? OnInspect { get; set; }

    public Task<PackageInfo> InspectPackageAsync(string path)
    {
        ModuleCalls.Add($"inspect {path}");
        if (OnInspect is not null)
            return OnInspect(path);
        return InspectFailure is null ? Task.FromResult(Package) : Task.FromException<PackageInfo>(InspectFailure);
    }

    public Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted) => Module($"install {path} {Join(granted)}");

    public Task<ActionResult> ApproveModuleAsync(string id, IReadOnlyList<ModulePermission> granted) => Module($"approve {id} {Join(granted)}");

    public Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted) =>
        Module($"permissions {id} {Join(granted)}");

    public Task<ActionResult> UninstallModuleAsync(string id) => Module($"uninstall {id}");

    /// <summary>Si posé, <see cref="InspectPackageAsync"/> lève cette erreur (le service ne peut pas lire le fichier).</summary>
    public ControlException? InspectFailure { get; set; }

    public Task<ActionResult> RelaunchModuleAsync(string id) => Module($"relaunch {id}");

    // Comme le service : une demande acceptée change ce que la lecture suivante rend.
    private async Task<ActionResult> Changing(string call, Func<CommunityOverview, CommunityOverview> change)
    {
        var result = await Module(call);
        if (result.Ok)
            Community = change(Community);
        return result;
    }

    public Task<ActionResult> SetModulesRootAsync(string path) => Changing($"root {path}", c => c with { ModulesRoot = path });

    public Task<ActionResult> SetDeveloperModeAsync(bool on) => Changing($"dev {on}", c => c with
    {
        DeveloperMode = on,
        Modules = [.. c.Modules.Where(m => on || !m.FromDevFolder)], // éteint, les modules des dossiers de travail disparaissent
    });

    public Task<ActionResult> AddDevFolderAsync(string path, IReadOnlyList<ModulePermission> granted) =>
        Changing($"addDev {path} {Join(granted)}", c => c with { DevFolders = [.. c.DevFolders.Where(f => f != path), path] });

    public Task<ActionResult> RemoveDevFolderAsync(string path) =>
        Changing($"removeDev {path}", c => c with { DevFolders = [.. c.DevFolders.Where(f => f != path)] });

    /// <summary>Ce que le service lit dans un dossier de travail.</summary>
    public PackageInfo DevFolder { get; set; } = new("alex.dev", "Démo", "Alex", "0.1.0", "Un module en cours d'écriture.",
        [ModulePermission.Internet, ModulePermission.Sensors], [], null, null);

    /// <summary>Si posé, rend l'examen d'un dossier à la place de <see cref="DevFolder"/> (un test le fait attendre, ou lever).</summary>
    public Func<string, Task<PackageInfo>>? OnInspectDevFolder { get; set; }

    public Task<PackageInfo> InspectDevFolderAsync(string path)
    {
        ModuleCalls.Add($"inspectDev {path}");
        return OnInspectDevFolder is null ? Task.FromResult(DevFolder) : OnInspectDevFolder(path);
    }

    public Task<ActionResult> ReloadDevModuleAsync(string id) => Module($"reloadDev {id}");

    public Task CheckModuleUpdatesAsync()
    {
        ModuleCalls.Add("checkModuleUpdates");
        return Task.CompletedTask;
    }

    /// <summary>Ce que la préparation d'une mise à jour rend.</summary>
    public ModuleUpdatePreparation Preparation { get; set; } = new(true, null, "1.3.0", "Notes.", [ModulePermission.Internet], []);

    /// <summary>Si posé, rend la préparation à la place de <see cref="Preparation"/> (un test la fait attendre, ou lever).</summary>
    public Func<string, Task<ModuleUpdatePreparation>>? OnPrepare { get; set; }

    public Task<ModuleUpdatePreparation> PrepareModuleUpdateAsync(string id)
    {
        ModuleCalls.Add($"prepare {id}");
        return OnPrepare is null ? Task.FromResult(Preparation) : OnPrepare(id);
    }

    /// <summary>Si posé, l'abandon d'une mise à jour lève cette erreur (le service n'est plus là).</summary>
    public ControlException? CancelFailure { get; set; }

    public Task CancelModuleUpdateAsync(string id)
    {
        ModuleCalls.Add($"cancel {id}");
        return CancelFailure is null ? Task.CompletedTask : Task.FromException(CancelFailure);
    }

    public Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted) => Module($"update {id} {Join(granted)}");

    /// <summary>Si posé, la lecture de la consommation lève cette erreur.</summary>
    public ControlException? ConsumptionFailure { get; set; }

    public Task<IReadOnlyList<ModuleConsumption>> GetConsumptionAsync()
    {
        ModuleCalls.Add("consumption");
        if (ConsumptionFailure is not null)
            return Task.FromException<IReadOnlyList<ModuleConsumption>>(ConsumptionFailure);
        return Task.FromResult<IReadOnlyList<ModuleConsumption>>([.. Consumption]);
    }

    public Task<bool> StartServiceAsync()
    {
        StartCount++;
        return Task.FromResult(StartResult);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
