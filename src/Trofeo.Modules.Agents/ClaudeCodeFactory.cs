using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Agents;

/// <summary>
/// Fabrique du module Claude Code. <paramref name="createSource"/> choisit la source de l'état (les tests y mettent
/// une source factice). <paramref name="installer"/> installe le mod des quotas ; absent, l'action répond que le dossier du mod
/// est introuvable.
/// </summary>
public sealed class ClaudeCodeFactory(Func<ModuleContext, IAgentSource> createSource, QuotaModInstaller? installer = null)
    : IConfigurableModuleFactory
{
    private const string InstallQuotas = "install-quotas";

    private static readonly AgentFlavor Flavor = new(
        "Aucune session Claude Code ouverte",
        "Claude Code n'est pas installé sur ce PC",
        ClaudeMascot.Definition);

    public ModuleDescriptor Descriptor { get; } = new(
        "claudecode",
        "Claude Code",
        new GridSize(6, 3),
        new Dictionary<SizePreset, GridSize>
        {
            [SizePreset.Compact] = new GridSize(6, 3),
            [SizePreset.Ideal] = new GridSize(8, 4),
            [SizePreset.Large] = new GridSize(10, 4),
        },
        AcceptsCommands: true);

    public SettingsSchema Schema { get; } = new(
    [
        .. AgentSchema.CommonFields(),
        new ActionField(InstallQuotas, "Installer le mod des quotas")
        {
            Confirm = "Le mod ashboard-quotas sera ajouté à Claude Code pour ton compte (claude plugin install). "
                + "Tu pourras le retirer avec claude plugin uninstall ashboard-quotas@ashboard-vision.",
        },
    ]);

    /// <summary>
    /// Fabrique de production : l'état est lu dans le dossier de Claude Code de l'utilisateur. <paramref name="modsDir"/> est le
    /// dossier <c>mods</c> à installer (voir <c>AppLocator.FindDirectory("mods")</c> dans Trofeo.Core, que ce projet ne référence
    /// pas) ; <c>claude</c> est lancé par le <c>PATH</c>.
    /// </summary>
    public static ClaudeCodeFactory CreateProduction(string? modsDir = null) =>
        new(_ => new ClaudeSource(ClaudeHome.Resolve(), ClaudeSource.IsProcessAlive),
            new QuotaModInstaller(QuotaModInstaller.RunProcessAsync, modsDir));

    public Task<ActionResult> RunActionAsync(string actionKey, JsonElement settings, ISecretStore secrets, CancellationToken ct) =>
        actionKey == InstallQuotas
            ? (installer ?? new QuotaModInstaller(QuotaModInstaller.RunProcessAsync, null)).InstallAsync(ct)
            : Task.FromResult(new ActionResult(false, "Action inconnue."));

    public IModule Create(ModuleContext context) =>
        new AgentModule(Descriptor, Flavor, createSource(context), AgentSettings.Parse(context.Settings), context.Time, context.Log, Random.Shared);
}
