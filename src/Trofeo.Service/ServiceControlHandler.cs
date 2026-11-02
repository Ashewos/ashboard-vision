using System.Diagnostics;
using System.Reflection;
using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Core.Shortcuts;
using Trofeo.Device;
using Trofeo.Distribution;
using Trofeo.Extensions;
using Trofeo.ModuleKit;

namespace Trofeo.Display;

/// <summary>
/// Ce que le service répond à la fenêtre de réglage : poignée de main, état, rechargement à chaud de la disposition,
/// recherches et actions des modules, aperçu de l'écran, modules complémentaires. Le canal de contrôle ne connaît pas
/// <c>Trofeo.Extensions</c> : c'est ici que ses types sont traduits en copies simples.
/// </summary>
/// <param name="screen">L'état du lien avec l'écran (fourni par le programme, qui connaît le périphérique).</param>
/// <param name="sensors">L'état de l'assistant des capteurs (fourni par le programme).</param>
/// <param name="buildConfig">
/// Fabrique la configuration à appliquer (thème, fond) à partir d'une disposition ; le second argument est la configuration
/// que la boucle applique en ce moment (<c>null</c> avant le premier rechargement d'un handler sans configuration de départ) :
/// son fond peut être gardé quand rien de ce qui le décide n'a changé.
/// </param>
/// <param name="hotkeys">Pose les raccourcis après un rechargement ; <c>null</c> : pas de raccourcis.</param>
/// <param name="community">
/// Les modules complémentaires. Le handler lui transmet le plafond d'images et l'apparence à chaque rechargement, et remet à neuf les
/// cases posées quand ce que leur type exécute change (<see cref="CommunityTypeWatcher"/>).
/// </param>
/// <param name="buildWarnings">
/// Reçoit les avertissements de <paramref name="buildConfig"/> (apparence, fond) : ils rejoignent ceux du rechargement.
/// <paramref name="buildConfig"/> doit les lui adresser.
/// </param>
/// <param name="initialConfig">
/// La configuration que la boucle applique au démarrage : tant que l'apparence ne change pas, un rechargement garde son thème et
/// son fond (une vidéo ne repart pas de zéro, une image n'est pas rouverte).
/// </param>
/// <param name="updates">Les mises à jour ; <c>null</c> : le service tourne sans (l'état reste « inconnu » et rien ne s'installe).</param>
public sealed class ServiceControlHandler(
    LayoutStore store, DisplayLoop loop, ModuleRegistry registry, Func<ScreenInfo> screen, Func<SensorInfo> sensors,
    ProcessMeter meter, Func<DashboardLayout, LoopConfig?, LoopConfig> buildConfig, IHotkeyHost? hotkeys, ICommunityModules community,
    WarningSink? buildWarnings = null, LoopConfig? initialConfig = null, UpdateService? updates = null)
    : IControlHandler, IDisposable
{
    /// <summary>Le plafond d'images de la boucle (<see cref="DisplayLoop"/>) : un hôte de module n'a aucune raison d'en écrire davantage.</summary>
    public const int HostMaxFpsCap = 12;

    private static readonly string ServiceVersion = ReadVersion();

    // Remet à neuf les cases posées d'un module complémentaire quand son type change vraiment ; libéré avec le handler.
    private readonly CommunityTypeWatcher _watcher = new(community, loop);

    /// <summary>La version qui tourne (<c>AssemblyInformationalVersion</c>, sans le <c>+hash</c> du commit).</summary>
    internal static string InstalledVersion => ServiceVersion;

    // La vie du service : les installations lancées par une requête n'en dépendent pas.
    private readonly CancellationTokenSource _lifetime = new();

    // Un rechargement à la fois : deux fenêtres de réglage ne s'entremêlent pas.
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    // Ce que la boucle applique en ce moment (thème et fond) et l'apparence dont ils viennent ; sous _reloadGate.
    private LoopConfig? _applied = initialConfig;
    private AppearanceSettings? _appliedAppearance = initialConfig?.Layout.Appearance;

    // Les raccourcis posés en dernier (au démarrage, puis à chaque pose) et s'il y avait alors un module commandable ; sous
    // _reloadGate. Un rechargement qui ne change ni l'un ni l'autre ne les repose pas : sinon un raccourci pris par une autre
    // application remettrait son avertissement après chaque modification sans rapport. null : état inconnu, la prochaine pose a lieu.
    private IReadOnlyList<ShortcutBinding>? _appliedShortcuts = initialConfig is null ? null : initialConfig.Layout.Shortcuts ?? ShortcutDefaults.All;
    private bool _appliedCommandable = loop.FindCommandTarget(null) is not null;

    public Welcome Hello() => new(ServiceVersion, ControlPipe.ProtocolVersion, GetTypes());

    public IReadOnlyList<ModuleTypeInfo> GetTypes()
    {
        var entries = community.Entries;
        var types = new List<ModuleTypeInfo>();
        foreach (var d in registry.Types)
        {
            var schema = registry.Factory(d.TypeId) is IConfigurableModuleFactory configurable ? configurable.Schema : SettingsSchema.Empty;
            // Le premier module de cet identifiant, comme le registre : seul un type de la communauté a une fiche.
            var entry = entries.FirstOrDefault(e => e.Id == d.TypeId && e.Manifest is not null);
            var sheet = entry?.Manifest is { } manifest
                ? new CommunityTypeInfo(manifest.Author, manifest.Description, manifest.Version, manifest.Permissions, community.GrantedTo(entry))
                : null;
            types.Add(new ModuleTypeInfo(d.TypeId, d.DisplayName, d.MinSize, d.Presets, d.AcceptsCommands, schema, sheet));
        }

        return types;
    }

    public ServiceStatus GetStatus()
    {
        var status = loop.Status;
        var modules = status.Modules
            .Select(m => new ModuleState(m.InstanceId, m.TypeId, m.Status.Health, m.Status.ActionMessage, m.Notices))
            .ToList();
        return new ServiceStatus(
            screen(), status.Emulated, modules, meter.Read(),
            new FrameInfo(status.FramesSent, status.RenderMs, status.SendMs), sensors());
    }

    /// <summary>L'état du lien avec l'écran tel que la fenêtre de réglage le connaît.</summary>
    public static ScreenInfo ToScreenInfo(ScreenStatus status) => new(
        status.State switch
        {
            ScreenState.Ready => LinkScreenState.Ready,
            ScreenState.Recovering => LinkScreenState.Recovering,
            _ => LinkScreenState.Disconnected,
        },
        status.Reason);

    public async Task<Reloaded> ReloadAsync(CancellationToken ct)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Le fichier d'abord : illisible ou invalide, l'écran ne change pas et rien n'est copié en .bad.
            var loaded = store.TryLoad();
            if (loaded.Layout is not { } layout)
                return new Reloaded(false, loaded.Error ?? "La disposition n'a pas pu être lue.", []);

            var warnings = new List<string>(loaded.Warnings);

            LoopConfig config;
            if (_applied is { } applied && Equals(layout.Appearance, _appliedAppearance))
            {
                // Même apparence : mêmes instances de thème et de fond, la boucle n'en change aucun.
                config = new LoopConfig(layout, applied.Theme, applied.Background)
                {
                    BackgroundWarnings = applied.BackgroundWarnings,
                    BackgroundIsThemeFallback = applied.BackgroundIsThemeFallback,
                };
            }
            else
            {
                buildWarnings?.Drain(); // ce qu'une fabrication précédente (le démarrage) a laissé ne regarde pas ce rechargement
                try
                {
                    config = buildConfig(layout, _applied);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Trace.TraceError($"Rechargement : l'apparence n'a pas pu être appliquée : {ex}");
                    return new Reloaded(false, $"L'apparence n'a pas pu être appliquée : {ex.Message}", []);
                }

                if (buildWarnings is not null) warnings.AddRange(buildWarnings.Drain());
            }

            // Les hôtes des modules complémentaires, avant que les cases soient recréées. Ici et pas dans buildConfig : celui-ci ne tourne
            // que si l'apparence change, et un plafond d'images modifié seul ne leur parviendrait pas.
            ApplyHostOptions(community, layout);

            warnings.AddRange(await loop.ApplyAsync(config).ConfigureAwait(false));
            _applied = config;
            _appliedAppearance = layout.Appearance;

            if (hotkeys is not null)
            {
                var wanted = layout.Shortcuts ?? ShortcutDefaults.All;
                var commandable = loop.FindCommandTarget(null) is not null;
                if (_appliedShortcuts is null || commandable != _appliedCommandable || !_appliedShortcuts.SequenceEqual(wanted))
                {
                    try
                    {
                        // Les refus de cette pose sont rendus une fois ; tant que rien ne change, on ne les répète pas.
                        warnings.AddRange(await hotkeys.ApplyAsync(wanted).ConfigureAwait(false));
                        _appliedShortcuts = wanted;
                        _appliedCommandable = commandable;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _appliedShortcuts = null; // état inconnu : le prochain rechargement réessaie
                        Trace.TraceWarning($"Rechargement : raccourcis non mis à jour : {ex.Message}");
                        warnings.Add($"Les raccourcis n'ont pas pu être mis à jour : {ex.Message}");
                    }
                }
            }

            Trace.TraceInformation($"Disposition rechargée : {layout.Placements.Count} module(s), {warnings.Count} avertissement(s).");
            return new Reloaded(true, null, warnings);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Transmet aux hôtes des modules complémentaires le plafond d'images (borné à celui de la boucle) et l'apparence de la disposition.
    /// À appeler au démarrage, puis à chaque rechargement.
    /// </summary>
    public static void ApplyHostOptions(ICommunityModules community, DashboardLayout layout) =>
        community.SetDisplayOptions(Math.Clamp(layout.MaxFps ?? HostMaxFpsCap, 1, HostMaxFpsCap), layout.Appearance ?? AppearanceSettings.Default);

    public Task<bool> RestartModuleAsync(Guid instanceId, CancellationToken ct) => loop.RestartAsync(instanceId);

    public async Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query, CancellationToken ct)
    {
        if (registry.Factory(typeId) is not IConfigurableModuleFactory factory) return [];

        try
        {
            return await factory.LookupAsync(fieldKey, query, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Un module en panne (réseau, service distant) ne doit pas faire tomber le service.
            Trace.TraceWarning($"Recherche « {fieldKey} » du module « {typeId} » en échec : {ex.Message}");
            return [];
        }
    }

    public async Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey, CancellationToken ct)
    {
        var layout = loop.Layout;
        var placed = layout.Placements.FirstOrDefault(p => p.InstanceId == instanceId);
        var placement = placed ?? layout.Shelf?.FirstOrDefault(p => p.InstanceId == instanceId);
        if (placement is null) return new ActionResult(false, "Module introuvable dans la disposition.");

        if (registry.Factory(placement.TypeId) is not IConfigurableModuleFactory factory)
            return new ActionResult(false, "Action inconnue.");

        ActionResult result;
        try
        {
            result = await factory.RunActionAsync(actionKey, placement.Settings, registry.SecretsFor(placement.TypeId), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning($"Action « {actionKey} » du module « {placement.TypeId} » en échec : {ex.Message}");
            return new ActionResult(false, $"L'action a échoué : {ex.Message}");
        }

        // Un module gardé ne relit pas ses secrets : on le recrée pour que l'action (oubli d'une empreinte, nouvelle clé)
        // prenne effet tout de suite. Un module rangé n'est pas à l'écran, il n'y a rien à redémarrer.
        if (result.Ok && placed is not null)
        {
            try
            {
                await loop.RestartAsync(instanceId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.TraceWarning($"Redémarrage du module « {placement.TypeId} » après l'action « {actionKey} » en échec : {ex.Message}");
            }
        }

        return result;
    }

    public IDisposable SubscribePreview(int maxFps, Action<PreviewFrame> onFrame) => loop.SubscribePreview(maxFps, onFrame);

    // ---- Mises à jour ----

    private static readonly UpdateInfo NoUpdateInfo =
        new(LinkUpdateState.Unknown, null, false, null, null, LinkUpdateChannel.Stable, false);

    public UpdateInfo GetUpdate() => updates is null ? NoUpdateInfo : ToUpdateInfo(updates.Current);

    public Task CheckUpdatesAsync(CancellationToken ct) => updates?.CheckNowAsync(ct) ?? Task.CompletedTask;

    public Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable, CancellationToken ct) =>
        updates?.SetPreferencesAsync(new UpdatePreferences(ToChannel(channel), waitForNextStable), ct) ?? Task.CompletedTask;

    /// <summary>
    /// Demandé par la fenêtre de réglage : elle est ouverte, on la rouvre après l'installation. Une mise à jour acceptée va jusqu'au bout
    /// même si la fenêtre se ferme : l'installation suit la vie du service, la requête ne fait qu'attendre le résultat.
    /// </summary>
    public async Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade, CancellationToken ct)
    {
        if (updates is null) return new ActionResult(false, "Les mises à jour ne sont pas disponibles pour l'instant.");

        var install = updates.InstallAsync(confirmDowngrade, reopenSettings: true, _lifetime.Token);
        // Si on n'attend plus la requête, l'échec éventuel de l'installation est tout de même journalisé.
        _ = install.ContinueWith(
            t => Trace.TraceError($"Mise à jour : l'installation a échoué : {t.Exception?.GetBaseException()}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        var (ok, message) = await install.WaitAsync(ct).ConfigureAwait(false);
        return new ActionResult(ok, message);
    }

    // ---- Modules complémentaires ----

    public CommunityOverview GetCommunity()
    {
        var modules = community.Entries.Select(ToModuleInfo).ToList();
        var store = community.StoreInfo;
        return new CommunityOverview(modules, store.ModulesRoot, store.DeveloperMode, store.DevFolders, store.Tampered);
    }

    // La mise à jour proposée (version, notes, permissions nouvelles) est celle que GitHub a annoncée et que le service a vérifiée.
    private CommunityModuleInfo ToModuleInfo(CommunityEntry entry)
    {
        var manifest = entry.Manifest;
        var update = community.UpdateFor(entry.Id);
        return new CommunityModuleInfo(
            entry.Id, manifest?.Name ?? entry.Id, manifest?.Author ?? "", manifest?.Description ?? "",
            manifest?.Version ?? entry.Approval?.Version, Enum.Parse<LinkCommunityState>(entry.State.ToString()), entry.Message,
            manifest?.Permissions ?? [], community.GrantedTo(entry), entry.FromDevFolder,
            UpdateVersion: update?.Version, UpdateNotes: update?.Notes, UpdateNewPermissions: update?.NewPermissions ?? []);
    }

    public PackageInfo InspectPackage(string path)
    {
        var inspection = community.Inspect(path);
        var manifest = inspection.Manifest;
        return new PackageInfo(
            manifest?.Id, manifest?.Name, manifest?.Author, manifest?.Version, manifest?.Description, manifest?.Permissions ?? [],
            inspection.Errors, inspection.Conflict, inspection.Installed?.Version);
    }

    public Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        community.InstallAsync(path, granted, ct);

    public ActionResult ApproveModule(string id, IReadOnlyList<ModulePermission> granted) => community.Approve(id, granted);

    public Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        community.SetPermissionsAsync(id, granted);

    public Task<ActionResult> UninstallModuleAsync(string id, CancellationToken ct) => community.UninstallAsync(id);

    public Task<ActionResult> RelaunchModuleAsync(string id, CancellationToken ct) => community.RelaunchAsync(id);

    public ActionResult SetModulesRoot(string path) => community.SetModulesRoot(path);

    public ActionResult SetDeveloperMode(bool on) => community.SetDeveloperMode(on);

    public ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted) => community.AddDevFolder(path, granted);

    public ActionResult RemoveDevFolder(string path) => community.RemoveDevFolder(path);

    public Task<ActionResult> ReloadDevModuleAsync(string id, CancellationToken ct) => community.ReloadDevModuleAsync(id);

    public Task CheckModuleUpdatesAsync(CancellationToken ct) => community.CheckUpdatesAsync(ct);

    public Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        community.InstallUpdateAsync(id, granted, ct);

    // Lire la consommation fait passer les relevés des modules à un toutes les 5 s tant que la fenêtre continue de la lire.
    public IReadOnlyList<ModuleConsumption> GetConsumption() => community.Consumption(meter.Read());

    /// <summary>L'arrêt du service annule l'installation en cours et arrête d'écouter les modules complémentaires.</summary>
    public void Dispose()
    {
        _watcher.Dispose();
        _lifetime.Cancel();
    }

    /// <summary>L'état des mises à jour tel que la fenêtre de réglage le connaît.</summary>
    public static UpdateInfo ToUpdateInfo(UpdateStatus status) => new(
        Enum.Parse<LinkUpdateState>(status.State.ToString()), status.Version, status.IsDowngrade, status.Percent, status.Message,
        status.Preferences.Channel == UpdateChannel.Beta ? LinkUpdateChannel.Beta : LinkUpdateChannel.Stable,
        status.Preferences.WaitForNextStable);

    private static UpdateChannel ToChannel(LinkUpdateChannel channel) =>
        channel == LinkUpdateChannel.Beta ? UpdateChannel.Beta : UpdateChannel.Stable;

    private static string ReadVersion()
    {
        var info = typeof(ServiceControlHandler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return typeof(ServiceControlHandler).Assembly.GetName().Version?.ToString(3) ?? "0";
        var plus = info.IndexOf('+'); // « 0.2.0+hash » : sans le hash du commit
        return plus > 0 ? info[..plus] : info;
    }
}
