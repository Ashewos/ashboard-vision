using SkiaSharp;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Service;

/// <summary>
/// La fabrique d'un module présent mais qui ne peut pas tourner (pas encore validé, fichiers modifiés, mis de côté,
/// trop ancien…) : sa case existe dans la disposition et dit pourquoi, en <see cref="Theme.Warning"/>, sans qu'aucun code du module
/// ne soit chargé. Son état est <see cref="ModuleHealth.NotConfigured"/>.
/// </summary>
public sealed class PlaceholderModuleFactory : IModuleFactory
{
    /// <summary>Ce que dit la case d'un module à valider.</summary>
    public const string NeedsApprovalText = "À valider dans la fenêtre de réglage";

    /// <summary>Ce que dit la case d'un module dont le contenu a changé depuis sa validation.</summary>
    public const string ChangedText = ModuleHostSupervisor.FilesChangedText;

    private readonly string _message;

    /// <param name="entry">Le module tel que le catalogue le voit.</param>
    public PlaceholderModuleFactory(CommunityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _message = MessageFor(entry);
        Descriptor = entry.Manifest?.ToDescriptor()
            ?? new ModuleDescriptor(entry.Id, entry.Id, new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
    }

    /// <inheritdoc />
    public ModuleDescriptor Descriptor { get; }

    /// <summary>La phrase que la case affiche.</summary>
    public string Message => _message;

    /// <inheritdoc />
    public IModule Create(ModuleContext context) => new PlaceholderModule(Descriptor, _message);

    private static string MessageFor(CommunityEntry entry) => entry.State switch
    {
        CommunityState.NeedsApproval => NeedsApprovalText,
        CommunityState.Changed => ChangedText,
        CommunityState.SetAside => entry.Message is { Length: > 0 } reason && reason.StartsWith("Mis de côté", StringComparison.Ordinal)
            ? reason
            : "Mis de côté : " + (entry.Message ?? "le module s'est arrêté trop souvent."),
        _ => entry.Message is { Length: > 0 } message ? message : "Module indisponible",
    };

    private sealed class PlaceholderModule(ModuleDescriptor descriptor, string message) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;

        public ModuleStatus Status { get; } = new(ModuleHealth.NotConfigured, message);

        public bool IsAnimating => false;

        public event EventHandler? Invalidated
        {
            add { }
            remove { }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public void Render(SKCanvas canvas, ModuleRenderContext ctx) =>
            ModuleDrawing.DrawActionMessage(canvas, ctx.Bounds, ctx.Theme, ctx.TextScale, message);

        public void Dispose()
        {
        }
    }
}
