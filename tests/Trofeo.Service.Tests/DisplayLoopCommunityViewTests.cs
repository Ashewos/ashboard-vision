using System.IO.Pipes;
using System.Runtime.InteropServices;
using AshboardVision.Sdk;
using Microsoft.Win32.SafeHandles;
using Trofeo.Core.Layout;
using Trofeo.Extensions;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

/// <summary>Un test qui a besoin des tubes et de la mémoire partagée de Windows : ignoré ailleurs.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Réservé à Windows (tubes anonymes et sections partagées).";
        }
    }
}

/// <summary>
/// Une case de module de la communauté qui naît dans une vue cachée, avec la vraie boucle d'affichage et le vrai superviseur
/// (seul l'hôte est joué à la main) : l'instance est ouverte une seule fois, à la taille qu'aura la case une fois affichée.
/// </summary>
public sealed class DisplayLoopCommunityViewTests : IAsyncLifetime
{
    private static readonly Guid V1 = new("11110000-0000-0000-0000-000000000001");
    private static readonly Guid V2 = new("22220000-0000-0000-0000-000000000002");
    private static readonly Guid P1 = new("11110000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = new("22220000-0000-0000-0000-0000000000a2");

    private readonly RecordingHostFactory _hosts = new();
    private ModuleHostSupervisor? _supervisor;
    private LoopRig? _rig;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _rig?.Dispose(); // ferme les cases, donc les instances
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }
    }

    private sealed class CommunityFactory(ModuleHostSupervisor supervisor, ModuleDescriptor descriptor) : IModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;

        public IModule Create(ModuleContext context) => supervisor.Open(context);
    }

    private sealed class NoData : IServiceDataBroker
    {
        public DataResult<SensorReading> Sensors() => new(DataStatus.Unavailable, null);

        public DataResult<NowPlayingInfo> NowPlaying() => new(DataStatus.Unavailable, null);
    }

    [WindowsOnlyFact]
    public async Task CommunityCaseCreatedInAHiddenView_IsOpenedOnce_AtTheSizeItIsLaterDrawnAt()
    {
        var manifest = new ModuleManifest("test.sample", "Module d'essai", "Pour les tests.", "tests", "1.0.0", new SdkVersion(1, 0), "module.dll", [],
            null, new GridSize(1, 1), new Dictionary<SizePreset, GridSize>(), new SettingsSchema([]), null);
        var entry = new CommunityEntry(manifest.Id, Path.GetTempPath(), manifest, CommunityState.Ready, null, null, FromDevFolder: true);
        _supervisor = new ModuleHostSupervisor(entry, [], "host.exe", new FakeSecretStore(), new NoData(), TimeProvider.System, (_, _) => { }, _hosts);

        // La case est dans la deuxième vue, plus large, avec un texte agrandi : toute différence de calcul entre l'annonce et le dessin se verrait.
        var layout = new DashboardLayout(
            180,
            [
                new LayoutView(V1, "Vue 1", [Place(P1, "fake")]),
                new LayoutView(V2, "Vue 2", [Place(P2, manifest.Id, column: 2, row: 1, columns: 6, rows: 3, textSize: TextSize.Large)]),
            ]);
        _rig = new LoopRig(layout, extraFactories: [new CommunityFactory(_supervisor, manifest.ToDescriptor())]);
        _rig.AddHealthyScreen(acks: 100);

        _rig.Loop.Start(CancellationToken.None);

        var host = await _hosts.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        var hidden = await host.WaitAsync<Visibility>();
        Assert.Equal((open.InstanceId, false), (hidden.InstanceId, hidden.Visible));

        // La vue de la case s'affiche, puis l'écran la dessine.
        Assert.True(_rig.Loop.ShowView(V2, +1));
        _rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        _rig.Loop.Tick();
        var shown = await host.WaitAsync<Visibility>(v => v.Visible);
        await Task.Delay(300);

        Assert.Equal(open.InstanceId, shown.InstanceId);
        Assert.Single(host.Of<Open>()); // jamais rouverte : la place annoncée est celle du dessin
        Assert.Empty(host.Of<Close>());
        Assert.Equal((6, 3), (open.Columns, open.Rows));
        Assert.Equal(TextSize.Large.Factor(), open.TextScale);
    }
}

/// <summary>Un faux hôte : pas de processus, les tubes sont lus et gardés. Juste ce qu'il faut pour voir ce que le service lui écrit.</summary>
internal sealed class RecordingHostFactory : IHostProcessFactory
{
    private readonly List<RecordingHost> _started = [];

    public IHostProcess Start(HostStart start)
    {
        var host = new RecordingHost(start);
        lock (_started)
        {
            _started.Add(host);
        }

        return host;
    }

    public async Task<RecordingHost> WaitForStartAsync()
    {
        for (var i = 0; i < 3000; i++)
        {
            lock (_started)
            {
                if (_started.Count > 0)
                {
                    return _started[0];
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Aucun hôte lancé.");
    }
}

internal sealed class RecordingHost : IHostProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AnonymousPipeClientStream _in;
    private readonly AnonymousPipeClientStream _out;
    private readonly List<HostMessage> _received = [];

    public RecordingHost(HostStart start)
    {
        _in = new AnonymousPipeClientStream(PipeDirection.In, new SafePipeHandle(Duplicate(start.InheritedHandles[0]), true));
        _out = new AnonymousPipeClientStream(PipeDirection.Out, new SafePipeHandle(Duplicate(start.InheritedHandles[1]), true));
        var channel = new MessageChannel(_in, _out);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in channel.ReadAllAsync(CancellationToken.None))
                {
                    lock (_received)
                    {
                        _received.Add(message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
            {
                // le service a fermé son côté
            }
        });
    }

    public Task<int> Exited => _exited.Task;

    public string DataDirectory { get; } = Path.GetTempPath();

    public IEnumerable<T> Of<T>() where T : HostMessage
    {
        lock (_received)
        {
            return [.. _received.OfType<T>()];
        }
    }

    public async Task<T> WaitAsync<T>(Func<T, bool>? filter = null) where T : HostMessage
    {
        for (var i = 0; i < 3000; i++)
        {
            if (Of<T>().FirstOrDefault(message => filter?.Invoke(message) ?? true) is { } found)
            {
                return found;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Aucun {typeof(T).Name} reçu.");
    }

    public long DuplicateInto(SafeHandle source) => 1; // l'hôte joué ici ne projette jamais les images partagées

    public void Kill() => _exited.TrySetResult(1);

    public JobUsage? ReadUsage() => null;

    public void Dispose()
    {
        _exited.TrySetResult(0);
        _in.Dispose();
        _out.Dispose();
    }

    private static IntPtr Duplicate(SafeHandle source)
    {
        var self = GetCurrentProcess();
        if (!DuplicateHandle(self, source, self, out var copy, 0, false, 2 /* DUPLICATE_SAME_ACCESS */))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return copy;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
