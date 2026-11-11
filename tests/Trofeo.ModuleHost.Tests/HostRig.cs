using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using AshboardVision.Sdk;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;

namespace Trofeo.ModuleHost.Tests;

/// <summary>
/// Un faux service : lance le vrai hôte de module (sans enfermement) par deux tubes anonymes ordinaires, lui parle avec le
/// protocole et garde tout ce qu'il écrit. Les tests attendent un message avec <see cref="WaitAsync{T}"/> ; ils tournent l'un
/// après l'autre (une seule collection) pour qu'un hôte n'hérite jamais des poignées d'un autre.
/// </summary>
internal sealed class HostRig : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly AnonymousPipeServerStream _toHost;
    private readonly AnonymousPipeServerStream _fromHost;
    private readonly MessageChannel _channel;
    private readonly List<HostMessage> _received = [];
    private readonly List<FrameBuffers> _buffers = [];
    private readonly Task _reader;

    private HostRig(Process process, AnonymousPipeServerStream toHost, AnonymousPipeServerStream fromHost)
    {
        Process = process;
        _toHost = toHost;
        _fromHost = fromHost;
        _channel = new MessageChannel(fromHost, toHost);
        _reader = Task.Run(ReadAsync);
    }

    public Process Process { get; }

    /// <summary>Appelé pour chaque message de l'hôte (dans le fil de lecture : ne pas y bloquer).</summary>
    public Func<HostMessage, Task>? Handler { get; set; }

    /// <summary>Si vrai, chaque <see cref="Frame"/> reçue est aussitôt acquittée par un <see cref="FrameDone"/>.</summary>
    public bool AutoFrameDone { get; set; }

    public static string BaseFolder => AppContext.BaseDirectory;

    public static string SampleFolder => Path.Combine(BaseFolder, "sample");

    public static HostRig Start(string? hostExe = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var toHost = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var exe = hostExe ?? Path.Combine(BaseFolder, "host", "Trofeo.ModuleHost.exe");
        var startInfo = new ProcessStartInfo(exe, $"--in {toHost.GetClientHandleAsString()} --out {fromHost.GetClientHandleAsString()}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        var process = Process.Start(startInfo)!;
        toHost.DisposeLocalCopyOfClientHandle();
        fromHost.DisposeLocalCopyOfClientHandle();
        return new HostRig(process, toHost, fromHost);
    }

    public IReadOnlyList<HostMessage> Messages
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public IEnumerable<T> Of<T>() where T : HostMessage => Messages.OfType<T>();

    public Task SendAsync(HostMessage message) => _channel.SendAsync(message, CancellationToken.None);

    public Task SendHelloAsync(string folder, string entry, ModulePermission[]? granted = null, int maxFps = 30, string appearance = "classique")
        => SendAsync(new Hello(HostMessage.ProtocolVersion, folder, entry, "test.sample", granted ?? [], Path.GetTempPath(),
            HostPayloads.AppearanceToJson(new AppearanceSettings(appearance)), maxFps));

    public Task SendSampleHelloAsync(ModulePermission[]? granted = null, int maxFps = 30)
        => SendHelloAsync(SampleFolder, "Trofeo.ModuleHost.Tests.SampleModule.dll", granted, maxFps);

    /// <summary>Ouvre une instance avec de vrais tampons partagés, dupliqués dans l'hôte.</summary>
    public async Task<OpenedInstance> OpenAsync(string settingsJson = "{}", int width = 96, int height = 48, int columns = 2, int rows = 1)
    {
        var buffers = new FrameBuffers(width, height);
        lock (_buffers)
        {
            _buffers.Add(buffers);
        }

        var handle = DuplicateIntoHost(buffers.Section);
        var id = Guid.NewGuid();
        using var settings = JsonDocument.Parse(settingsJson);
        await SendAsync(new Open(id, settings.RootElement.Clone(), width, height, 1.25f, columns, rows, handle, 0));
        return new OpenedInstance(id, buffers, width, height);
    }

    /// <summary>Attend (sans consommer) le premier message de ce type qui répond au filtre.</summary>
    public async Task<T> WaitAsync<T>(Func<T, bool>? filter = null, TimeSpan? timeout = null) where T : HostMessage
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? Patience;
        while (true)
        {
            var found = Of<T>().FirstOrDefault(message => filter?.Invoke(message) ?? true);
            if (found is not null)
            {
                return found;
            }

            if (deadline.Elapsed > limit)
            {
                throw new TimeoutException($"Aucun message {typeof(T).Name} dans les {limit.TotalSeconds:F0} s. Reçus : " +
                    string.Join(" | ", Messages.Select(m => m.ToString())));
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Attend qu'une condition sur les messages reçus soit vraie.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > (timeout ?? Patience))
            {
                throw new TimeoutException("Condition jamais remplie. Reçus : " + string.Join(" | ", Messages.Select(m => m.ToString())));
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Attend une ligne du journal du module qui contient le texte.</summary>
    public async Task<Log> WaitForLogAsync(string text, TimeSpan? timeout = null) =>
        await WaitAsync<Log>(log => log.Message.Contains(text, StringComparison.Ordinal), timeout);

    public async Task<int> WaitForExitAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? Patience);
        await Process.WaitForExitAsync(cts.Token);
        return Process.ExitCode;
    }

    /// <summary>
    /// Fait ce que fera le service pour un <see cref="LocalConnect"/> : deux tubes anonymes, dupliqués dans l'hôte, dont le bout
    /// « service » est confié à <paramref name="serve"/> (qui tourne à part ; le flux est fermé quand elle se termine).
    /// </summary>
    public async Task RelayAsync(LocalConnect connect, Func<Stream, Task> serve)
    {
        var toModule = new AnonymousPipeServerStream(PipeDirection.Out);
        var fromModule = new AnonymousPipeServerStream(PipeDirection.In);
        var readHandle = DuplicateIntoHost(toModule.ClientSafePipeHandle);
        var writeHandle = DuplicateIntoHost(fromModule.ClientSafePipeHandle);
        toModule.DisposeLocalCopyOfClientHandle();
        fromModule.DisposeLocalCopyOfClientHandle();
        _ = Task.Run(async () =>
        {
            using var pair = new PairStream(fromModule, toModule);
            try
            {
                await serve(pair);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // le client a refusé ou fermé : rien d'autre à faire côté serveur
            }
        });
        await SendAsync(new LocalConnected(connect.Id, readHandle, writeHandle));
    }

    /// <summary>Ferme le côté « service vers hôte » : l'hôte doit s'arrêter.</summary>
    public void CloseInput() => _toHost.Dispose();

    /// <summary>Duplique une poignée de ce processus dans l'hôte et renvoie sa valeur là-bas.</summary>
    public long DuplicateIntoHost(SafeHandle source)
    {
        if (!DuplicateHandle(GetCurrentProcess(), source, Process.SafeHandle, out var target, 0, false, 2 /* DUPLICATE_SAME_ACCESS */))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return target.ToInt64();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        await Process.WaitForExitAsync();
        Process.Dispose();
        _toHost.Dispose();
        _fromHost.Dispose();
        await _reader.WaitAsync(TimeSpan.FromSeconds(10));
        lock (_buffers)
        {
            foreach (var buffers in _buffers)
            {
                buffers.Dispose();
            }
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var message in _channel.ReadAllAsync(CancellationToken.None))
            {
                lock (_received)
                {
                    _received.Add(message);
                }

                if (AutoFrameDone && message is Frame frame)
                {
                    await SendAsync(new FrameDone(frame.InstanceId, frame.Buffer));
                }

                if (Handler is { } handler)
                {
                    await handler(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // le tube s'est fermé avec l'hôte
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle sourceHandle, SafeProcessHandle targetProcess,
        out IntPtr target, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}

/// <summary>Une instance ouverte : ses tampons partagés et sa taille.</summary>
internal sealed record OpenedInstance(Guid Id, FrameBuffers Buffers, int Width, int Height)
{
    /// <summary>Copie le tampon dans un bitmap pour lire les pixels dessinés.</summary>
    public SKBitmap Read(int buffer)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Buffers.CopyTo(buffer, bitmap);
        return bitmap;
    }
}

/// <summary>Deux tubes vus comme un seul flux (le bout « service » d'une connexion relayée).</summary>
internal sealed class PairStream : Stream
{
    private readonly Stream _read;
    private readonly Stream _write;

    public PairStream(Stream read, Stream write)
    {
        _read = read;
        _write = write;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    public override void Flush() => _write.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _write.Dispose();
            _read.Dispose();
        }

        base.Dispose(disposing);
    }
}
