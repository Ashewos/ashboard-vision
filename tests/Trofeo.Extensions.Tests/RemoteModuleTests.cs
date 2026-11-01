using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// La case d'un module de la communauté vue par la boucle d'affichage : elle pose la dernière image reçue sans jamais attendre
/// l'hôte. Les tests d'image tournent avec le vrai hôte et le module d'essai ; ceux de délai et de panne jouent l'hôte à la main.
/// </summary>
[Collection("Hosts")]
public sealed class RemoteModuleTests : IAsyncLifetime
{
    private readonly ModuleSandbox _folders = new();
    private readonly List<SupervisorRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        _folders.Dispose();
    }

    private SupervisorRig FakeRig(FakeHostFactory factory)
    {
        var rig = new SupervisorRig(ModuleSandbox.Ready(_folders.SmallModule(), ModuleSandbox.Manifest(entry: "module.dll")), factory);
        _rigs.Add(rig);
        return rig;
    }

    private (SupervisorRig Rig, PlainHostProcessFactory Factory) RealRig()
    {
        var factory = new PlainHostProcessFactory();
        var rig = new SupervisorRig(ModuleSandbox.Ready(_folders.SampleCopy(), ModuleSandbox.Manifest()), factory);
        _rigs.Add(rig);
        return (rig, factory);
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.Pixels.All(pixel => pixel.Alpha == 0);

    [WindowsHostFact]
    public async Task Render_BeforeFirstFrame_DrawsNothing()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();

        using var first = rig.Draw(module);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();
        using var second = rig.Draw(module);

        Assert.True(IsEmpty(first));
        Assert.True(IsEmpty(second));
        Assert.Equal(ModuleHealth.Online, module.Status.Health);
    }

    [WindowsHostFact]
    public async Task Render_AfterFrame_DrawsItsPixels()
    {
        var (rig, _) = RealRig();
        var module = rig.Open();
        var accent = Themes.ClassiqueDefinition.Theme.Accent;

        SKBitmap? drawn = null;
        using (rig.Draw(module))
        {
        }

        await TestWait.UntilAsync(() =>
        {
            drawn?.Dispose();
            drawn = rig.Draw(module);
            return !IsEmpty(drawn);
        });
        using var bitmap = drawn!;

        // La moitié gauche du module d'essai est rouge, la moitié droite a la couleur d'accent du thème ; la case est décalée de 10 px.
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(10 + 10, 10 + 24));
        var right = bitmap.GetPixel(10 + 85, 10 + 24);
        Assert.Equal((accent.Red, accent.Green, accent.Blue), (right.Red, right.Green, right.Blue));
        Assert.Equal(0, bitmap.GetPixel(2, 2).Alpha); // hors de la case : rien
    }

    [WindowsHostFact]
    public async Task Render_NoFrameFor10Seconds_SaysNotResponding()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();

        rig.Time.Advance(TimeSpan.FromSeconds(9.9));
        using (var early = rig.Draw(module))
        {
            Assert.Equal(ModuleHealth.Online, module.Status.Health);
            Assert.True(IsEmpty(early));
        }

        var statusChanged = 0;
        module.StatusChanged += (_, _) => Interlocked.Increment(ref statusChanged);
        rig.Time.Advance(TimeSpan.FromSeconds(0.2));
        using (var late = rig.Draw(module))
        {
            Assert.Equal(new ModuleStatus(ModuleHealth.Offline, "Le module ne répond pas"), module.Status);
            Assert.False(IsEmpty(late)); // le message est dessiné
        }

        Assert.Equal(1, statusChanged);

        // Dès qu'une image arrive, la case retrouve son état.
        await host.SendFrameAsync(open, 0, SKColors.Blue);
        await TestWait.UntilAsync(() => module.Status.Health == ModuleHealth.Online);
        using var recovered = rig.Draw(module);
        Assert.Equal(SKColors.Blue, recovered.GetPixel(10 + 5, 10 + 5));
    }

    [WindowsHostFact]
    public async Task Frame_IsCopiedThenAcknowledgedThenInvalidated()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        module.Invalidated += (_, _) => invalidated.TrySetResult();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        await host.SendFrameAsync(open, 1, SKColors.Green);

        var done = await host.WaitAsync<FrameDone>();
        await invalidated.Task.WaitAsync(TestWait.Patience);
        Assert.Equal((open.InstanceId, 1), (done.InstanceId, done.Buffer));
        using var bitmap = rig.Draw(module);
        Assert.Equal(SKColors.Green, bitmap.GetPixel(10 + 5, 10 + 5));
    }

    [WindowsHostFact]
    public async Task HostKilledMidFrame_RenderDoesNotThrowAndKeepsLastImage()
    {
        var (rig, factory) = RealRig();
        var module = rig.Open(settings: """{"mode":"animated"}""");
        using (rig.Draw(module))
        {
        }

        // L'hôte dessine en continu ; un fil de dessin tourne pendant qu'on le tue.
        await TestWait.UntilAsync(() =>
        {
            using var bitmap = rig.Draw(module);
            return !IsEmpty(bitmap);
        });
        var failures = new List<Exception>();
        using var stop = new CancellationTokenSource();
        var renderer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var bitmap = rig.Draw(module);
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            }
        });

        factory.Last.Kill();
        await TestWait.UntilAsync(() => module.Status == new ModuleStatus(ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText));
        await Task.Delay(200);
        await stop.CancelAsync();
        await renderer;

        Assert.Empty(failures);
        Assert.False(module.IsAnimating);
        using var last = rig.Draw(module);
        var pixel = last.GetPixel(10 + 10, 10 + 24);
        Assert.True(pixel.Alpha > 0 && pixel.Red > pixel.Green + 20 && pixel.Red > pixel.Blue + 20, $"La dernière image devrait rester (rouge, assombrie) : {pixel}");
    }

    [WindowsHostFact]
    public async Task HostDiesInTheMiddleOfAMessage_IsACrashNotAnException()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        await host.SendFrameAsync(open, 0, SKColors.Red);
        await TestWait.UntilAsync(() => !IsEmpty(rig.Draw(module)));

        // Une trame annonce 200 octets, n'en livre que 10, puis l'hôte disparaît.
        host.WriteRaw([200, 0, 0, 0, (byte)'{', (byte)'"', (byte)'t', (byte)'y', (byte)'p', (byte)'e', (byte)'"', (byte)':', (byte)'"', (byte)'f']);
        host.Exit(1);

        await TestWait.UntilAsync(() => module.Status == new ModuleStatus(ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText));
        using var bitmap = rig.Draw(module);
        Assert.False(IsEmpty(bitmap));
    }

    [WindowsHostFact]
    public async Task Render_WithAnotherSize_ReopensTheInstanceWithNewImages()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module, 96, 48))
        {
        }

        var host = await factory.WaitForStartAsync();
        var first = await host.WaitAsync<Open>();
        using (rig.Draw(module, 96, 48))
        {
        }

        using (rig.Draw(module, 120, 60, columns: 3))
        {
        }

        var second = await host.WaitAsync<Open>(open => open.InstanceId != first.InstanceId);
        await host.WaitAsync<Close>(close => close.InstanceId == first.InstanceId);
        Assert.Equal(1, host.Of<Open>().Count(open => open.InstanceId == first.InstanceId));
        Assert.Equal((120, 60, 3), (second.Width, second.Height, second.Columns));

        // Une image de l'ancienne taille arrivée en retard n'est jamais posée.
        await host.SendFrameAsync(first, 0, SKColors.Red);
        await Task.Delay(100);
        using var bitmap = rig.Draw(module, 120, 60, columns: 3);
        Assert.True(IsEmpty(bitmap));
    }

    [WindowsHostFact]
    public async Task Dispose_ThenRender_DoesNothing()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        module.Dispose();
        module.Dispose();

        using var bitmap = rig.Draw(module);
        Assert.True(IsEmpty(bitmap));
        Assert.Equal(0, rig.Supervisor.ActiveInstances);
    }
}
