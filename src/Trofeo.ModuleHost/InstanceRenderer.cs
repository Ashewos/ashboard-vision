using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost;

/// <summary>
/// Une instance du module (une case du damier) : la classe du module, ses deux tampons d'images dans la section partagée et la
/// boucle qui dessine. Un seul fil dessine pour l'instance, jamais deux images en même temps.
/// <para>
/// <b>Rythme.</b> Une image est dessinée quand le module l'a demandé (<see cref="CommunityModule"/>.Invalidate) ou tant
/// qu'il est animé, au plus <see cref="HostContext.MaxFps"/> fois par seconde, dans un tampon que le service a fini de
/// copier (<see cref="FrameDone"/>) : un tampon qui attend son accusé n'est jamais écrit. Sans demande ni animation, la boucle dort.
/// </para>
/// <para>
/// <b>Visibilité.</b> Une instance cachée (<see cref="SetVisible"/>) tourne comme les autres (démarrage, minuteries du module) mais
/// n'est jamais dessinée : pour la boucle, c'est « rien à faire ». Une demande de dessin faite pendant ce temps reste en attente et
/// part dès que l'instance redevient visible.
/// </para>
/// <para>
/// <b>Raccourcis.</b> Un raccourci (<see cref="DeliverShortcut"/>) est livré à <c>OnShortcutAsync</c> du module, un à la fois et dans l'ordre, sur un
/// fil du pool (jamais celui qui dessine), cachée ou non. La file est bornée à <see cref="MaxPendingShortcuts"/> : devant un module lent, les appuis
/// en trop sont jetés (et comptés au journal), jamais accumulés.
/// </para>
/// </summary>
internal sealed class InstanceRenderer : IAsyncDisposable
{
    private const string ModuleError = "Erreur du module";

    /// <summary>Le nombre de raccourcis qui peuvent attendre un module lent ; au-delà, les suivants sont jetés.</summary>
    internal const int MaxPendingShortcuts = 8;
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);
    private static readonly JsonElement EmptySettings = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly Guid _id;
    private readonly HostContext _host;
    private readonly HostLog _log;
    private readonly JsonElement _settings;
    private readonly int _width;
    private readonly int _height;
    private readonly GridSize _grid;
    private readonly float _textScale;
    private readonly IntPtr _view;
    private readonly SKSurface[] _surfaces = new SKSurface[2];
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new(); // _dirty, _visible, _busy, _lastBuffer, _lastFrame, _cooldownUntil
    private readonly object _statusGate = new(); // l'état et son envoi
    private readonly bool[] _busy = new bool[2];
    private readonly Channel<string> _shortcuts = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingShortcuts)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait, // TryWrite renvoie faux quand la file est pleine (DropWrite, lui, répondrait vrai)
    });
    private bool _dirty = true;
    private bool _visible = true;
    private int _lastBuffer = 1;
    private long _lastFrame = Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency;
    private long _cooldownUntil;
    private CommunityModule? _module;
    private Task? _start;
    private Task? _loop;
    private Task? _shortcutLoop;
    private int _droppedShortcuts;
    private int _closed;

    // L'état de la case : celui que le module a dit, sauf erreur (une exception de sa part l'emporte jusqu'à son prochain
    // changement d'état ou son prochain dessin réussi, sauf si l'erreur est définitive : constructeur ou démarrage).
    private ModuleHealth _moduleHealth = ModuleHealth.Online;
    private string? _moduleMessage;
    private bool _inError;
    private bool _fatalError;
    private (ModuleHealth Health, string? Message)? _lastPosted;

    /// <summary>La section partagée est déjà projetée en <paramref name="view"/> ; l'instance la défait à sa fermeture.</summary>
    public InstanceRenderer(HostContext host, Open open, IntPtr view)
    {
        _host = host;
        _id = open.InstanceId;
        _width = open.Width;
        _height = open.Height;
        _grid = new GridSize(open.Columns, open.Rows);
        _textScale = open.TextScale;
        _view = view;
        _settings = open.Settings.ValueKind == JsonValueKind.Object ? open.Settings : EmptySettings;
        _log = new HostLog(host.Link, $"[{_id.ToString("N")[..8]}] ");

        try
        {
            var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bufferBytes = FrameView.BufferBytes(_width, _height);
            for (var i = 0; i < 2; i++)
            {
                _surfaces[i] = SKSurface.Create(info, view + (i * bufferBytes), _width * 4)
                    ?? throw new InvalidOperationException("Impossible de dessiner dans les images partagées.");
            }
        }
        catch
        {
            foreach (var surface in _surfaces)
            {
                surface?.Dispose();
            }

            throw;
        }
    }

    public Guid Id => _id;

    /// <summary>Crée le module, le démarre, puis lance la boucle de dessin, le tout sur un fil du pool (jamais sur la boucle de lecture). Une panne du module met l'instance en erreur.</summary>
    public void Start() => _start = Task.Run(RunStartAsync);

    /// <summary>Le service a fini de copier ce tampon : on peut de nouveau y écrire.</summary>
    public void FrameDone(int buffer)
    {
        if (buffer is not (0 or 1))
        {
            return;
        }

        lock (_gate)
        {
            _busy[buffer] = false;
        }

        Wake();
    }

    /// <summary>La case est cachée ou redevient visible (une instance est visible à son ouverture). Cachée, elle n'est plus dessinée.</summary>
    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            _visible = visible;
        }

        Wake();
    }

    /// <summary>
    /// Un raccourci à livrer au module. Rend la main aussitôt : le module l'appelle à son rythme, dans l'ordre. File pleine (module lent) : le
    /// raccourci est jeté. Avant la fin de <c>StartAsync</c>, il attend dans la file ; si le démarrage échoue, il n'est jamais livré.
    /// </summary>
    public void DeliverShortcut(string shortcutId)
    {
        if (!_shortcuts.Writer.TryWrite(shortcutId))
        {
            Interlocked.Increment(ref _droppedShortcuts);
        }
    }

    /// <summary>Redessine (le thème a changé).</summary>
    public void Redraw() => Invalidate();

    /// <summary>Le plafond d'images par seconde a changé.</summary>
    public void LimitChanged() => Wake();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _closing.Cancel();
        _shortcuts.Writer.TryComplete();
        Wake();

        var ended = true;
        try
        {
            if (_start is not null)
            {
                await _start.WaitAsync(CloseWait).ConfigureAwait(false);
            }

            if (_loop is not null)
            {
                await _loop.WaitAsync(CloseWait).ConfigureAwait(false);
            }

            if (_shortcutLoop is not null)
            {
                await _shortcutLoop.WaitAsync(CloseWait).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            ended = false; // un module qui ne rend pas la main : on ne défait pas la mémoire qu'il pourrait encore toucher
        }

        if (_module is { } module)
        {
            module.CloseSignals();
            try
            {
                await module.DisposeAsync().AsTask().WaitAsync(CloseWait).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Error($"Exception à la fermeture du module : {ex.Message}");
            }
            catch (OutOfMemoryException ex)
            {
                ModuleGuard.CrashIfFatal(ex);
            }
        }

        if (ended)
        {
            foreach (var surface in _surfaces)
            {
                surface.Dispose();
            }

            FrameView.Unmap(_view);
            _closing.Dispose();
        }
    }

    private async Task RunStartAsync()
    {
        try
        {
            var backend = new HostBackend(_host.Link, _id, Invalidate, SetModuleStatus);
            _module = _host.CreateModule(_settings, _log, backend);
        }
        catch (Exception ex)
        {
            if (!ModuleGuard.CrashIfFatal(ex))
            {
                Fail("la création du module", ex, fatal: true);
            }

            return;
        }

        try
        {
            await _module.StartAsync(_closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!ModuleGuard.CrashIfFatal(ex))
            {
                Fail("StartAsync", ex, fatal: true);
            }

            return;
        }

        if (_closing.IsCancellationRequested)
        {
            return;
        }

        _loop = Task.Run(() => LoopAsync(_closing.Token));
        _shortcutLoop = Task.Run(() => ShortcutLoopAsync(_closing.Token));
        Wake();
    }

    /// <summary>Livre les raccourcis au module, un à la fois. Une panne du module est journalisée : elle ne met pas la case en erreur et n'arrête rien.</summary>
    private async Task ShortcutLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var shortcutId in _shortcuts.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _module!.OnShortcutAsync(shortcutId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!ModuleGuard.CrashIfFatal(ex))
                    {
                        _log.Error($"Exception dans OnShortcutAsync : {ex}");
                    }
                }

                var dropped = Interlocked.Exchange(ref _droppedShortcuts, 0);
                if (dropped > 0)
                {
                    _log.Host("warn", $"{dropped} raccourci(s) ignoré(s) : le module répond trop lentement (trop de raccourcis en attente, {MaxPendingShortcuts} au plus).");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // fermeture de l'instance
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    var animating = ReadAnimating();
                    int buffer;
                    long due;
                    lock (_gate)
                    {
                        buffer = FreeBuffer();
                        if (!_visible || buffer < 0 || !(_dirty || animating))
                        {
                            break; // rien à faire (cachée comprise) : un événement (demande, accusé, nouveau plafond, retour de la case) réveillera la boucle
                        }

                        due = Math.Max(_lastFrame + (Stopwatch.Frequency / Math.Max(1, _host.MaxFps)), _cooldownUntil);
                    }

                    var wait = due - Stopwatch.GetTimestamp();
                    if (wait > 0)
                    {
                        // Attente interrompue par tout événement (nouveau plafond, accusé, demande) : l'état est alors relu.
                        await _wake.WaitAsync(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), ct).ConfigureAwait(false);
                        continue; // l'état a pu changer pendant l'attente : on le relit
                    }

                    lock (_gate)
                    {
                        if (!_visible)
                        {
                            continue; // cachée pendant l'attente : on relit l'état, la demande reste en attente
                        }

                        _dirty = false; // une demande faite pendant le dessin en réclame un autre
                        _busy[buffer] = true;
                        _lastBuffer = buffer;
                        _lastFrame = Stopwatch.GetTimestamp();
                    }

                    Draw(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // fermeture de l'instance
        }
    }

    /// <summary>Le tampon où dessiner : l'autre que le dernier s'il est libre, sinon le dernier, sinon aucun (-1).</summary>
    private int FreeBuffer()
    {
        var other = 1 - _lastBuffer;
        return !_busy[other] ? other : !_busy[_lastBuffer] ? _lastBuffer : -1;
    }

    private void Draw(int buffer)
    {
        var drawn = false;
        try
        {
            var canvas = _surfaces[buffer].Canvas;
            var saved = canvas.Save();
            try
            {
                canvas.Clear(SKColors.Transparent);
                _module!.Render(canvas, new ModuleRenderContext(new SKRect(0, 0, _width, _height), _grid, _host.Theme, _textScale, DateTimeOffset.Now));
            }
            finally
            {
                canvas.RestoreToCount(saved);
            }

            canvas.Flush();
            drawn = true;
        }
        catch (Exception ex)
        {
            if (!ModuleGuard.CrashIfFatal(ex))
            {
                Fail("Render", ex, fatal: false);
            }
        }
        finally
        {
            if (!drawn)
            {
                lock (_gate)
                {
                    _busy[buffer] = false;
                    _dirty = true; // la demande n'a pas été satisfaite : un nouvel essai suivra, après le délai, sans que le module ait à la renouveler
                    _cooldownUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency; // pas plus d'un essai par seconde après une panne
                }
            }
        }

        if (!drawn || _closing.IsCancellationRequested)
        {
            return;
        }

        Recover();
        _host.Link.Post(new Frame(_id, buffer, ReadAnimating()));
    }

    private bool ReadAnimating()
    {
        try
        {
            return _module!.IsAnimating;
        }
        catch (Exception ex)
        {
            if (!ModuleGuard.CrashIfFatal(ex))
            {
                Fail("IsAnimating", ex, fatal: false);
            }

            return false;
        }
    }

    // --- Demandes du module ---------------------------------------------------------------------------------------------------

    private void Invalidate()
    {
        lock (_gate)
        {
            _dirty = true;
        }

        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // un réveil est déjà en attente
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // --- État -----------------------------------------------------------------------------------------------------------------

    /// <summary>Le module change d'état : l'erreur éventuelle est effacée, sauf si elle est définitive.</summary>
    private void SetModuleStatus(ModuleHealth health, string? message)
    {
        lock (_statusGate)
        {
            _moduleHealth = health;
            _moduleMessage = message;
            if (_fatalError)
            {
                return;
            }

            _inError = false;
            PostStatusLocked(health, message);
        }
    }

    private void Fail(string where, Exception exception, bool fatal)
    {
        _log.Error($"Exception dans {where} : {exception}");
        lock (_statusGate)
        {
            _inError = true;
            _fatalError |= fatal;
            PostStatusLocked(ModuleHealth.Offline, ModuleError);
        }
    }

    /// <summary>Un dessin a réussi : l'état redevient celui du module.</summary>
    private void Recover()
    {
        lock (_statusGate)
        {
            if (_inError && !_fatalError)
            {
                _inError = false;
                PostStatusLocked(_moduleHealth, _moduleMessage);
            }
        }
    }

    private void PostStatusLocked(ModuleHealth health, string? message)
    {
        var text = message is null ? null : HostLink.Clip(message);
        if (_lastPosted is { } last && last.Health == health && last.Message == text)
        {
            return;
        }

        _lastPosted = (health, text);
        _host.Link.Post(new Status(_id, health, text));
    }
}
