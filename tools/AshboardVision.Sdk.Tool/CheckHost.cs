using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Extensions;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Tool;

/// <summary>Le journal du module pendant l'essai : tout ce que l'hôte et le superviseur écrivent, gardé pour le montrer si l'essai échoue.</summary>
internal sealed class CheckLog : IModuleLog
{
    private const int MaxLines = 200;
    private readonly ConcurrentQueue<string> _lines = new();
    private long _total;

    public IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>Le nombre de lignes écrites depuis le début, y compris celles que <see cref="MaxLines"/> a fait oublier.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>Les lignes écrites après que <see cref="Total"/> valait <paramref name="total"/> (celles qui sont encore gardées).</summary>
    public IReadOnlyList<string> Since(long total) => [.. Lines.TakeLast((int)Math.Clamp(Total - total, 0, MaxLines))];

    public void Info(string message) => Add(message);

    public void Warning(string message) => Add(message);

    public void Error(string message) => Add(message);

    private void Add(string message)
    {
        _lines.Enqueue(message);
        Interlocked.Increment(ref _total);
        while (_lines.Count > MaxLines && _lines.TryDequeue(out _))
        {
        }
    }
}

/// <summary>De fausses données de capteurs et de musique, toujours les mêmes : l'essai ne lit pas le matériel de ce PC.</summary>
internal sealed class SampleDataBroker : IServiceDataBroker
{
    public DataResult<SensorReading> Sensors() => new(DataStatus.Ok, SampleSensors.Reading());

    public DataResult<NowPlayingInfo> NowPlaying() => new(DataStatus.Ok, new NowPlayingInfo(
        "Titre d'essai", "Artiste d'essai", "Lecteur d'essai", TimeSpan.FromSeconds(72), TimeSpan.FromSeconds(215), true, null));
}

/// <summary>
/// Lance les hôtes par la fabrique de production, en notant ce qui compte pour l'essai : si le processus s'est
/// arrêté sans qu'on l'ait demandé (et avec quel code).
/// </summary>
internal sealed class HostProcessRecorder(IHostProcessFactory inner) : IHostProcessFactory
{
    private readonly object _gate = new();
    private bool _stopRequested;
    private int? _unexpectedExit;
    private readonly TaskCompletionSource<int> _stopExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _ends = [];

    /// <summary>Le code de sortie d'un hôte qui s'est arrêté tout seul, avant qu'on le demande ; <c>null</c> si aucun ne l'a fait.</summary>
    public int? UnexpectedExitCode
    {
        get
        {
            lock (_gate)
            {
                return _unexpectedExit;
            }
        }
    }

    /// <summary>À appeler juste avant de fermer la dernière case : les arrêts qui suivent sont voulus. Rend la tâche qui se termine avec le code de sortie.</summary>
    public Task<int> ExpectStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            return _stopExit.Task;
        }
    }

    public IHostProcess Start(HostStart start)
    {
        var process = inner.Start(start);
        var ended = process.Exited.ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully)
            {
                return;
            }

            lock (_gate)
            {
                if (_stopRequested)
                {
                    _stopExit.TrySetResult(task.Result);
                }
                else
                {
                    _unexpectedExit ??= task.Result;
                }
            }
        }, TaskScheduler.Default);
        lock (_gate)
        {
            _ends.Add(ended);
        }

        return new Recorded(process);
    }

    /// <summary>
    /// Se termine quand tous les hôtes lancés jusqu'ici sont finis. L'essai attend cela avant de supprimer le conteneur d'essai : un hôte qui
    /// finit encore ne doit pas y tenir des fichiers.
    /// </summary>
    public Task AllEnded()
    {
        lock (_gate)
        {
            return Task.WhenAll(_ends);
        }
    }

    private sealed class Recorded(IHostProcess inner) : IHostProcess
    {
        public Task<int> Exited => inner.Exited;

        public string DataDirectory => inner.DataDirectory;

        public long DuplicateInto(SafeHandle source) => inner.DuplicateInto(source);

        public void Kill() => inner.Kill();

        public JobUsage? ReadUsage() => inner.ReadUsage();

        public long? ReadPrivateBytes() => inner.ReadPrivateBytes();

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Ce qu'une attente d'image a donné.</summary>
internal enum FrameOutcomeKind
{
    /// <summary>Une image neuve est arrivée.</summary>
    Frame,

    /// <summary>Rien dans le délai.</summary>
    Timeout,

    /// <summary>L'hôte s'est arrêté tout seul.</summary>
    HostStopped,

    /// <summary>Le module dit qu'il est en erreur.</summary>
    ModuleError,

    /// <summary>L'essai a été interrompu (Ctrl+C).</summary>
    Cancelled,
}

internal readonly record struct FrameOutcome(FrameOutcomeKind Kind, string? Message = null, int? ExitCode = null, TimeSpan Elapsed = default)
{
    public bool IsFrame => Kind == FrameOutcomeKind.Frame;
}

/// <summary>Ce que l'arrêt de l'hôte a donné.</summary>
internal readonly record struct StopOutcome(bool Clean, TimeSpan Elapsed, string? Problem);

/// <summary>
/// Une case ouverte pendant l'essai : un <see cref="RemoteModule"/>, la même case que le logiciel pose sur l'écran, à laquelle on demande
/// des images et que l'on attend. Un seul fil l'utilise.
/// </summary>
internal sealed class CheckCase : IDisposable
{
    private readonly CheckSession _session;
    private readonly object _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CheckCase(CheckSession session, RemoteModule module, Guid instanceId)
    {
        _session = session;
        Module = module;
        InstanceId = instanceId;
        module.Invalidated += OnChanged;
        module.StatusChanged += OnChanged;
    }

    public RemoteModule Module { get; }

    /// <summary>L'identifiant d'instance de la case, celui que le superviseur attend pour lui transmettre un raccourci.</summary>
    public Guid InstanceId { get; }

    private void OnChanged(object? sender, EventArgs e)
    {
        TaskCompletionSource current;
        lock (_gate)
        {
            current = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        current.TrySetResult();
    }

    /// <summary>
    /// Dessine la case à cette taille et avec ce thème sur une image neuve (celle que le logiciel poserait sur l'écran). Le premier appel à
    /// une taille fait ouvrir l'instance dans l'hôte : l'image n'est celle du module qu'une fois <see cref="WaitForFrameAsync"/> revenu.
    /// </summary>
    public SKBitmap Draw(GridSize size, ThemeDefinition theme)
    {
        var rect = GridGeometry.ModuleRect(0, 0, size.Columns, size.Rows, theme.Grid);
        var bitmap = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            Module.Render(canvas, new ModuleRenderContext(new SKRect(0, 0, rect.Width, rect.Height), size, theme.Theme, 1f, DateTimeOffset.Now));
            canvas.Flush();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Attend qu'une image neuve soit arrivée (plus que <paramref name="after"/> images reçues), que l'hôte s'arrête, que le module se dise en erreur, ou le délai.</summary>
    public async Task<FrameOutcome> WaitForFrameAsync(long after, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }

            if (Module.FramesReceived > after)
            {
                return new FrameOutcome(FrameOutcomeKind.Frame, Elapsed: clock.Elapsed);
            }

            if (_session.Recorder.UnexpectedExitCode is { } code)
            {
                return new FrameOutcome(FrameOutcomeKind.HostStopped, ExitCode: code, Elapsed: clock.Elapsed);
            }

            var status = Module.Status;
            if (status.Health == ModuleHealth.Offline && status.ActionMessage is { Length: > 0 } message
                && message != RemoteModule.NotRespondingText && message != ModuleHostSupervisor.HostStoppedText)
            {
                return new FrameOutcome(FrameOutcomeKind.ModuleError, message, Elapsed: clock.Elapsed);
            }

            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new FrameOutcome(FrameOutcomeKind.Timeout, Elapsed: clock.Elapsed);
            }

            try
            {
                // Un réveil toutes les 50 ms au plus : l'arrêt de l'hôte ne passe par aucun événement de la case.
                var nap = remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50);
                await Task.WhenAny(changed, Task.Delay(nap, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return new FrameOutcome(FrameOutcomeKind.Cancelled, Elapsed: clock.Elapsed);
            }
        }
    }

    public void Dispose()
    {
        Module.Invalidated -= OnChanged;
        Module.StatusChanged -= OnChanged;
        Module.Dispose();
    }
}

/// <summary>
/// Un hôte de module lancé comme le logiciel le fait (le même superviseur, le même AppContainer, le même job object), mais dans le profil
/// d'essai <c>ashboard-vision.check.&lt;id&gt;</c>, avec des secrets en mémoire et de fausses données. Le module est traité comme un dossier de
/// développement : ses fichiers ne sont pas verrouillés (le créateur reconstruit pendant qu'il essaie).
/// </summary>
internal sealed class CheckSession : IAsyncDisposable
{
    private readonly ModuleHostSupervisor _supervisor;
    private readonly InMemorySecrets _secrets = new();
    private readonly List<CheckCase> _cases = [];

    public CheckSession(string folder, ModuleManifest manifest, IReadOnlyList<ModulePermission> granted, string hostExecutable, CheckLog log)
    {
        Log = log;
        var entry = new CommunityEntry(manifest.Id, folder, manifest, CommunityState.Ready, null, null, FromDevFolder: true);
        // Le dossier de l'hôte n'est pas lisible par un AppContainer sous le profil de l'utilisateur (là où « dotnet tool » range les outils) : on le lui accorde.
        var sandbox = new SandboxedHostProcessFactory(AppContainerProfile.EnsureForCheck, [Path.GetDirectoryName(hostExecutable)!]);
        Recorder = new HostProcessRecorder(sandbox);
        _supervisor = new ModuleHostSupervisor(
            entry, granted, hostExecutable, _secrets, new SampleDataBroker(), TimeProvider.System, (_, _) => { }, Recorder, log);
    }

    public CheckLog Log { get; }

    public HostProcessRecorder Recorder { get; }

    /// <summary>La consommation de l'hôte en cours (temps processeur du job et mémoire privée) ; <c>null</c> s'il n'y a pas d'hôte.</summary>
    public (JobUsage Usage, long PrivateBytes)? Measure() => _supervisor.Measure();

    /// <summary>Change l'apparence de toutes les cases ; l'hôte redessine chacune.</summary>
    public void SetAppearance(string theme, float? panelOpacity = null) =>
        _supervisor.SetAppearance(HostPayloads.AppearanceToJson(new AppearanceSettings(theme, panelOpacity)));

    /// <summary>Ouvre une case. Le premier appel fait lancer l'hôte.</summary>
    public CheckCase OpenCase(JsonElement settings)
    {
        var instanceId = Guid.NewGuid();
        var module = _supervisor.Open(new ModuleContext(instanceId, settings, TimeProvider.System, Log, _secrets));
        var checkCase = new CheckCase(this, module, instanceId);
        _cases.Add(checkCase);
        return checkCase;
    }

    /// <summary>Transmet un raccourci à cette case, comme le service le fait pour une case de la vue affichée. Faux si le superviseur ne l'a pas transmis.</summary>
    public bool SendShortcut(string shortcutId, CheckCase checkCase) => _supervisor.SendShortcut(shortcutId, [checkCase.InstanceId]);

    /// <summary>Ferme une case ; la dernière fermée fait arrêter l'hôte.</summary>
    public void CloseCase(CheckCase checkCase)
    {
        _cases.Remove(checkCase);
        checkCase.Dispose();
    }

    /// <summary>
    /// Ferme toutes les cases : le superviseur demande à l'hôte de finir et le tue au bout de 5 secondes. L'arrêt est propre si l'hôte s'est
    /// arrêté avant ce délai sans code de panne.
    /// </summary>
    public async Task<StopOutcome> StopAsync(CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(5);
        if (Recorder.UnexpectedExitCode is { } gone)
        {
            return new StopOutcome(false, TimeSpan.Zero, $"l'hôte s'était déjà arrêté (code {gone:X8})");
        }

        var exit = Recorder.ExpectStop();
        var clock = Stopwatch.StartNew();
        foreach (var checkCase in _cases.ToArray())
        {
            CloseCase(checkCase);
        }

        try
        {
            // 2 secondes de plus que le délai : le superviseur tue l'hôte à 5 s (code de sortie 1), et on veut le voir tué plutôt que perdu.
            var code = await exit.WaitAsync(limit + TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (clock.Elapsed > limit)
            {
                return new StopOutcome(false, clock.Elapsed, $"l'hôte n'a pas fini en {limit.TotalSeconds:0} s, il a été arrêté de force");
            }

            // Le code 1 est celui d'un arrêt forcé : le superviseur tue l'hôte dès que son tube se ferme, parfois un instant avant que le processus
            // n'ait fini de sortir (code 0). Un arrêt forcé après le délai a déjà été refusé plus haut ; les codes de l'hôte pour une panne sont 2 et 3.
            return code is 0 or 1
                ? new StopOutcome(true, clock.Elapsed, null)
                : new StopOutcome(false, clock.Elapsed, $"l'hôte s'est arrêté avec le code {code:X8}");
        }
        catch (TimeoutException)
        {
            return new StopOutcome(false, clock.Elapsed, $"l'hôte n'a pas fini en {limit.TotalSeconds:0} s");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var checkCase in _cases.ToArray())
        {
            CloseCase(checkCase);
        }

        await _supervisor.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Recorder.AllEnded().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Un hôte qui ne finit pas en 8 s a déjà été tué par le superviseur : la suppression du conteneur dira si quelque chose le retient encore.
        }
    }
}
