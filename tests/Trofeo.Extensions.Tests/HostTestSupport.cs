using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using AshboardVision.Sdk;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Les tests qui lancent un hôte (vrai ou faux) tournent l'un après l'autre et seuls : un processus lancé sans enfermement hérite
/// de toutes les poignées héritables du moment, et un autre test ne doit jamais en recevoir de celui-ci.
/// </summary>
[CollectionDefinition("Hosts", DisableParallelization = true)]
public sealed class HostsCollection;

/// <summary>Un service de données dont les réponses sont fixées par le test.</summary>
internal sealed class FakeDataBroker : IServiceDataBroker
{
    public DataResult<SensorReading> SensorsResult { get; set; } =
        new(DataStatus.Ok, new SensorReading("CPU test", null, new Dictionary<string, double> { ["cpu.load"] = 12.5 }));

    public DataResult<NowPlayingInfo> NowPlayingResult { get; set; } = new(DataStatus.Unavailable, null);

    /// <summary>Une lecture lente (le service ne doit pas s'y empiler).</summary>
    public TimeSpan Delay { get; set; }

    public int SensorCalls => Volatile.Read(ref _sensorCalls);

    private int _sensorCalls;

    public DataResult<SensorReading> Sensors()
    {
        Interlocked.Increment(ref _sensorCalls);
        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        return SensorsResult;
    }

    public DataResult<NowPlayingInfo> NowPlaying() => NowPlayingResult;
}

internal static class Win32
{
    private const uint HandleFlagInherit = 1;
    private const uint DuplicateSameAccess = 2;

    public static void SetInheritable(SafeHandle handle, bool inheritable)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, inheritable ? HandleFlagInherit : 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Duplique une poignée de ce processus dans un autre (ou dans lui-même) et renvoie sa valeur là-bas.</summary>
    public static IntPtr DuplicateInto(SafeHandle source, SafeProcessHandle? target = null)
    {
        var self = GetCurrentProcess();
        IntPtr inner;
        bool ok;
        if (target is null)
        {
            ok = DuplicateHandle(self, source, self, out inner, 0, false, DuplicateSameAccess);
        }
        else
        {
            ok = DuplicateHandle(self, source, target, out inner, 0, false, DuplicateSameAccess);
        }

        if (!ok)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return inner;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle source, SafeProcessHandle targetProcess, out IntPtr target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}

/// <summary>Lance le vrai <c>Trofeo.ModuleHost.exe</c> sans enfermement (tubes hérités tels quels).</summary>
internal sealed class PlainHostProcessFactory : IHostProcessFactory
{
    public static string HostExecutable => Path.Combine(AppContext.BaseDirectory, "host", "Trofeo.ModuleHost.exe");

    public List<PlainHostProcess> Started { get; } = [];

    public List<HostStart> Starts { get; } = [];

    public IHostProcess Start(HostStart start)
    {
        // Le superviseur crée des tubes non héritables (c'est ce que veut l'enfermement) : on les rend héritables le temps du lancement.
        foreach (var handle in start.InheritedHandles)
        {
            Win32.SetInheritable(handle, true);
        }

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(start.Executable, start.Arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        }
        finally
        {
            foreach (var handle in start.InheritedHandles)
            {
                Win32.SetInheritable(handle, false);
            }
        }

        var started = new PlainHostProcess(process);
        lock (Started)
        {
            Started.Add(started);
            Starts.Add(start);
        }

        return started;
    }

    public PlainHostProcess Last
    {
        get
        {
            lock (Started)
            {
                return Started[^1];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (Started)
            {
                return Started.Count;
            }
        }
    }
}

internal sealed class PlainHostProcess : IHostProcess
{
    private readonly Process _process;
    private readonly Task<int> _exited;

    public PlainHostProcess(Process process)
    {
        _process = process;
        _exited = Task.Run(async () =>
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        });
    }

    public int Id => _process.Id;

    public Task<int> Exited => _exited;

    public string DataDirectory { get; } = Path.GetTempPath();

    public JobUsage? Usage { get; set; }

    public bool HasExited => _exited.IsCompleted;

    public long DuplicateInto(SafeHandle source) => Win32.DuplicateInto(source, _process.SafeHandle).ToInt64();

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // déjà fini
        }
    }

    public long? PrivateBytes { get; set; }

    /// <summary>Combien de fois la mémoire privée a été lue : seul le relevé de consommation la lit (la lecture du job sert aussi à l'arrêt de l'hôte).</summary>
    public int PrivateReads => Volatile.Read(ref _privateReads);

    private int _privateReads;

    public JobUsage? ReadUsage()
    {
        return Usage;
    }

    public long? ReadPrivateBytes()
    {
        Interlocked.Increment(ref _privateReads);
        return PrivateBytes;
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Un faux hôte : pas de processus, le test joue l'hôte lui-même par les deux tubes. Il lit ce que le service écrit
/// (<see cref="Received"/>) et peut écrire des messages ou des octets quelconques.
/// </summary>
internal sealed class FakeHostProcess : IHostProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AnonymousPipeClientStream _in;
    private readonly AnonymousPipeClientStream _out;
    private readonly List<HostMessage> _received = [];
    private readonly Dictionary<Guid, (IntPtr View, int Width, int Height)> _views = [];
    private readonly Task _reader;

    private readonly bool _readInput;
    private readonly Action<FakeHostProcess, HostMessage>? _onMessage;

    public FakeHostProcess(HostStart start, bool readInput = true, Action<FakeHostProcess, HostMessage>? onMessage = null)
    {
        Start = start;
        _readInput = readInput;
        _onMessage = onMessage;
        _in = new AnonymousPipeClientStream(PipeDirection.In, new SafePipeHandle(Win32.DuplicateInto(start.InheritedHandles[0]), true));
        _out = new AnonymousPipeClientStream(PipeDirection.Out, new SafePipeHandle(Win32.DuplicateInto(start.InheritedHandles[1]), true));
        HostSide = new MessageChannel(_in, _out);
        _reader = readInput ? Task.Run(ReadAsync) : Task.CompletedTask;
    }

    public HostStart Start { get; }

    public MessageChannel HostSide { get; }

    public JobUsage? Usage { get; set; }

    public string DataDirectory { get; } = Path.GetTempPath();

    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    public bool Killed { get; private set; }

    public IReadOnlyList<HostMessage> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public IEnumerable<T> Of<T>() where T : HostMessage => Received.OfType<T>();

    public long DuplicateInto(SafeHandle source) => Win32.DuplicateInto(source).ToInt64();

    public void Kill()
    {
        if (HasExited)
        {
            return; // comme un vrai processus : tuer un processus fini ne change rien
        }

        Killed = true;
        Exit(1);
    }

    public long? PrivateBytes { get; set; }

    /// <summary>Combien de fois la mémoire privée a été lue : seul le relevé de consommation la lit (la lecture du job sert aussi à l'arrêt de l'hôte).</summary>
    public int PrivateReads => Volatile.Read(ref _privateReads);

    private int _privateReads;

    public JobUsage? ReadUsage()
    {
        return Usage;
    }

    public long? ReadPrivateBytes()
    {
        Interlocked.Increment(ref _privateReads);
        return PrivateBytes;
    }

    /// <summary>L'hôte s'arrête : le tube vers le service se ferme, et le processus est fini.</summary>
    public void Exit(int code)
    {
        try
        {
            _out.Dispose();
            if (!_readInput)
            {
                _in.Dispose(); // personne n'y lit : le service qui écrit encore doit voir le tube cassé
            }
        }
        catch (IOException)
        {
        }

        _exited.TrySetResult(code);
    }

    public Task SendAsync(HostMessage message) => HostSide.SendAsync(message, CancellationToken.None);

    /// <summary>Écrit des octets quelconques dans le tube du service (un hôte qui ne parle pas le protocole).</summary>
    public void WriteRaw(byte[] bytes)
    {
        _out.Write(bytes);
        _out.Flush();
    }

    /// <summary>Peint l'image du tampon dans la section de l'instance (comme l'hôte) puis l'annonce. L'hôte doit avoir reçu <see cref="Open"/>.</summary>
    public async Task SendFrameAsync(Open open, int buffer, SKColor color, bool animating = false)
    {
        PaintBuffer(open, buffer, color);
        await SendAsync(new Frame(open.InstanceId, buffer, animating));
    }

    public void PaintBuffer(Open open, int buffer, SKColor color)
    {
        if (!_views.TryGetValue(open.InstanceId, out var view))
        {
            view = (FrameView.Map(open.Buffer0, open.Width, open.Height), open.Width, open.Height);
            _views[open.InstanceId] = view;
        }

        var bytes = view.Width * view.Height * 4;
        var premultiplied = color.Alpha == 255 ? color : new SKColor((byte)(color.Red * color.Alpha / 255), (byte)(color.Green * color.Alpha / 255), (byte)(color.Blue * color.Alpha / 255), color.Alpha);
        var pixels = new byte[bytes];
        for (var i = 0; i < bytes; i += 4)
        {
            pixels[i] = premultiplied.Blue;
            pixels[i + 1] = premultiplied.Green;
            pixels[i + 2] = premultiplied.Red;
            pixels[i + 3] = premultiplied.Alpha;
        }

        Marshal.Copy(pixels, 0, view.View + buffer * bytes, bytes);
    }

    public async Task<T> WaitAsync<T>(Func<T, bool>? filter = null, TimeSpan? timeout = null) where T : HostMessage
    {
        T? found = null;
        await TestWait.UntilAsync(() => (found = Of<T>().FirstOrDefault(message => filter?.Invoke(message) ?? true)) is not null, timeout,
            () => $"Aucun {typeof(T).Name} reçu. Reçus : " + string.Join(" | ", Received.Select(m => m.GetType().Name)));
        return found!;
    }

    public void Dispose()
    {
        // Les tubes sont fermés avec le test ; ici, rien : le service a déjà fermé son côté.
        try
        {
            _in.Dispose();
        }
        catch (IOException)
        {
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var message in HostSide.ReadAllAsync(CancellationToken.None))
            {
                lock (_received)
                {
                    _received.Add(message);
                }

                _onMessage?.Invoke(this, message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
        }
    }
}

internal sealed class FakeHostFactory : IHostProcessFactory
{
    private readonly List<FakeHostProcess> _started = [];

    /// <summary>Appelé pour chaque lancement, avant qu'il réussisse ; peut lever pour simuler un échec.</summary>
    public Action<HostStart>? OnStart { get; set; }

    /// <summary>Appelé sur le faux processus neuf (pour lui fixer par exemple une consommation).</summary>
    public Action<FakeHostProcess>? Configure { get; set; }

    /// <summary>Faux : l'hôte ne lit jamais ce que le service lui écrit.</summary>
    public bool ReadInput { get; set; } = true;

    /// <summary>Appelé pour chaque message que le faux hôte reçoit (sur son fil de lecture).</summary>
    public Action<FakeHostProcess, HostMessage>? OnMessage { get; set; }

    public IReadOnlyList<FakeHostProcess> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public int Count => Started.Count;

    public FakeHostProcess Last => Started[^1];

    public IHostProcess Start(HostStart start)
    {
        OnStart?.Invoke(start);
        var process = new FakeHostProcess(start, ReadInput, OnMessage);
        Configure?.Invoke(process);
        lock (_started)
        {
            _started.Add(process);
        }

        return process;
    }

    /// <summary>Attend le lancement numéro <paramref name="count"/> et renvoie ce faux hôte.</summary>
    public async Task<FakeHostProcess> WaitForStartAsync(int count = 1)
    {
        await TestWait.UntilAsync(() => Count >= count, null, () => $"Pas de lancement n°{count} (il y en a {Count}).");
        return Started[count - 1];
    }
}

internal static class TestWait
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, Func<string>? describe = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > (timeout ?? Patience))
            {
                throw new TimeoutException("Condition jamais remplie. " + describe?.Invoke());
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Vrai si la condition reste fausse pendant tout le délai (pour affirmer qu'une chose n'arrive pas).</summary>
    public static async Task<bool> StaysFalseAsync(Func<bool> condition, TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            if (condition())
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }
}

/// <summary>Des dossiers de module pour les tests et les entrées du catalogue qui vont avec.</summary>
internal sealed class ModuleSandbox : IDisposable
{
    public const string SampleEntry = "Trofeo.ModuleHost.Tests.SampleModule.dll";

    private readonly List<string> _folders = [];

    public static string SampleFolder => Path.Combine(AppContext.BaseDirectory, "sample");

    public void Dispose()
    {
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

    public string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "avmod-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        return folder;
    }

    /// <summary>Un petit dossier de module (quelques fichiers) : le superviseur ne le charge jamais pour de vrai quand l'hôte est faux.</summary>
    public string SmallModule()
    {
        var folder = NewFolder();
        File.WriteAllText(Path.Combine(folder, "module.dll"), "pas une vraie DLL");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "data.txt"), "des données");
        return folder;
    }

    /// <summary>Une copie du module d'essai de l'hôte (DLL, SkiaSharp, SDK…).</summary>
    public string SampleCopy()
    {
        var folder = NewFolder();
        foreach (var file in Directory.EnumerateFiles(SampleFolder, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, Path.GetRelativePath(SampleFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return folder;
    }

    public static ModuleManifest Manifest(
        string id = "test.sample", string entry = SampleEntry, IReadOnlyList<ModulePermission>? permissions = null, params SettingField[] fields) =>
        new(id, "Module d'essai", "Pour les tests.", "tests", "1.0.0", new SdkVersion(1, 0), entry, permissions ?? [], null, new GridSize(1, 1),
            new Dictionary<SizePreset, GridSize>(), new SettingsSchema(fields), null);

    /// <summary>Une entrée du catalogue : validée à l'empreinte actuelle du dossier (sauf si <paramref name="hash"/> est donné).</summary>
    public static CommunityEntry Ready(string folder, ModuleManifest manifest, IReadOnlyList<ModulePermission>? granted = null, string? hash = null) =>
        new(manifest.Id, folder, manifest, CommunityState.Ready, null,
            new ApprovedModule(manifest.Id, manifest.Version, folder, hash ?? ContentHash.Compute(folder), granted ?? manifest.Permissions, null, null), false);

    public static CommunityEntry DevFolder(string folder, ModuleManifest manifest) =>
        new(manifest.Id, folder, manifest, CommunityState.Ready, null, null, true);

    public static JsonElement Json(string json = "{}")
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Un superviseur avec tout ce qu'il faut autour : temps figé, données et coffre factices, mises de côté enregistrées.</summary>
internal sealed class SupervisorRig : IAsyncDisposable
{
    public SupervisorRig(
        CommunityEntry entry, IHostProcessFactory launcher, IReadOnlyList<ModulePermission>? granted = null, string? hostExecutable = null,
        ISecretStore? secrets = null)
    {
        Launcher = launcher;
        Secrets = secrets ?? new Trofeo.TestDoubles.FakeSecretStore();
        Supervisor = new ModuleHostSupervisor(
            entry, granted ?? entry.Approval?.Granted ?? [], hostExecutable ?? PlainHostProcessFactory.HostExecutable, Secrets, Data, Time,
            (id, reason) =>
            {
                lock (SetAsides)
                {
                    SetAsides.Add((id, reason));
                }
            },
            launcher, Log, running =>
            {
                lock (_hostRunning)
                {
                    _hostRunning.Add(running);
                }
            });
    }

    private readonly List<bool> _hostRunning = [];

    /// <summary>Ce que le superviseur a annoncé : vrai quand un hôte est installé, faux quand il n'y en a plus.</summary>
    public IReadOnlyList<bool> HostRunning
    {
        get
        {
            lock (_hostRunning)
            {
                return [.. _hostRunning];
            }
        }
    }

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public FakeDataBroker Data { get; } = new();

    public ISecretStore Secrets { get; }

    public Trofeo.TestDoubles.FakeModuleLog Log { get; } = new();

    public List<(string Id, string Reason)> SetAsides { get; } = [];

    public IHostProcessFactory Launcher { get; }

    public ModuleHostSupervisor Supervisor { get; }

    public List<RemoteModule> Modules { get; } = [];

    public RemoteModule Open(Guid? instance = null, string settings = "{}")
    {
        var module = Supervisor.Open(new ModuleContext(instance ?? Guid.NewGuid(), ModuleSandbox.Json(settings), Time, Log, Secrets));
        Modules.Add(module);
        return module;
    }

    /// <summary>Dessine la case sur un bitmap transparent (la case occupe un rectangle décalé de 10 px) et le rend.</summary>
    public SKBitmap Draw(RemoteModule module, int width = 96, int height = 48, int columns = 2, int rows = 1)
    {
        var bitmap = new SKBitmap(width + 20, height + 20);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        module.Render(canvas, new ModuleRenderContext(
            SKRect.Create(10, 10, width, height), new GridSize(columns, rows), Trofeo.Core.Rendering.Themes.ClassiqueDefinition.Theme, 1f, Time.GetUtcNow()));
        return bitmap;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var module in Modules)
        {
            module.Dispose();
        }

        await Supervisor.DisposeAsync();
    }
}

/// <summary>Un test qui lance un hôte de module (un exécutable Windows) ou sa mécanique : ignoré ailleurs que sous Windows.</summary>
public sealed class WindowsHostFactAttribute : FactAttribute
{
    public WindowsHostFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "L'hôte de module n'existe que sous Windows.";
        }
    }
}

/// <summary>Comme <see cref="WindowsHostFactAttribute"/>, pour un test à paramètres.</summary>
public sealed class WindowsHostTheoryAttribute : TheoryAttribute
{
    public WindowsHostTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "L'hôte de module n'existe que sous Windows.";
        }
    }
}

/// <summary>Un coffre à secrets lent, qui compte ses appels.</summary>
internal sealed class SlowSecretStore : ISecretStore
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public string? Get(string key)
    {
        Interlocked.Increment(ref _calls);
        Thread.Sleep(30);
        return null;
    }

    public void Set(string key, string? value)
    {
        Interlocked.Increment(ref _calls);
        Thread.Sleep(30);
    }
}
