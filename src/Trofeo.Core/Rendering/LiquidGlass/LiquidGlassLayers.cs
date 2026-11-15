using SkiaSharp;

namespace Trofeo.Core.Rendering;

/// <summary>
/// Les couches du thème « Liquid Glass avancé », gardées en mémoire par case : l'ombre (sur le rectangle élargi de sa marge) et
/// le verre (sur le rectangle de la case), avec la luminance mesurée pour la garde de lisibilité.
/// <list type="bullet">
/// <item>Le verre d'une case est recalculé quand changent l'instance du fond ou sa <see cref="IBackground.Version"/>, le
/// rectangle, les réglages du verre ou la couleur du panneau. L'opacité de la teinte n'en fait pas partie : la teinte se pose
/// par-dessus, à chaque image.</item>
/// <item>L'ombre ne dépend que du rectangle et de ses quatre réglages : un fond animé ne la recalcule pas.</item>
/// <item>La copie floutée du fond se fait à pleine taille (voir <see cref="Blur"/>) et ne vit que le temps des calculs.</item>
/// <item>Les pastilles des éléments choisis (<see cref="PaintPill"/>) déforment la case telle qu'elle vient d'être peinte, et
/// sont gardées tant que ses pixels ne changent pas.</item>
/// </list>
/// Une image se compose entre <see cref="BeginFrame"/> et <see cref="EndFrame"/>, sur un seul fil (celui de l'affichage).
/// Avec <c>inBackground</c>, les couches d'un fond fixe se calculent sur un fil à part : en attendant, la case garde sa couche
/// précédente, ou n'en a pas (<see cref="IsReady"/> rend faux, l'appelant dessine alors le verre simple), et
/// <see cref="Ready"/> prévient quand une couche est arrivée. Un fond animé calcule toujours sur place : chaque image du fond
/// est déjà nouvelle. L'effet n'est jamais utilisé par deux fils à la fois.
/// </summary>
internal sealed class LiquidGlassLayers : IDisposable
{
    /// <summary>La copie réduite sur laquelle la luminance est mesurée : le quart de la case, comme <see cref="Backdrop"/>.</summary>
    private const int LuminanceDivisor = 4;

    private static readonly SKSamplingOptions Shrink = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private sealed record GlassKey(IBackground Background, int Version, SKRect Rect, GlassSettings Settings, SKColor Panel);

    private readonly record struct ShadowKey(SKRect Rect, float CornerRadius, float Opacity, float Spread, float OffsetY);

    private sealed record GlassLayer(GlassKey Key, SKImage Image, float Luminance);

    /// <summary><paramref name="Image"/> est <c>null</c> quand l'ombre est invisible (opacité nulle).</summary>
    private sealed record ShadowLayer(ShadowKey Key, SKImage? Image, SKRect Area);

    /// <summary>Une pastille gardée : sa forme (calée sur les pixels, coupée à la case), ses réglages et son image.</summary>
    private sealed class Pill(SKRect shape, GlassSettings settings, SKImage image)
    {
        public SKRect Shape => shape;
        public GlassSettings Settings => settings;
        public SKImage Image => image;
        public bool Used = true;
    }

    /// <summary>Ce qu'une case garde : ses deux couches, le calcul en route sur le fil à part, et ses pastilles.</summary>
    private sealed class Slot
    {
        public GlassLayer? Glass;
        public ShadowLayer? Shadow;
        public Job? Job;
        public bool Used;

        /// <summary>
        /// Les pixels de la case telle qu'elle était peinte (fond, ombres, verre, teinte) quand ses pastilles ont été calculées,
        /// sur <see cref="SceneArea"/>, lignes serrées. <c>null</c> tant que la case n'a pas de pastille.
        /// </summary>
        public byte[]? Scene;
        public SKRectI SceneArea;

        /// <summary>Vrai quand <see cref="Scene"/> a déjà été comparée à l'image en cours : elle vaut pour toutes ses pastilles.</summary>
        public bool SceneChecked;
        public List<Pill>? Pills;
    }

    /// <summary>Un calcul confié au fil à part. Une clé absente : cette couche-là n'est pas à refaire.</summary>
    private sealed class Job(SKRect rect, GlassKey? glass, ShadowKey? shadow, GlassSettings settings, Scene scene, SKImage sharp)
    {
        public SKRect Rect => rect;
        public GlassKey? GlassKey => glass;
        public ShadowKey? ShadowKey => shadow;
        public GlassSettings Settings => settings;
        public Scene Scene => scene;
        public SKImage Sharp => sharp;

        /// <summary>La case ne veut plus de ce calcul (autre réglage, autre fond, case retirée) : s'il n'a pas commencé, il est sauté.</summary>
        public volatile bool Cancelled;

        /// <summary>
        /// Annulé parce que la même case a demandé autre chose sur le fil à part (un curseur qu'on bouge). S'il était déjà en
        /// cours, son verre reste plus récent que celui que la case montre : il sert en attendant le suivant.
        /// </summary>
        public volatile bool Superseded;
    }

    private sealed record Result(Job Job, GlassLayer? Glass, ShadowLayer? Shadow, Exception? Error);

    /// <summary>
    /// Le fond d'une version donnée. Sur place, l'image du fond est empruntée le temps de l'image. Pour le fil à part, elle est
    /// copiée (le fond peut être libéré pendant le calcul) ; la copie et sa version floutée sont libérées dès que plus aucun
    /// calcul ne s'en sert.
    /// </summary>
    private sealed class Scene(IBackground background, int version, bool sync, Action<int> track)
    {
        private readonly object _lock = new();
        private SKImage? _owned;
        private SKImage? _blurred;
        private float _sigma;
        private int _users;

        public IBackground Background => background;
        public int Version => version;

        /// <summary>Vrai : les couches de ce fond se calculent sur place, jamais sur le fil à part.</summary>
        public bool Sync => sync;

        /// <summary>Fil de l'affichage : prend la scène pour un calcul sur le fil à part, et rend la copie du fond qu'il lira.</summary>
        public SKImage Acquire(SKImage current)
        {
            lock (_lock)
            {
                _users++;
                if (_owned is null)
                {
                    _owned = Copy(current);
                    track(1);
                }
                return _owned;
            }
        }

        /// <summary>Fil à part : rend la scène. Au dernier calcul, la copie du fond et son flou sont libérés.</summary>
        public void Release()
        {
            lock (_lock)
            {
                if (--_users > 0) return;

                if (_owned is not null)
                {
                    _owned.Dispose();
                    _owned = null;
                    track(-1);
                }
                DropBlurLocked();
            }
        }

        /// <summary>
        /// La version floutée de <paramref name="sharp"/> pour ce σ, gardée jusqu'au prochain σ ; <paramref name="sharp"/>
        /// lui-même sans flou. Appelé par celui qui calcule (un seul à la fois).
        /// </summary>
        public SKImage Blurred(SKImage sharp, float sigma)
        {
            if (sigma <= 0f) return sharp;

            lock (_lock)
            {
                if (_blurred is not null && _sigma == sigma) return _blurred;

                DropBlurLocked();
                _blurred = Blur(sharp, sigma);
                track(1);
                _sigma = sigma;
                return _blurred;
            }
        }

        /// <summary>Libère la copie floutée : elle sera refaite au prochain calcul.</summary>
        public void DropBlur()
        {
            lock (_lock) DropBlurLocked();
        }

        private void DropBlurLocked()
        {
            if (_blurred is null) return;

            _blurred.Dispose();
            _blurred = null;
            track(-1);
        }

        private static SKImage Copy(SKImage image)
        {
            var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
            try
            {
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
                {
                    canvas.DrawImage(image, 0, 0, paint);
                }

                return BackgroundSupport.Freeze(bitmap); // les pixels passent à l'image
            }
            finally
            {
                bitmap.Dispose();
            }
        }
    }

    private readonly bool _inBackground;
    private readonly LiquidGlassEffect _effect = new();
    private readonly object _effectGate = new(); // l'effet et le flou : un seul fil à la fois

    // L'effet des pastilles n'appartient qu'au fil de l'affichage : une pastille n'attend jamais le fil à part.
    private LiquidGlassEffect? _pillEffect;
    private readonly Dictionary<SKRect, Slot> _slots = [];

    // Le fil à part : sa file, ses résultats, et s'il tourne.
    private readonly object _queueGate = new();
    private readonly Queue<Job> _queue = new();
    private readonly List<Result> _results = [];
    private bool _draining;
    private volatile bool _disposed;

    private Scene? _scene;
    private IBackground? _background;
    private GlassSettings _settings = new();
    private SKColor _panel;
    private int _computations;
    private int _shadowComputations;
    private int _pillComputations;
    private int _liveImages;
    private int _workerFailures;

    /// <param name="inBackground">Vrai : les couches d'un fond fixe se calculent sur un fil à part.</param>
    /// <param name="report">Reçoit, sur le fil de l'affichage, l'échec d'un calcul fait sur le fil à part.</param>
    public LiquidGlassLayers(bool inBackground = false, Action<string>? report = null)
    {
        _inBackground = inBackground;
        _report = report;
    }

    private readonly Action<string>? _report;

    /// <summary>Une couche calculée sur le fil à part est prête. Levé depuis ce fil.</summary>
    public event Action? Ready;

    /// <summary>Combien de couches de verre ont été calculées (tests).</summary>
    internal int Computations => Volatile.Read(ref _computations);

    /// <summary>Combien de couches d'ombre ont été calculées (tests).</summary>
    internal int ShadowComputations => Volatile.Read(ref _shadowComputations);

    /// <summary>Combien de pastilles ont été calculées (tests).</summary>
    internal int PillComputations => _pillComputations;

    /// <summary>
    /// Combien d'images sont en vie : couches de verre et d'ombre (posées ou en attente de l'être), pastilles, copies du fond
    /// et copies floutées. Retombe à 0 après <see cref="Dispose"/>, une fois le fil à part arrêté (tests).
    /// </summary>
    internal int LiveImages => Volatile.Read(ref _liveImages);

    /// <summary>Combien de calculs ont échoué sur le fil à part (tests).</summary>
    internal int WorkerFailures => Volatile.Read(ref _workerFailures);

    /// <summary>Vrai tant que le fil à part a un calcul en cours ou en attente (tests).</summary>
    internal bool IsWorking
    {
        get
        {
            lock (_queueGate) return _draining || _queue.Count > 0;
        }
    }

    /// <summary>Combien de cases gardent une couche en mémoire (tests).</summary>
    internal int LiveLayers => _slots.Values.Count(s => s.Glass is not null || s.Shadow is not null);

    /// <summary>
    /// Commence une image : prend le fond et les réglages du verre de <paramref name="theme"/>, et pose les couches que le fil
    /// à part a finies depuis la dernière image.
    /// </summary>
    public void BeginFrame(IBackground background, ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(theme);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _background = background;
        _settings = theme.Panel.Glass ?? GlassPresets.Find(GlassPresets.Clair)!;
        _panel = theme.Theme.Panel;

        if (_scene is null || !ReferenceEquals(_scene.Background, background) || _scene.Version != background.Version)
        {
            DropSyncBlur();
            _scene = new Scene(background, background.Version, sync: !_inBackground || background.IsAnimated, Track);
        }

        foreach (var slot in _slots.Values)
        {
            slot.Used = false;
            slot.SceneChecked = false;
            if (slot.Pills is { } pills)
            {
                foreach (var pill in pills) pill.Used = false;
            }
        }
        Collect();
    }

    /// <summary>
    /// Peint les ombres des cases <paramref name="rects"/>, avant tous les verres : l'ombre d'une case passe ainsi dans l'écart
    /// et sous le verre de sa voisine. Une case dont le verre n'est pas encore prêt n'a pas d'ombre ici.
    /// </summary>
    public void PaintShadows(SKCanvas canvas, IReadOnlyList<SKRect> rects)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(rects);

        foreach (var rect in rects)
        {
            if (Ensure(rect) is { Glass: not null, Shadow: { Image: { } image, Area: var area } })
            {
                canvas.DrawImage(image, area.Left, area.Top);
            }
        }
    }

    /// <summary>Peint la couche de verre de la case, sans la teinte. Rien si elle n'est pas prête.</summary>
    public void PaintGlass(SKCanvas canvas, SKRect rect)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (Ensure(rect)?.Glass is { } glass)
        {
            canvas.DrawImage(glass.Image, glass.Key.Rect.Left, glass.Key.Rect.Top);
        }
    }

    /// <summary>
    /// Peint la pastille de verre d'un élément choisi de la case <paramref name="caseRect"/> : le même programme que le verre de
    /// la case, en pilule (rayon : la moitié de sa hauteur ; biseau : celui du réglage, ramené à cette moitié au plus), sans
    /// ombre portée. À appeler après le panneau de la case et avant le dessin du module.
    /// <list type="bullet">
    /// <item>La pastille déforme la case telle qu'elle est peinte dans <paramref name="frame"/> au premier appel de l'image
    /// pour cette case (fond, ombres, verre ou verre simple d'attente, teinte) : ni le contenu du module, ni une autre
    /// pastille. Au-delà des bords de la case, elle lit le bord.</item>
    /// <item>Elle est coupée à la case : sa forme est l'intersection de <paramref name="pill"/> (calé sur les pixels) et de la
    /// case, et rien n'est peint hors du contour arrondi de la case. Moins de 4 px de large ou de haut, ou un rectangle qui
    /// n'est pas fait de nombres finis : ignorée.</item>
    /// <item>Elle est gardée tant que les pixels de la case, sa forme et les réglages du verre ne changent pas : les pixels
    /// sont comparés un à un à chaque image. Une pastille ne peut donc pas rester celle d'une autre teinte, d'un autre fond
    /// ou d'un autre verre. Elle est libérée à la première image qui ne la demande plus.</item>
    /// <item>Le calcul se fait sur place, avec un effet qui n'appartient qu'à ce fil : jamais d'attente du fil à part.</item>
    /// </list>
    /// </summary>
    /// <param name="canvas">La toile de l'image, sans transformation : ses coordonnées sont celles de <paramref name="frame"/>.</param>
    /// <param name="frame">Les pixels sur lesquels <paramref name="canvas"/> dessine (4 octets par pixel).</param>
    /// <param name="caseRect">Le rectangle de la case, à l'écran.</param>
    /// <param name="pill">Le rectangle de l'élément choisi, à l'écran.</param>
    public void PaintPill(SKCanvas canvas, SKPixmap frame, SKRect caseRect, SKRect pill)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.BytesPerPixel != 4) throw new ArgumentException("L'image doit avoir 4 octets par pixel.", nameof(frame));
        if (_scene is null) throw new InvalidOperationException("BeginFrame n'a pas été appelé.");

        if (!IsFinite(pill) || !IsFinite(caseRect)) return;

        var area = Snap(caseRect);
        var shape = SKRect.Intersect(Snap(pill), area);
        var sceneArea = SKRectI.Intersect(SKRectI.Round(area), SKRectI.Create(frame.Width, frame.Height));
        shape = SKRect.Intersect(shape, sceneArea);
        if (!(shape.Width >= MinPillSide) || !(shape.Height >= MinPillSide)) return;
        if (Ensure(caseRect) is not { } slot) return;

        if (!slot.SceneChecked)
        {
            if (slot.Scene is null || slot.SceneArea != sceneArea || !SameScene(frame, sceneArea, slot.Scene))
            {
                // La case n'est plus peinte comme avant : ses pastilles sont à refaire.
                var previous = slot.Scene;
                DropPills(slot);
                slot.Scene = CopyScene(frame, sceneArea, previous);
                slot.SceneArea = sceneArea;
            }
            slot.SceneChecked = true;
        }

        float half = shape.Height / 2f;
        var settings = _settings with { CornerRadius = half, ZRadius = Math.Min(_settings.ZRadius, half), ShadowOpacity = 0f };
        var pills = slot.Pills ??= [];
        var kept = pills.Find(p => p.Shape == shape && p.Settings == settings);
        if (kept is null)
        {
            kept = new Pill(shape, settings, ComputePill(slot.Scene!, slot.SceneArea, frame, shape, settings));
            pills.Add(kept);
        }
        kept.Used = true;

        int saved = canvas.Save();
        try
        {
            float radius = Math.Max(0f, Math.Min(_settings.CornerRadius, Math.Min(area.Width, area.Height) / 2f));
            using var clip = new SKRoundRect(area, radius);
            canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
            canvas.DrawImage(kept.Image, shape.Left, shape.Top);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>
    /// Vrai si la case a un verre à montrer : celui qu'elle demande, ou à défaut le précédent de ce rectangle. Une case sans
    /// surface n'a rien à montrer : elle est prête.
    /// </summary>
    public bool IsReady(SKRect rect)
    {
        return HasNoSurface(Snap(rect)) || Ensure(rect)!.Glass is not null;
    }

    /// <summary>
    /// Luminance relative (WCAG, 0 à 1) de la zone la plus claire du verre de la case, posé sur le fond : mesurée une fois par
    /// calcul de la couche, sur une copie réduite, et gardée avec elle. 0 tant que la case n'a pas de verre.
    /// </summary>
    public float Luminance(SKRect rect) => Ensure(rect)?.Glass?.Luminance ?? 0f;

    /// <summary>Finit l'image : libère les couches des rectangles qu'elle n'a pas utilisés.</summary>
    public void EndFrame()
    {
        List<SKRect>? unused = null;
        foreach (var (rect, slot) in _slots)
        {
            if (!slot.Used) (unused ??= []).Add(rect);
        }

        if (unused is not null)
        {
            foreach (var rect in unused)
            {
                Free(_slots[rect]);
                _slots.Remove(rect);
            }
        }

        // Les pastilles que cette image n'a pas demandées (l'élément choisi a changé) ; sans pastille, la copie de la case part aussi.
        foreach (var slot in _slots.Values)
        {
            if (slot.Pills is not { } pills) continue;

            for (int i = pills.Count - 1; i >= 0; i--)
            {
                if (pills[i].Used) continue;

                Drop(pills[i]);
                pills.RemoveAt(i);
            }
            if (pills.Count == 0) DropPills(slot);
        }

        // Sur un fond fixe, toutes les couches de l'image sont faites : la copie floutée ne sert plus.
        if (_background is { IsAnimated: false }) DropSyncBlur();
    }

    /// <summary>
    /// Libère la copie floutée d'un fond calculé sur place. Celle d'un fond confié au fil à part est libérée par ce fil, à la fin
    /// de son dernier calcul : elle ne doit pas disparaître sous lui.
    /// </summary>
    private void DropSyncBlur()
    {
        if (_scene is { Sync: true } scene) scene.DropBlur();
    }

    public void Dispose()
    {
        lock (_queueGate)
        {
            if (_disposed) return;

            _disposed = true;
            while (_queue.TryDequeue(out var job)) job.Scene.Release();
            foreach (var result in _results) Discard(result);
            _results.Clear();
        }

        foreach (var slot in _slots.Values) Free(slot);
        _slots.Clear();
        DropSyncBlur();
        _scene = null;
        _background = null;

        // Un calcul en cours sur le fil à part finit d'abord : l'effet n'est jamais libéré sous lui.
        lock (_effectGate) _effect.Dispose();
        _pillEffect?.Dispose();
        _pillEffect = null;
    }

    /// <summary>
    /// La copie floutée de <paramref name="sharp"/>, à pleine taille, bords prolongés (le flou ne s'assombrit pas vers
    /// l'extérieur). Une copie au quart, agrandie, s'écarte trop du flou plein pour les petits flous : voir
    /// <c>QuarterBlur_MatchesFullBlur</c>.
    /// </summary>
    internal static SKImage Blur(SKImage sharp, float sigma)
    {
        var info = new SKImageInfo(sharp.Width, sharp.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Impossible de créer la surface du flou du verre.");
        using var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = blur, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawImage(sharp, 0, 0, paint);
        return surface.Snapshot();
    }

    /// <summary>Les rectangles sont calés sur les pixels : une forme à une position fractionnaire serait rééchantillonnée.</summary>
    private static SKRect Snap(SKRect rect) => new(
        MathF.Round(rect.Left), MathF.Round(rect.Top), MathF.Round(rect.Right), MathF.Round(rect.Bottom));

    private static bool HasNoSurface(SKRect rect) => rect.Width <= 0f || rect.Height <= 0f;

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);

    // ---- Les pastilles ----

    /// <summary>En dessous de 4 px de large ou de haut, une pastille est ignorée.</summary>
    private const float MinPillSide = 4f;

    /// <summary>Vrai si les pixels de <paramref name="area"/> dans l'image sont exactement ceux de <paramref name="scene"/>.</summary>
    private static bool SameScene(SKPixmap frame, SKRectI area, byte[] scene)
    {
        ReadOnlySpan<byte> pixels = frame.GetPixelSpan();
        int row = area.Width * 4;
        if (scene.Length != row * area.Height) return false;

        for (int y = 0; y < area.Height; y++)
        {
            var line = pixels.Slice((area.Top + y) * frame.RowBytes + area.Left * 4, row);
            if (!line.SequenceEqual(scene.AsSpan(y * row, row))) return false;
        }
        return true;
    }

    /// <summary>Copie les pixels de <paramref name="area"/>, lignes serrées, dans <paramref name="reuse"/> s'il a la bonne taille.</summary>
    private static byte[] CopyScene(SKPixmap frame, SKRectI area, byte[]? reuse)
    {
        ReadOnlySpan<byte> pixels = frame.GetPixelSpan();
        int row = area.Width * 4;
        var scene = reuse is not null && reuse.Length == row * area.Height ? reuse : new byte[row * area.Height];
        for (int y = 0; y < area.Height; y++)
        {
            pixels.Slice((area.Top + y) * frame.RowBytes + area.Left * 4, row).CopyTo(scene.AsSpan(y * row, row));
        }
        return scene;
    }

    /// <summary>La pastille de <paramref name="shape"/>, qui déforme la case de <paramref name="scene"/>. Fil de l'affichage.</summary>
    private SKImage ComputePill(byte[] scene, SKRectI area, SKPixmap frame, SKRect shape, GlassSettings settings)
    {
        var info = new SKImageInfo(area.Width, area.Height, frame.ColorType, frame.AlphaType);
        using var sharp = SKImage.FromPixelCopy(info, scene, area.Width * 4)
            ?? throw new InvalidOperationException("Impossible de copier la case pour sa pastille.");
        float sigma = LiquidGlassEffect.BlurSigma(settings.Blur);
        using var blurred = sigma > 0f ? Blur(sharp, sigma) : null;

        var effect = _pillEffect ??= new LiquidGlassEffect();
        var image = effect.RenderGlass(sharp, blurred ?? sharp, new SKPoint(area.Left, area.Top), shape, settings);
        Track(1);
        _pillComputations++;
        return image;
    }

    private void Drop(Pill pill)
    {
        pill.Image.Dispose();
        Track(-1);
    }

    /// <summary>Libère les pastilles de la case et la copie de ses pixels.</summary>
    private void DropPills(Slot slot)
    {
        if (slot.Pills is { } pills)
        {
            foreach (var pill in pills) Drop(pill);
            pills.Clear();
        }
        slot.Scene = null;
    }

    /// <summary>
    /// La case de <paramref name="rect"/>, marquée comme utilisée par cette image, ses couches calculées sur place ou
    /// demandées au fil à part. <c>null</c> pour un rectangle sans surface.
    /// </summary>
    private Slot? Ensure(SKRect rect)
    {
        var scene = _scene ?? throw new InvalidOperationException("BeginFrame n'a pas été appelé.");
        var background = _background!;
        rect = Snap(rect);
        if (HasNoSurface(rect)) return null;

        if (!_slots.TryGetValue(rect, out var slot))
        {
            _slots[rect] = slot = new Slot();
        }
        if (slot.Used) return slot; // déjà vue dans cette image

        slot.Used = true;
        var s = _settings;
        var glassKey = new GlassKey(background, background.Version, rect, s, _panel);
        var shadowKey = new ShadowKey(rect, s.CornerRadius, s.ShadowOpacity, s.ShadowSpread, s.ShadowOffsetY);
        bool needGlass = slot.Glass?.Key != glassKey;
        bool needShadow = slot.Shadow?.Key != shadowKey;

        if (scene.Sync)
        {
            Cancel(slot); // un calcul parti pour un autre fond n'a plus lieu d'être
            if (needShadow)
            {
                var shadow = ComputeShadow(shadowKey, s);
                Drop(slot.Shadow);
                slot.Shadow = shadow;
            }
            if (needGlass)
            {
                var glass = ComputeGlass(glassKey, scene, background.Current);
                Drop(slot.Glass);
                slot.Glass = glass;
            }
            return slot;
        }

        var wantedGlass = needGlass ? glassKey : null;
        ShadowKey? wantedShadow = needShadow ? shadowKey : null;
        if (slot.Job is { } running && running.GlassKey == wantedGlass && running.ShadowKey == wantedShadow)
        {
            return slot; // déjà en route (ou déjà échoué : pas de nouvel essai pour la même demande)
        }

        if (slot.Job is { } old) old.Superseded = true;
        Cancel(slot);
        if (needGlass || needShadow)
        {
            var job = new Job(rect, wantedGlass, wantedShadow, s, scene, scene.Acquire(background.Current));
            slot.Job = job;
            Enqueue(job);
        }
        return slot;
    }

    private GlassLayer ComputeGlass(GlassKey key, Scene scene, SKImage sharp)
    {
        lock (_effectGate)
        {
            var blurred = scene.Blurred(sharp, LiquidGlassEffect.BlurSigma(key.Settings.Blur));
            var image = _effect.RenderGlass(sharp, blurred, SKPoint.Empty, key.Rect, key.Settings);
            Track(1);
            try
            {
                float luminance = MeasureLuminance(sharp, image, key.Rect);
                Interlocked.Increment(ref _computations);
                return new GlassLayer(key, image, luminance);
            }
            catch
            {
                image.Dispose();
                Track(-1);
                throw;
            }
        }
    }

    private ShadowLayer ComputeShadow(ShadowKey key, GlassSettings settings)
    {
        lock (_effectGate)
        {
            var image = _effect.RenderShadow(key.Rect, settings, out var area);
            if (image is not null) Track(1);
            Interlocked.Increment(ref _shadowComputations);
            return new ShadowLayer(key, image, area);
        }
    }

    /// <summary>
    /// La zone la plus claire du verre posé sur le fond (le verre peut être translucide, et son alpha l'assombrit : la mesure
    /// se fait après la pose, pas sur les pixels bruts de la couche), sur une copie réduite au quart.
    /// </summary>
    private static float MeasureLuminance(SKImage sharp, SKImage glass, SKRect rect)
    {
        int width = glass.Width, height = glass.Height;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var full = new SKBitmap(info);
        using (var canvas = new SKCanvas(full))
        {
            using var source = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(sharp, -rect.Left, -rect.Top, source);
            canvas.DrawImage(glass, 0, 0);
        }
        full.SetImmutable();

        int smallWidth = Math.Max(1, (width + LuminanceDivisor - 1) / LuminanceDivisor);
        int smallHeight = Math.Max(1, (height + LuminanceDivisor - 1) / LuminanceDivisor);
        using var small = new SKBitmap(new SKImageInfo(smallWidth, smallHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(small))
        using (var image = SKImage.FromBitmap(full))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKRect(0, 0, smallWidth, smallHeight), Shrink, paint);
        }

        return Backdrop.BrightestLuminance(small, 0, 0, smallWidth, smallHeight);
    }

    // ---- Le fil à part ----

    private void Enqueue(Job job)
    {
        lock (_queueGate)
        {
            _queue.Enqueue(job);
            if (_draining) return;

            _draining = true;
        }

        _ = Task.Run(Drain);
    }

    /// <summary>Vide la file, un calcul à la fois, puis s'arrête : le fil ne vit que pendant les calculs.</summary>
    private void Drain()
    {
        while (true)
        {
            Job job;
            lock (_queueGate)
            {
                if (_disposed || !_queue.TryDequeue(out job!))
                {
                    _draining = false;
                    return;
                }
            }

            try
            {
                if (job.Cancelled) continue;

                var result = Compute(job);
                bool kept;
                lock (_queueGate)
                {
                    kept = !_disposed;
                    if (kept) _results.Add(result);
                }

                if (kept)
                {
                    RaiseReady();
                }
                else
                {
                    Discard(result);
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _workerFailures); // rien ne doit arrêter le fil : les calculs suivants se font
            }
            finally
            {
                job.Scene.Release();
            }
        }
    }

    private void RaiseReady()
    {
        try
        {
            Ready?.Invoke();
        }
        catch (Exception)
        {
            // Un abonné qui lève n'arrête pas le fil : les calculs suivants de la file doivent se faire.
        }
    }

    private Result Compute(Job job)
    {
        GlassLayer? glass = null;
        ShadowLayer? shadow = null;
        try
        {
            lock (_effectGate)
            {
                if (_disposed) return new Result(job, null, null, null); // l'effet est libéré : rien à calculer

                // Annulé en route sans être remplacé (case retirée, autre thème) : le verre, le plus long, n'est pas fait.
                bool dropped = job.Cancelled && !job.Superseded;
                if (job.ShadowKey is { } shadowKey) shadow = ComputeShadow(shadowKey, job.Settings);
                if (job.GlassKey is { } glassKey && !dropped) glass = ComputeGlass(glassKey, job.Scene, job.Sharp);
            }

            return new Result(job, glass, shadow, null);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _workerFailures);
            Drop(glass);
            Drop(shadow);
            return new Result(job, null, null, ex);
        }
    }

    /// <summary>Pose les couches finies par le fil à part sur les cases qui les attendent encore ; les autres sont libérées.</summary>
    private void Collect()
    {
        if (!_inBackground) return;

        Result[] results;
        lock (_queueGate)
        {
            if (_results.Count == 0) return;

            results = [.. _results];
            _results.Clear();
        }

        foreach (var result in results)
        {
            var job = result.Job;
            if (!_slots.TryGetValue(job.Rect, out var slot))
            {
                Discard(result); // la case n'existe plus
                continue;
            }

            if (!ReferenceEquals(slot.Job, job))
            {
                // La case a changé d'avis entre-temps. Si elle attend toujours un verre pour le même fond, celui-ci, parti
                // après celui qu'elle montre, sert en attendant ; son ombre, elle, n'est pas reprise.
                if (job.Superseded && result.Glass is { } interim && slot.Job is { GlassKey: { } wanted }
                    && ReferenceEquals(wanted.Background, interim.Key.Background) && wanted.Version == interim.Key.Version)
                {
                    Drop(slot.Glass);
                    slot.Glass = interim;
                    Drop(result.Shadow);
                }
                else
                {
                    Discard(result);
                }
                continue;
            }

            if (result.Error is { } error)
            {
                // Le calcul reste la demande en cours de la case : pas de nouvel essai tant qu'elle demande la même chose.
                _report?.Invoke($"Échec du calcul du verre d'une case : {error}");
                continue;
            }

            slot.Job = null;
            if (result.Shadow is { } shadow)
            {
                Drop(slot.Shadow);
                slot.Shadow = shadow;
            }
            if (result.Glass is { } glass)
            {
                Drop(slot.Glass);
                slot.Glass = glass;
            }
        }
    }

    private void Track(int change) => Interlocked.Add(ref _liveImages, change);

    private void Drop(GlassLayer? layer)
    {
        if (layer is null) return;

        layer.Image.Dispose();
        Track(-1);
    }

    private void Drop(ShadowLayer? layer)
    {
        if (layer?.Image is not { } image) return;

        image.Dispose();
        Track(-1);
    }

    private static void Cancel(Slot slot)
    {
        if (slot.Job is not { } job) return;

        job.Cancelled = true;
        slot.Job = null;
    }

    private void Free(Slot slot)
    {
        Cancel(slot);
        Drop(slot.Glass);
        Drop(slot.Shadow);
        slot.Glass = null;
        slot.Shadow = null;
        DropPills(slot);
    }

    private void Discard(Result result)
    {
        Drop(result.Glass);
        Drop(result.Shadow);
    }
}
