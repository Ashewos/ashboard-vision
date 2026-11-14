namespace Trofeo.Core.Rendering;

/// <summary>
/// Décide quand envoyer une image : seulement si quelque chose a changé (ou s'anime)
/// et jamais plus souvent que le plafond d'images par seconde (de début de rendu à début de rendu).
/// </summary>
public sealed class FrameScheduler : IDisposable
{
    private readonly TimeProvider _time;
    private readonly AutoResetEvent _signal = new(false);
    private int _dirty;
    private bool _hasRendered;
    private long _lastRenderTimestamp;
    private long? _renderStart; // début du rendu en cours, noté par ShouldRender
    private long _minIntervalTicks;

    /// <param name="time">Source de temps (mockable en test).</param>
    /// <param name="maxFps">Plafond d'images par seconde ; doit être strictement positif.</param>
    public FrameScheduler(TimeProvider time, double maxFps = 12)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (!(maxFps > 0) || double.IsInfinity(maxFps))
        {
            throw new ArgumentOutOfRangeException(nameof(maxFps), maxFps, "Le plafond d'images par seconde doit être strictement positif.");
        }

        _time = time;
        _minIntervalTicks = IntervalTicks(maxFps);
    }

    /// <summary>Durée minimale entre deux envois (1000 / maxFps ms).</summary>
    public TimeSpan MinInterval => TimeSpan.FromTicks(Volatile.Read(ref _minIntervalTicks));

    /// <summary>Plus petit et plus grand plafonds acceptés par <see cref="SetMaxFps"/>, en images par seconde.</summary>
    public const double MinFpsLimit = 1;

    public const double MaxFpsLimit = 12;

    /// <summary>
    /// Change le plafond d'images par seconde, à chaud ; borné à [1, 12]. Une valeur qui n'est pas un nombre est ignorée.
    /// Peut être appelé depuis n'importe quel thread : l'intervalle suivant s'en sert.
    /// </summary>
    public void SetMaxFps(double maxFps)
    {
        if (double.IsNaN(maxFps)) return;
        Volatile.Write(ref _minIntervalTicks, IntervalTicks(Math.Clamp(maxFps, MinFpsLimit, MaxFpsLimit)));
    }

    private static long IntervalTicks(double maxFps) => (long)Math.Round(TimeSpan.TicksPerSecond / maxFps);

    /// <summary>
    /// Levé par <see cref="MarkDirty"/> et <see cref="Wake"/> : la boucle d'affichage dort dessus
    /// (avec un délai) au lieu de se réveiller à intervalle fixe. Se réarme seul à chaque réveil.
    /// </summary>
    public WaitHandle Signal => _signal;

    /// <summary>Vrai si un redessin est en attente (sans le consommer).</summary>
    public bool IsDirty => Volatile.Read(ref _dirty) != 0;

    /// <summary>Signale qu'un module doit être redessiné et réveille la boucle. Peut être appelé depuis n'importe quel thread.</summary>
    public void MarkDirty()
    {
        Volatile.Write(ref _dirty, 1);
        Wake();
    }

    /// <summary>Réveille la boucle sans demander de redessin. Peut être appelé depuis n'importe quel thread.</summary>
    public void Wake()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // Un module en fin de vie peut signaler après l'arrêt : sans conséquence.
        }
    }

    /// <summary>
    /// Délai avant que l'intervalle minimum entre deux images soit écoulé : 0 si aucune image n'a encore été
    /// rendue, sinon <c>max(0, MinInterval − écoulé)</c>. À appeler depuis le thread de la boucle.
    /// </summary>
    public TimeSpan UntilNextSlot()
    {
        if (!_hasRendered)
        {
            return TimeSpan.Zero;
        }

        var remaining = MinInterval - _time.GetElapsedTime(_lastRenderTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Vrai s'il faut rendre une image maintenant. Quand la réponse est vraie, la demande de
    /// redessin en attente est consommée : un <see cref="MarkDirty"/> arrivé pendant le rendu
    /// reste donc en attente pour l'image suivante. À appeler depuis le thread de la boucle.
    /// <paramref name="ignoreCap"/> : le plafond d'images par seconde ne retient pas cette image (le glissement d'une vue à
    /// l'autre va aussi vite que l'envoi le permet) ; l'image suivante, elle, compte son intervalle depuis celle-ci.
    /// </summary>
    public bool ShouldRender(bool anyAnimating, bool ignoreCap = false)
    {
        bool wanted = anyAnimating || IsDirty || ignoreCap;
        if (!wanted)
        {
            return false;
        }

        if (!ignoreCap && _hasRendered && _time.GetElapsedTime(_lastRenderTimestamp) < MinInterval)
        {
            return false;
        }

        Interlocked.Exchange(ref _dirty, 0);
        _renderStart = _time.GetTimestamp();
        return true;
    }

    /// <summary>
    /// À appeler une fois l'image envoyée. L'intervalle minimum se compte depuis le <b>début</b> du rendu que
    /// <see cref="ShouldRender"/> vient d'accepter, pas depuis cette fin : l'échéance de l'image suivante est absolue, la durée du
    /// rendu et de l'envoi ne s'y ajoute pas (sinon 50 ms de rendu feraient 133 ms entre deux images au lieu de 83). Un rendu plus
    /// long que l'intervalle laisse l'image suivante partir tout de suite. Sans <see cref="ShouldRender"/> avant, c'est maintenant.
    /// </summary>
    public void OnRendered()
    {
        _lastRenderTimestamp = _renderStart ?? _time.GetTimestamp();
        _renderStart = null;
        _hasRendered = true;
    }

    public void Dispose() => _signal.Dispose();
}
