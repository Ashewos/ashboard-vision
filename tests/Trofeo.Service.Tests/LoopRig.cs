using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Device;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Display.Tests;

/// <summary>Un module qui compte ce qu'on lui fait ; tient lieu de module réel pour la mise à jour à chaud.</summary>
internal sealed class ProbeModule(string typeId, Guid instanceId, JsonElement settings) : IModule, ICommandableModule, IModuleNotices
{
    private int _disposed;
    private int _starts;

    public Guid InstanceId { get; } = instanceId;
    public JsonElement Settings { get; } = settings;
    public int Disposed => Volatile.Read(ref _disposed);
    public int Starts => Volatile.Read(ref _starts);
    public List<SKRect> Rects { get; } = [];
    public IReadOnlyList<string> Notices { get; set; } = [];
    public ModuleStatus Status { get; set; } = new(ModuleHealth.Online);
    public bool IsAnimating { get; set; }
    public ModuleDescriptor Descriptor { get; } = new(typeId, typeId, new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
    public event EventHandler? Invalidated { add { } remove { } }
    public event EventHandler? StatusChanged { add { } remove { } }
    public Task StartAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _starts);
        return Task.CompletedTask;
    }

    public void Render(SKCanvas canvas, ModuleRenderContext ctx) => Rects.Add(ctx.Bounds);
    public bool HandleCommand(ModuleCommand command) => true;
    public void Dispose() => Interlocked.Increment(ref _disposed);
}

/// <summary>Un fond qui note s'il a été libéré.</summary>
internal sealed class TrackedBackground(SKColor color) : IBackground
{
    private readonly ColorBackground _inner = new(color);
    public bool Disposed { get; private set; }
    public int DisposeCount { get; private set; }
    public bool IsAnimated => false;
    public SKImage Current => _inner.Current;
    public int Version => 1;
    public bool Advance(TimeSpan elapsed) => false;
    public TimeSpan? UntilNextFrame => null;

    public void Dispose()
    {
        Disposed = true;
        DisposeCount++;
        _inner.Dispose();
    }
}

/// <summary>Banc d'essai de la boucle avec deux types de modules (« fake » et « other ») dont les instances sont suivies.</summary>
internal sealed class LoopRig : IDisposable
{
    public static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed class ProbeFactory(string typeId, LoopRig rig) : IModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = new(typeId, typeId, new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context)
        {
            var module = new ProbeModule(typeId, context.InstanceId, context.Settings);
            lock (rig.Created) rig.Created.Add(module);
            return module;
        }
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    public FakeUsbTransportFactory Usb { get; } = new();
    public ScreenDevice Screen { get; }
    public FrameScheduler Scheduler { get; }
    public DisplayLoop Loop { get; }
    public List<ProbeModule> Created { get; } = [];
    public ModuleRegistry Registry { get; }

    /// <param name="extraFactories">Des types de modules en plus de « fake » et « other ».</param>
    /// <param name="secrets">Le coffre de chaque type ; par défaut un coffre en mémoire neuf à chaque appel.</param>
    public LoopRig(
        DashboardLayout? layout = null, IBackground? background = null, ThemeDefinition? theme = null,
        IReadOnlyList<IModuleFactory>? extraFactories = null, Func<string, ISecretStore>? secrets = null,
        DisplayedViewStore? viewStore = null)
    {
        Screen = new ScreenDevice(Usb, Time, static () => []);
        Scheduler = new FrameScheduler(Time);
        IModuleFactory[] factories = [new ProbeFactory("fake", this), new ProbeFactory("other", this), .. extraFactories ?? []];
        Registry = new ModuleRegistry(factories, secrets ?? (_ => new FakeSecretStore()));
        Loop = new DisplayLoop(layout ?? Layout(Place(A, "fake")), Registry, Screen, Scheduler, Time, theme, background, viewStore);
    }

    public static JsonElement Json(string json = "{}") => JsonDocument.Parse(json).RootElement.Clone();

    public static ModulePlacement Place(
        Guid id, string type = "fake", int column = 0, int row = 0, int columns = 4, int rows = 2, string settings = "{}",
        TextSize textSize = TextSize.Normal) =>
        new(id, type, column, row, columns, rows, textSize, Json(settings));

    public static DashboardLayout Layout(params ModulePlacement[] placements) => DashboardLayout.SingleView(180, placements);

    public IEnumerable<ProbeModule> Of(Guid id) => Created.Where(m => m.InstanceId == id);

    public FakeUsbTransport AddHealthyScreen(int acks = 200)
    {
        var transport = new FakeUsbTransport().EnqueueReadTimeout().EnqueueHandshakeReply();
        for (var i = 0; i < acks; i++) transport.EnqueueAck();
        Usb.EnqueueTransport(transport);
        return transport;
    }

    public void Dispose()
    {
        Loop.Dispose();
        Screen.Dispose();
        Scheduler.Dispose();
    }
}

/// <summary>Un fond animé qui change à chaque avance et note les temps demandés.</summary>
internal sealed class StepBackground : IBackground
{
    private readonly ColorBackground _inner = new(SKColors.DarkSlateBlue);
    private int _version;
    public List<TimeSpan> AdvanceTimes { get; } = [];
    public bool Disposed { get; private set; }
    public bool IsAnimated => true;
    public SKImage Current => _inner.Current;
    public int Version => _version;
    public TimeSpan? UntilNextFrame => TimeSpan.FromMilliseconds(100);

    public bool Advance(TimeSpan elapsed)
    {
        AdvanceTimes.Add(elapsed);
        _version++;
        return true;
    }

    public void Dispose()
    {
        Disposed = true;
        _inner.Dispose();
    }
}
