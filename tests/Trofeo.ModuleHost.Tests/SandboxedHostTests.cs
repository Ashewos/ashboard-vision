using System.IO.Pipes;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.ModuleHost.Tests;

/// <summary>
/// Le vrai chemin de production, une fois : l'hôte lancé par <see cref="SandboxedProcess"/> (AppContainer et job object), ses
/// tubes hérités par poignée, sa section d'images dupliquée par <see cref="SandboxedProcess.DuplicateInto"/>. Sert à vérifier que
/// l'hôte démarre vraiment enfermé (chargement du module, SkiaSharp natif, polices) ; les autres tests le lancent sans enfermement.
/// </summary>
[Trait("Windows", "Sandbox")]
public sealed class SandboxedHostTests : IDisposable
{
    private readonly List<string> _profiles = [];
    private readonly List<string> _folders = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        foreach (var id in _profiles)
        {
            AppContainerProfile.Delete(id);
        }

        foreach (var folder in _folders)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [WindowsHostFact]
    public async Task Host_RunsInsideTheRealSandbox_DrawsAFrameAndStopsCleanly()
    {
        var id = "test.hostsmoke-" + Guid.NewGuid().ToString("N")[..12];
        _profiles.Add(id);
        var profile = AppContainerProfile.Ensure(id);
        var folder = Path.Combine(Path.GetTempPath(), "hostsmoke-" + Guid.NewGuid().ToString("N")[..12]);
        _folders.Add(folder);
        CopyDirectory(Path.Combine(HostRig.BaseFolder, "host"), Path.Combine(folder, "host"));
        CopyDirectory(HostRig.SampleFolder, Path.Combine(folder, "sample"));
        profile.GrantRead(folder);

        var job = Track(new ModuleJob());
        var toHost = Track(new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable));
        var fromHost = Track(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable));
        var arguments = $"--in {toHost.GetClientHandleAsString()} --out {fromHost.GetClientHandleAsString()}";
        var process = Track(SandboxedProcess.Start(new SandboxStart(Path.Combine(folder, "host", "Trofeo.ModuleHost.exe"), arguments, profile, [],
            [toHost.ClientSafePipeHandle, fromHost.ClientSafePipeHandle], job)));
        toHost.DisposeLocalCopyOfClientHandle();
        fromHost.DisposeLocalCopyOfClientHandle();

        await using var channel = new MessageChannel(fromHost, toHost);
        var received = new List<HostMessage>();
        var reader = Task.Run(async () =>
        {
            await foreach (var message in channel.ReadAllAsync(CancellationToken.None))
            {
                lock (received)
                {
                    received.Add(message);
                }
            }
        });

        await channel.SendAsync(new Hello(HostMessage.ProtocolVersion, Path.Combine(folder, "sample"), "Trofeo.ModuleHost.Tests.SampleModule.dll", "test.hostsmoke",
            [], profile.DataDirectory, HostPayloads.AppearanceToJson(new AppearanceSettings("classique", Font: "Manrope")), 30), CancellationToken.None);
        await WaitForAsync<Ready>(received);

        using var buffers = new FrameBuffers(96, 48);
        var handle = process.DuplicateInto(buffers.Section).ToInt64();
        var instance = Guid.NewGuid();
        using var settings = System.Text.Json.JsonDocument.Parse("""{"color":"#FF8000"}""");
        await channel.SendAsync(new Open(instance, settings.RootElement.Clone(), 96, 48, 1f, 2, 1, handle, 0), CancellationToken.None);

        var frame = await WaitForAsync<Frame>(received);
        using var bitmap = new SKBitmap(new SKImageInfo(96, 48, SKColorType.Bgra8888, SKAlphaType.Premul));
        buffers.CopyTo(frame.Buffer, bitmap);
        Assert.Equal(new SKColor(255, 128, 0), bitmap.GetPixel(5, 5));

        await channel.SendAsync(new Shutdown(), CancellationToken.None);
        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30)));
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(job.Read().MemoryLimitHit);
        Assert.Empty(received.OfType<Failure>());
    }

    private static async Task<T> WaitForAsync<T>(List<HostMessage> received) where T : HostMessage
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (received)
            {
                if (received.OfType<T>().FirstOrDefault() is { } found)
                {
                    return found;
                }

                if (received.OfType<Failure>().FirstOrDefault() is { } failure)
                {
                    throw new InvalidOperationException("L'hôte enfermé a répondu Failure : " + failure.Message);
                }
            }

            if (deadline.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"Aucun {typeof(T).Name} de l'hôte enfermé en 30 s.");
            }

            await Task.Delay(10);
        }
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
