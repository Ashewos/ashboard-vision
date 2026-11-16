using System.Diagnostics;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Rendering;

/// <summary>Compose l'image complète de l'écran à partir de la disposition, des modules, du thème et du fond.</summary>
public sealed class FrameComposer : IDisposable
{
    private const string ErrorText = "Erreur du module";

    /// <summary>
    /// Combien de pastilles une case peut avoir par image : les éléments choisis notés au-delà sont ignorés. Chaque pastille de
    /// verre coûte un calcul sur le fil de l'affichage et une image gardée. Ce nombre ne borne pas à lui seul le coût, qui suit
    /// la surface : voir <see cref="MaxPillAreaPerCase"/>.
    /// </summary>
    internal const int MaxPillsPerCase = 16;

    /// <summary>
    /// La surface totale, en pixels, des pastilles de verre d'une case par image. Les pastilles sont comptées dans l'ordre où le
    /// module les note, chacune pour sa surface coupée à la case ; celle qui ferait dépasser ce qui reste du budget n'est pas
    /// calculée en verre : elle est dessinée par <see cref="ModuleDrawing.DrawLocalSelection"/> (la pilule claire, sans calcul),
    /// avec la même coupe et la même opacité, après les pastilles de verre de la case. Une pastille plus petite notée ensuite
    /// peut encore tenir dans le reste.
    /// <para>
    /// Pourquoi : le calcul d'une pastille se fait sur le fil de l'affichage et son coût suit sa surface. Sans cette borne, un
    /// module (de la communauté, hostile ou simplement bogué) qui note 16 rectangles de la taille de sa case, différents à
    /// chaque image, faisait calculer 16 verres de la taille de la case par image : 347 ms par image pour une case 4 × 2,
    /// 1 479 ms pour 8 × 4, 3 092 ms pour 16 × 4, contre 1,5 à 2 ms sans pastille (relecture finale du plan 9). Tout l'écran
    /// tombait à 1 à 3 images par seconde. 40 000 px, c'est plusieurs onglets de Claude Code (environ 10 000 px chacun), et
    /// moins de la moitié d'une case 4 × 2.
    /// </para>
    /// </summary>
    internal const int MaxPillAreaPerCase = 40_000;

    /// <summary>Les éléments choisis qu'un module note pendant son dessin, relatifs à sa case.</summary>
    private sealed class SelectionList : ISelectionSink
    {
        public List<(SKRect Rect, float Opacity)> Rects { get; } = [];

        public void Add(SKRect caseLocalRect, float opacity = 1f)
        {
            if (Rects.Count >= MaxPillsPerCase) return;

            Rects.Add((caseLocalRect, float.IsFinite(opacity) ? Math.Clamp(opacity, 0f, 1f) : 1f));
        }
    }

    /// <summary>Le format de l'image de l'écran, celui de <c>new SKBitmap(1920, 462)</c> : la surface de la carte et le bitmap rendu l'ont aussi.</summary>
    private static readonly SKImageInfo FrameInfo = new(GridGeometry.Width, GridGeometry.Height);

    private readonly RateLimitedErrorLog _errors;
    private readonly Backdrop _backdrop = new();
    private readonly PanelAlphaSmoother _alpha = new(); // opacité des panneaux sur un fond animé, par rectangle
    private readonly bool _backgroundGlass;

    // La carte graphique, si le service en a donné une, et la surface 1920 × 462 qu'on y garde d'une image à l'autre.
    // Le compositeur n'est pas propriétaire de l'appareil : il ne le libère jamais.
    private readonly IGpuDevice? _gpu;
    private SKSurface? _target;
    private bool _gpuFailed;

    // Les couches de « Liquid Glass avancé » : créées à la première image de ce thème, libérées dès qu'un autre thème est composé.
    private LiquidGlassLayers? _glass;

    // Fond uni de la surcharge sans fond, gardé tant que la couleur du thème ne change pas.
    private ColorBackground? _legacyBackground;
    private SKColor _legacyColor;

    /// <param name="errors">Destination des échecs de rendu ; par défaut le <see cref="Trace"/>, une fois par minute et par message.</param>
    /// <param name="backgroundGlass">
    /// Vrai : sur un fond fixe, les couches de verre de « Liquid Glass avancé » se calculent sur un fil à part. En attendant, une
    /// case garde sa couche précédente, ou à défaut le verre simple de Liquid Glass, et <see cref="GlassReady"/> prévient quand
    /// une couche est prête. Faux : tout est calculé pendant <c>Compose</c>. Sans effet sur la carte : le verre s'y calcule toujours
    /// pendant <c>Compose</c>, sur le fil de l'affichage, le seul qui touche l'appareil.
    /// </param>
    /// <param name="gpu">
    /// La carte graphique sur laquelle dessiner, ou <c>null</c> pour tout dessiner sur le processeur (outils, tests, machine sans
    /// carte, accélération coupée) : l'image est alors identique au pixel près à celle d'avant la carte. Le compositeur ne libère
    /// jamais l'appareil, qui appartient au service ; il doit être libéré avant lui. Un appareil ne se sert que d'un fil à la fois.
    /// </param>
    public FrameComposer(RateLimitedErrorLog? errors = null, bool backgroundGlass = false, IGpuDevice? gpu = null)
    {
        _errors = errors ?? new RateLimitedErrorLog(TimeProvider.System);
        _backgroundGlass = backgroundGlass;
        _gpu = gpu;
    }

    /// <summary>Vrai tant que l'appareil donné à la construction marche : les images se dessinent sur la carte, verre avancé compris.</summary>
    public bool UsesGpu => _gpu is not null && !_gpuFailed;

    /// <summary>
    /// La carte a été abandonnée (perdue, ou une surface refusée) : le message dit pourquoi, en français. Levé une seule fois, depuis
    /// le fil de la composition, après quoi tout se dessine sur le processeur. L'image en cours sort quand même.
    /// </summary>
    public event Action<string>? GpuFailed;

    /// <summary>
    /// Une couche de verre calculée sur le fil à part est prête : la prochaine composition la montrera. Levé depuis ce fil.
    /// </summary>
    public event Action? GlassReady;

    /// <summary>Les couches de « Liquid Glass avancé » en service ; <c>null</c> hors de ce thème (tests).</summary>
    internal LiquidGlassLayers? Glass => _glass;

    /// <summary>Vrai quand les couches de verre en service se calculent sur la carte graphique (tests).</summary>
    internal bool GlassOnGpu => _glass?.OnGpu ?? false;

    /// <summary>Combien de couches de verre ont été calculées depuis que le thème « Liquid Glass avancé » est composé (tests).</summary>
    internal int GlassComputations => _glass?.Computations ?? 0;

    /// <summary>Combien de couches d'ombre ont été calculées (tests).</summary>
    internal int GlassShadowComputations => _glass?.ShadowComputations ?? 0;

    /// <summary>Combien de pastilles de verre ont été calculées (tests).</summary>
    internal int GlassPillComputations => _glass?.PillComputations ?? 0;

    /// <summary>Combien de cases gardent une couche de verre ou d'ombre en mémoire (tests).</summary>
    internal int GlassLayers => _glass?.LiveLayers ?? 0;

    /// <summary>Combien de fois le fond flouté du verre a été recalculé (tests).</summary>
    internal int BackdropComputations => _backdrop.Computations;

    /// <summary>
    /// Comme <see cref="Compose(IReadOnlyList{ModulePlacement}, IReadOnlyDictionary{Guid, IModule}, ThemeDefinition, IBackground, DateTimeOffset)"/>
    /// avec le dessin de Classique (panneau mat) pour <paramref name="theme"/> et un fond uni de sa couleur de fond.
    /// </summary>
    public SKBitmap Compose(
        IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules,
        Theme theme,
        DateTimeOffset now)
    {
        if (_legacyBackground is null || _legacyColor != theme.Background)
        {
            _legacyBackground?.Dispose();
            _legacyBackground = new ColorBackground(theme.Background);
            _legacyColor = theme.Background;
        }

        return Compose(placements, modules, Themes.ClassiqueDefinition with { Theme = theme }, _legacyBackground, now);
    }

    /// <summary>
    /// Peint le fond, puis pour chaque case de <paramref name="placements"/> (les cases d'une vue) son panneau (mat, verre ou pixel, selon le thème) et le rendu du
    /// module, rogné à la forme du panneau (1920 × 462). En « Liquid Glass avancé », les ombres de toutes les cases passent
    /// d'abord, puis chaque case pose son verre, sa teinte et son module. Quand le thème confie au service la pastille de
    /// l'élément choisi (<c>Theme.ServiceDrawsSelection</c>), le dessin du module est enregistré, ses pastilles sont peintes,
    /// puis il est rejoué par-dessus : les pastilles passent sous tout ce que le module dessine.
    /// </summary>
    public SKBitmap Compose(
        IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules,
        ThemeDefinition theme,
        IBackground background,
        DateTimeOffset now)
    {
        if (UsesGpu && TryComposeOnGpu(placements, modules, theme, background, now) is { } frame)
        {
            return frame;
        }

        return ComposeOnCpu(placements, modules, theme, background, now);
    }

    private SKBitmap ComposeOnCpu(
        IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules,
        ThemeDefinition theme,
        IBackground background,
        DateTimeOffset now)
    {
        _backdrop.Device = null;

        var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        try
        {
            using var canvas = new SKCanvas(bitmap);
            using var pixels = bitmap.PeekPixels();
            DrawFrame(canvas, pixels, null, placements, modules, theme, background, now);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose(); // pas de bitmap orphelin de 3,5 Mo si la composition échoue
            throw;
        }
    }

    /// <summary>
    /// Compose l'image sur la surface de la carte, puis la rapatrie dans un bitmap neuf, du même format que celui du processeur.
    /// Rend <c>null</c>, après avoir abandonné la carte (<see cref="GpuFailed"/>), si la carte est perdue ou refuse une surface, en
    /// cours d'image ou à l'envoi du travail : l'appelant refait alors toute l'image sur le processeur.
    /// </summary>
    private SKBitmap? TryComposeOnGpu(
        IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules,
        ThemeDefinition theme,
        IBackground background,
        DateTimeOffset now)
    {
        var gpu = _gpu!;
        SKBitmap? bitmap = null;
        try
        {
            _backdrop.Device = gpu;
            _target ??= gpu.CreateSurface(FrameInfo);
            var canvas = _target.Canvas;
            canvas.Clear(SKColors.Transparent);
            DrawFrame(canvas, null, _target, placements, modules, theme, background, now); // les pastilles prennent la surface en instantané

            gpu.Submit();
            ThrowIfLost(gpu);

            bitmap = new SKBitmap(FrameInfo);
            if (!_target.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                ThrowIfLost(gpu);
                throw new GpuLostException("La carte graphique n'a pas rendu l'image dessinée.");
            }

            ThrowIfLost(gpu); // la lecture attend la carte : une panne en cours de route se voit ici
            return bitmap;
        }
        catch (GpuLostException ex)
        {
            bitmap?.Dispose();
            AbandonGpu(ex.Message);
            return null;
        }
        catch (Exception) when (gpu.IsLost)
        {
            // Skia a pu lever autre chose qu'une GpuLostException sur une carte tombée en pleine image.
            bitmap?.Dispose();
            AbandonGpu(gpu.LostReason ?? "La carte graphique est perdue.");
            return null;
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
    }

    private static void ThrowIfLost(IGpuDevice gpu)
    {
        if (gpu.IsLost) throw new GpuLostException(gpu.LostReason ?? "La carte graphique est perdue.");
    }

    /// <summary>Renonce à la carte : ses surfaces sont jetées, le compositeur ne la touche plus (elle reste au service), l'événement est levé.</summary>
    private void AbandonGpu(string reason)
    {
        if (_gpuFailed) return;

        _gpuFailed = true;
        _backdrop.Device = null;
        DropGlass(); // ses couches sont des images de la carte : elles partent avec elle, le processeur refera les siennes
        try
        {
            _target?.Dispose();
        }
        catch (Exception ex)
        {
            _errors.Report($"Impossible de libérer la surface de la carte graphique perdue : {ex.Message}");
        }

        _target = null;
        GpuFailed?.Invoke(reason);
    }

    /// <summary>
    /// Peint tout l'écran sur <paramref name="canvas"/> (1920 × 462). <paramref name="pixels"/> : les pixels de l'image sur le
    /// processeur, d'où les pastilles de verre lisent le fond ; <c>null</c> sur la carte, où <paramref name="gpuFrame"/> (la
    /// surface de la carte que peint <paramref name="canvas"/>) les remplace. Un seul des deux est donné.
    /// </summary>
    private void DrawFrame(
        SKCanvas canvas, SKPixmap? pixels, SKSurface? gpuFrame, IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules, ThemeDefinition theme, IBackground background, DateTimeOffset now)
    {
        DrawBackground(canvas, background);

        LiquidGlassLayers? glass = null;
        if (theme.Panel.Kind == PanelKind.LiquidGlass)
        {
            glass = GlassFor(onGpu: gpuFrame is not null);
            glass.BeginFrame(background, theme);
            glass.PaintShadows(canvas, [.. placements.Select(placement => ModulePlace.Rect(placement, theme))]);
        }
        else
        {
            DropGlass(); // un autre thème : aucune couche ne reste en mémoire
        }

        foreach (var placement in placements)
        {
            var rect = ModulePlace.Rect(placement, theme);
            PaintPanel(canvas, rect, theme, background, now);

            if (!modules.TryGetValue(placement.InstanceId, out var module))
            {
                continue;
            }

            if (theme.Theme.ServiceDrawsSelection)
            {
                RenderOverSelections(canvas, pixels, gpuFrame, placement, module, rect, theme, background, now);
                continue;
            }

            int saved = canvas.Save();
            try
            {
                PanelPainter.ClipToPanel(canvas, rect, theme); // même forme que le panneau
                module.Render(canvas, ModulePlace.Context(placement, theme, now));
            }
            catch (Exception ex)
            {
                _errors.Report($"Échec du rendu du module {placement.TypeId} ({placement.InstanceId}) : {ex}");
                canvas.RestoreToCount(saved);
                PaintErrorPanel(canvas, rect, theme, background, now);
            }
            finally
            {
                canvas.RestoreToCount(saved);
            }
        }

        glass?.EndFrame();
    }

    public void Dispose()
    {
        DropGlass();
        _target?.Dispose();
        _target = null;
        _backdrop.Dispose();
        _legacyBackground?.Dispose();
        _legacyBackground = null;
    }

    /// <summary>
    /// Le dessin d'un module quand le service dessine la pastille de ses éléments choisis : le module dessine dans un
    /// enregistrement, où <see cref="ModuleDrawing.DrawSelection"/> ne fait que noter des rectangles ; les pastilles sont peintes
    /// sur la case, puis l'enregistrement est rejoué par-dessus, rogné à la forme du panneau. Un module qui échoue n'a ni
    /// pastille ni dessin : le panneau d'erreur.
    /// </summary>
    private void RenderOverSelections(
        SKCanvas canvas, SKPixmap? pixels, SKSurface? gpuFrame, ModulePlacement placement, IModule module, SKRect rect,
        ThemeDefinition theme, IBackground background, DateTimeOffset now)
    {
        var context = ModulePlace.Context(placement, theme, now);
        var selections = new SelectionList();
        SKPicture picture;
        try
        {
            picture = Record(SKPoint.Empty, context, selections, module.Render);
        }
        catch (Exception ex)
        {
            _errors.Report($"Échec du rendu du module {placement.TypeId} ({placement.InstanceId}) : {ex}");
            PaintErrorPanel(canvas, rect, theme, background, now);
            return;
        }

        using (picture)
        {
            PaintSelectionsThenPicture(
                canvas, pixels, gpuFrame, placement.TypeId, placement.InstanceId, rect, theme, context.Bounds.Location, selections, picture);
        }
    }

    /// <summary>
    /// Enregistre le dessin d'un module : <see cref="ModuleDrawing.DrawSelection"/> y note ses rectangles dans
    /// <paramref name="selections"/> au lieu de les dessiner. L'enregistrement couvre tout l'écran ; <paramref name="origin"/> décale
    /// le dessin (0 pour un module dont les bornes sont déjà celles de l'écran).
    /// </summary>
    private static SKPicture Record(SKPoint origin, ModuleRenderContext context, SelectionList selections, Action<SKCanvas, ModuleRenderContext> render)
    {
        using var recorder = new SKPictureRecorder();
        var recording = recorder.BeginRecording(SKRect.Create(GridGeometry.Width, GridGeometry.Height));
        recording.Translate(origin.X, origin.Y);
        render(recording, context with { Selections = selections });
        return recorder.EndRecording();
    }

    /// <summary>
    /// Peint les pastilles notées (décalées de <paramref name="offset"/>, qui les ramène à l'écran), puis rejoue l'enregistrement par-dessus,
    /// rogné à la forme du panneau : les pastilles passent sous tout ce que le module dessine. Une pastille qui échoue ne prive pas le
    /// module de son dessin. Les pastilles de verre se partagent le budget de surface de la case (<see cref="MaxPillAreaPerCase"/>) ;
    /// au-delà, la pilule claire. Ce chemin sert aux modules natifs, à ceux de la communauté et aux aides de test du SDK.
    /// </summary>
    private void PaintSelectionsThenPicture(
        SKCanvas canvas, SKPixmap? pixels, SKSurface? gpuFrame, string typeId, Guid instanceId, SKRect rect, ThemeDefinition theme,
        SKPoint offset, SelectionList selections, SKPicture picture)
    {
        int saved = canvas.Save();
        try
        {
            long budget = MaxPillAreaPerCase; // ce qui reste de verre à cette case, pour cette image
            List<(SKRect Pill, float Opacity)>? overBudget = null;
            foreach (var (local, opacity) in selections.Rects)
            {
                var pill = local;
                pill.Offset(offset.X, offset.Y);
                if (!(opacity > 0f)) continue; // invisible : ni dessin ni budget

                long area = PillArea(rect, pill);
                if (area > budget)
                {
                    (overBudget ??= []).Add((pill, opacity));
                    continue;
                }

                budget -= area;
                PaintSelection(canvas, pixels, gpuFrame, rect, pill, opacity, theme, glass: true);
            }

            // Les pilules claires passent après les pastilles de verre : le verre déforme la case nue, et reste gardé en mémoire
            // d'une image à l'autre même si ces pilules changent.
            foreach (var (pill, opacity) in overBudget ?? [])
            {
                PaintSelection(canvas, pixels, gpuFrame, rect, pill, opacity, theme, glass: false);
            }
        }
        catch (Exception ex) when (!IsGpuFailure(ex, gpuFrame))
        {
            // Une pastille qui échoue ne prive pas le module de son dessin. Une carte perdue, elle, remonte : l'image se refait
            // sur le processeur.
            _errors.Report($"Échec du dessin de la pastille du module {typeId} ({instanceId}) : {ex}");
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }

        saved = canvas.Save();
        try
        {
            PanelPainter.ClipToPanel(canvas, rect, theme); // même forme que le panneau
            canvas.DrawPicture(picture);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>
    /// Le calque d'une case pour les aides de test du SDK : ce que le service poserait sur le panneau de la case, et rien d'autre.
    /// Le panneau (fond, ombre, verre, teinte) est peint comme à l'écran, sur le fond par défaut du thème, à la place (0, 0) du damier,
    /// mais seulement dans une image à part, d'où les pastilles lisent leurs pixels ; le calque rendu est transparent hors des pastilles
    /// et du dessin du module. <paramref name="render"/> reçoit un canevas dont l'origine est le coin de la case et un contexte aux
    /// bornes locales (0, 0, largeur, hauteur) : une exception qu'elle lève sort telle quelle. Un thème sans pastille du service
    /// n'a pas besoin de ce chemin.
    /// </summary>
    /// <param name="theme">Le thème (« Liquid Glass avancé » pour voir la vraie pastille).</param>
    /// <param name="size">La taille de la case sur le damier.</param>
    /// <param name="textScale">L'échelle du texte de la case.</param>
    /// <param name="now">L'heure du contexte.</param>
    /// <param name="render">Le dessin du module.</param>
    internal SKBitmap ComposeCaseLayer(
        ThemeDefinition theme, GridSize size, float textScale, DateTimeOffset now, Action<SKCanvas, ModuleRenderContext> render)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(render);

        _backdrop.Device = null; // le calque se peint sur le processeur, comme sans carte

        var area = GridGeometry.ModuleRect(0, 0, size.Columns, size.Rows, theme.Grid);
        var rect = SKRect.Create(area.Left, area.Top, area.Width, area.Height);
        var context = new ModuleRenderContext(new SKRect(0, 0, area.Width, area.Height), size, theme.Theme, textScale, now);

        using var background = BackgroundFactory.Create(null, theme);
        using var frame = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using var layer = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using var frameCanvas = new SKCanvas(frame);
        using var layerCanvas = new SKCanvas(layer);
        using var pixels = frame.PeekPixels();
        layerCanvas.Clear(SKColors.Transparent);
        DrawBackground(frameCanvas, background);

        LiquidGlassLayers? glass = null;
        if (theme.Panel.Kind == PanelKind.LiquidGlass)
        {
            glass = GlassFor(onGpu: false); // le calque se peint sur le processeur
            glass.BeginFrame(background, theme);
            glass.PaintShadows(frameCanvas, [rect]);
        }
        else
        {
            DropGlass();
        }

        PaintPanel(frameCanvas, rect, theme, background, now);

        var selections = new SelectionList();
        using (var picture = Record(rect.Location, context, selections, render))
        {
            PaintSelectionsThenPicture(layerCanvas, pixels, null, "module d'essai", Guid.Empty, rect, theme, rect.Location, selections, picture);
        }

        glass?.EndFrame();

        var result = new SKBitmap(new SKImageInfo(area.Width, area.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(result);
            canvas.Clear(SKColors.Transparent);
            using var copy = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(layer, -area.Left, -area.Top, copy);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// La pastille d'un élément choisi, coupée à la case : en verre avec « Liquid Glass avancé », sinon (le drapeau sur un thème
    /// d'un autre panneau) la pilule claire de <see cref="ModuleDrawing.DrawLocalSelection"/>. Une <paramref name="opacity"/> inférieure à 1
    /// (l'image du module est voilée) pose la pastille dans un calque de cette opacité ; à 1, le chemin est celui d'avant.
    /// <paramref name="glass"/> faux (le budget de surface de la case est dépassé, voir <see cref="MaxPillAreaPerCase"/>) : la
    /// pilule claire aussi, sans aucun calcul de verre, avec la même coupe et le même calque d'opacité.
    /// </summary>
    private void PaintSelection(
        SKCanvas canvas, SKPixmap? pixels, SKSurface? gpuFrame, SKRect rect, SKRect pill, float opacity, ThemeDefinition theme, bool glass)
    {
        if (opacity < 1f)
        {
            if (!(opacity > 0f)) return;

            using var veil = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255f)) };
            int layer = canvas.SaveLayer(rect, veil);
            try
            {
                PaintSelection(canvas, pixels, gpuFrame, rect, pill, 1f, theme, glass);
            }
            finally
            {
                canvas.RestoreToCount(layer);
            }

            return;
        }

        if (glass && theme.Panel.Kind == PanelKind.LiquidGlass && _glass is { } layers)
        {
            if (pixels is not null && !layers.OnGpu)
            {
                layers.PaintPill(canvas, pixels, rect, pill);
                return;
            }

            if (gpuFrame is not null && layers.OnGpu)
            {
                layers.PaintPill(canvas, gpuFrame, rect, pill);
                return;
            }
        }

        var shape = SKRect.Intersect(pill, rect);
        if (!(shape.Width >= 4f) || !(shape.Height >= 4f)) return;

        canvas.Save();
        PanelPainter.ClipToPanel(canvas, rect, theme);
        ModuleDrawing.DrawLocalSelection(canvas, shape, theme.Theme);
        canvas.Restore();
    }

    /// <summary>
    /// La surface, en pixels, de la pastille coupée à la case, calée sur les pixels comme le fait
    /// <see cref="LiquidGlassLayers.PaintPill"/>. 0 pour une pastille vide, hors de la case ou qui n'est pas faite de nombres
    /// finis : elle ne coûte rien, <c>PaintPill</c> l'ignore.
    /// </summary>
    private static long PillArea(SKRect rect, SKRect pill)
    {
        static SKRect Snap(SKRect r) => new(MathF.Round(r.Left), MathF.Round(r.Top), MathF.Round(r.Right), MathF.Round(r.Bottom));

        var shape = SKRect.Intersect(Snap(pill), Snap(rect));
        if (!(shape.Width > 0f) || !(shape.Height > 0f)) return 0; // vide, ou pas un nombre

        return (long)Math.Ceiling((double)shape.Width * shape.Height);
    }

    /// <summary>
    /// Les couches de verre en service, du bon côté : sur la carte (images de la carte, calculées sur place) ou sur le
    /// processeur. Un changement de côté (le calque d'essai du SDK, la carte abandonnée) les jette : elles ne passent pas de l'un à l'autre.
    /// </summary>
    private LiquidGlassLayers GlassFor(bool onGpu)
    {
        if (_glass is { } existing && existing.OnGpu != onGpu) DropGlass();

        return _glass ??= NewGlassLayers(onGpu);
    }

    private LiquidGlassLayers NewGlassLayers(bool onGpu)
    {
        var layers = new LiquidGlassLayers(_backgroundGlass, _errors.Report, onGpu ? _gpu : null);
        layers.Ready += OnGlassReady;
        return layers;
    }

    /// <summary>Une panne de la carte, à laisser remonter : seulement sur la carte (sur le processeur, un échec de pastille reste un échec de pastille).</summary>
    private bool IsGpuFailure(Exception ex, SKSurface? gpuFrame) =>
        gpuFrame is not null && (ex is GpuLostException || (_gpu?.IsLost ?? false));

    private void OnGlassReady() => GlassReady?.Invoke();

    private void DropGlass()
    {
        if (_glass is null) return;

        _glass.Ready -= OnGlassReady;
        _glass.Dispose();
        _glass = null;
    }

    // L'image courante du fond n'est jamais gardée : elle n'est valable que pour cette image de l'écran.
    private static void DrawBackground(SKCanvas canvas, IBackground background)
    {
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(background.Current, 0, 0, paint);
    }

    /// <summary>
    /// Peint le panneau. Sur un fond fixe, l'opacité est celle que la garde de lisibilité veut tout de suite. Sur un fond animé,
    /// elle est lissée dans le temps (<see cref="PanelAlphaSmoother"/>) : une tache claire qui passe sous le panneau ne le fait
    /// pas clignoter. Garde coupée : l'opacité choisie, sans mesure ni lissage.
    /// </summary>
    private void PaintPanel(SKCanvas canvas, SKRect rect, ThemeDefinition theme, IBackground background, DateTimeOffset now)
    {
        if (theme.Panel.Kind == PanelKind.LiquidGlass)
        {
            if (_glass is { } glass && glass.IsReady(rect))
            {
                PaintGlassPanel(canvas, rect, theme, background, now, glass);
                return;
            }

            // Le verre de la case se calcule encore sur le fil à part : en attendant, le verre simple de Liquid Glass, à la
            // forme du verre attendu.
            theme = theme with
            {
                Panel = Themes.LiquidGlass.Panel,
                Theme = theme.Theme with { CornerRadius = PanelPainter.CornerRadius(theme, rect) },
            };
        }

        var blurred = theme.Panel.Kind == PanelKind.Glass ? _backdrop.Get(background, theme.Panel) : null;
        if (!theme.LegibilityGuard)
        {
            PanelPainter.Paint(canvas, rect, theme, blurred, background.Current);
            return;
        }

        float target = PanelPainter.TargetAlpha(theme, _backdrop.BrightestLuminance(background, rect));
        float alpha = background.IsAnimated ? _alpha.Smooth(rect, target, now) : target;
        PanelPainter.Paint(canvas, rect, theme, blurred, background.Current, alpha: alpha);
    }

    /// <summary>
    /// Le panneau de « Liquid Glass avancé » : la couche de verre gardée en mémoire, puis la teinte, qui n'est jamais gardée
    /// avec elle. La garde de lisibilité mesure la zone la plus claire du verre posé sur le fond (les reflets y sont déjà) ;
    /// l'opacité est lissée sur un fond animé, comme pour les autres thèmes.
    /// </summary>
    private void PaintGlassPanel(
        SKCanvas canvas, SKRect rect, ThemeDefinition theme, IBackground background, DateTimeOffset now, LiquidGlassLayers glass)
    {
        glass.PaintGlass(canvas, rect);

        float alpha = theme.Theme.PanelOpacity;
        if (theme.LegibilityGuard)
        {
            float target = PanelPainter.TargetAlpha(theme, glass.Luminance(rect));
            alpha = background.IsAnimated ? _alpha.Smooth(rect, target, now) : target;
        }

        PanelPainter.Paint(canvas, rect, theme, null, background.Current, alpha: alpha);
    }

    /// <summary>Efface ce que le module a pu peindre avant d'échouer, puis affiche le message d'erreur.</summary>
    private void PaintErrorPanel(SKCanvas canvas, SKRect rect, ThemeDefinition theme, IBackground background, DateTimeOffset now)
    {
        canvas.Save();
        PanelPainter.ClipToPanel(canvas, rect, theme); // le module n'a rien pu peindre au-delà : l'ombre et la bordure restent
        DrawBackground(canvas, background);
        PaintPanel(canvas, rect, theme, background, now);

        using var font = ModuleDrawing.Font(theme.Theme, 24f);
        float maxWidth = rect.Width - 16f;
        float width = font.MeasureText(ErrorText);
        if (width > maxWidth && maxWidth > 0)
        {
            font.Size = font.Size * maxWidth / width;
        }

        using var paint = new SKPaint { IsAntialias = true, Color = theme.Theme.Warning };
        float baseline = rect.MidY - (font.Metrics.Ascent + font.Metrics.Descent) / 2f;
        canvas.DrawText(ErrorText, rect.MidX, baseline, SKTextAlign.Center, font, paint);
        canvas.Restore();
    }
}
