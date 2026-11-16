using SkiaSharp;
using Trofeo.Core.Layout;

namespace Trofeo.Core.Rendering;

/// <summary>Fond de l'écran, toujours 1920 × 462. Utilisé par un seul thread (celui de l'affichage).</summary>
public interface IBackground : IDisposable
{
    bool IsAnimated { get; }

    /// <summary>
    /// Image courante, recadrée pour remplir l'écran. Même instance tant que <see cref="Version"/> ne change pas ;
    /// après un <see cref="Advance"/> qui rend vrai, l'ancienne instance n'est plus valable.
    /// </summary>
    SKImage Current { get; }

    /// <summary>Change à chaque nouvelle image (le flou du verre s'appuie dessus).</summary>
    int Version { get; }

    /// <summary>Avance au temps écoulé depuis le début de la lecture ; vrai si l'image a changé.</summary>
    bool Advance(TimeSpan elapsed);

    /// <summary>Délai avant la prochaine image depuis le dernier <see cref="Advance"/> ; null pour un fond fixe.</summary>
    TimeSpan? UntilNextFrame { get; }
}

/// <summary>
/// Un fond qui sait où il en est et qui l'a décodé : le service peut le rouvrir à la même position quand la carte qui décode
/// ses images change (réglages, panne de la carte, nouvel essai).
/// </summary>
public interface ISeekableBackground
{
    /// <summary>L'instant de la vidéo montré en ce moment, depuis son début : la position où reprendre quand on la rouvre.</summary>
    TimeSpan Position { get; }

    /// <summary>
    /// L'identifiant (LUID) de la carte graphique qui décode les images ; <c>null</c> : le processeur les décode.
    /// Seule la carte qui décode réellement compte : un décodage par la carte qui a échoué à l'ouverture donne <c>null</c>.
    /// </summary>
    long? DecoderLuid { get; }
}

/// <summary>
/// Un fond dont l'image est déjà sur la carte graphique (vidéo décodée par la carte) : le compositeur la prend telle quelle,
/// sans la rapatrier sur le processeur puis la renvoyer.
/// </summary>
public interface IGpuFrameSource
{
    /// <summary>
    /// L'image courante du fond sur <paramref name="device"/>, recadrée comme <see cref="IBackground.Current"/>, ou <c>null</c> si elle
    /// n'est pas sur cette carte (le compositeur prend alors <see cref="IBackground.Current"/>). L'appelant en devient propriétaire :
    /// il la libère avant l'appareil. Appelée depuis le fil de l'affichage, comme tout ce que fait un fond.
    /// </summary>
    SKImage? TryGetGpuFrame(IGpuDevice device);
}

/// <summary>Aides communes aux fonds : recadrage, décodage d'une image fixe, textes d'avertissement.</summary>
internal static class BackgroundSupport
{
    /// <summary>Au-delà de cette taille (par côté), une image est refusée.</summary>
    public const int MaxSide = 16384;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static SKImageInfo ScreenInfo { get; } =
        new(GridGeometry.Width, GridGeometry.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

    public static string Missing(string path) => $"Fond d'écran introuvable : {path}. Fond du thème utilisé.";
    public static string Unreadable(string path) => $"Fond d'écran illisible : {path}. Fond du thème utilisé.";

    /// <summary>Comme <see cref="Unreadable(string)"/>, avec la cause de l'échec.</summary>
    public static string Unreadable(string path, string cause) =>
        $"Fond d'écran illisible : {path} ({cause}). Fond du thème utilisé.";
    public static string BadColor(string? color) => $"Couleur de fond invalide : {color}. Fond du thème utilisé.";
    public static string NoVideo(string? path) => $"Vidéo non prise en charge ici : {path}. Fond du thème utilisé.";

    /// <summary>Rend immuable puis enveloppe l'image : les pixels sont partagés, le bitmap peut être libéré.</summary>
    public static SKImage Freeze(SKBitmap bitmap)
    {
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>
    /// Dessine <paramref name="source"/> sur tout l'écran, centrée : échelle = max(1920 / l, 462 / h)
    /// (l'image remplit l'écran, le surplus est coupé). <paramref name="origin"/> est l'orientation EXIF de la photo :
    /// le recadrage se calcule sur la taille redressée, puis la rotation ou le miroir est appliqué au dessin.
    /// </summary>
    public static void DrawCover(SKCanvas canvas, SKImage source, SKEncodedOrigin origin = SKEncodedOrigin.TopLeft)
    {
        if (origin is < SKEncodedOrigin.TopLeft or > SKEncodedOrigin.LeftBottom)
        {
            origin = SKEncodedOrigin.TopLeft; // valeur inconnue : image telle quelle
        }

        bool turned = Transposes(origin);
        float width = turned ? source.Height : source.Width, height = turned ? source.Width : source.Height;
        float scale = Math.Max((float)GridGeometry.Width / width, (float)GridGeometry.Height / height);
        float w = width * scale, h = height * scale;
        float left = (GridGeometry.Width - w) / 2f, top = (GridGeometry.Height - h) / 2f;

        if (origin == SKEncodedOrigin.TopLeft)
        {
            canvas.DrawImage(source, new SKRect(0, 0, source.Width, source.Height), SKRect.Create(left, top, w, h), Sampling, null);
            return;
        }

        canvas.Save();
        canvas.Translate(left, top);
        canvas.Scale(scale);
        var matrix = OriginMatrix(origin, source.Width, source.Height);
        canvas.Concat(in matrix);
        canvas.DrawImage(source, SKRect.Create(0, 0, source.Width, source.Height), Sampling);
        canvas.Restore();
    }

    /// <summary>
    /// Comme <see cref="DrawCover(SKCanvas, SKImage, SKEncodedOrigin)"/> pour la partie <paramref name="area"/> de
    /// <paramref name="source"/> seulement (le reste est du bourrage de décodeur) : le recadrage d'une image qui est déjà sur
    /// la carte, qu'on ne peut pas tailler sans la copier.
    /// </summary>
    public static void DrawCover(SKCanvas canvas, SKImage source, SKRectI area)
    {
        float scale = Math.Max((float)GridGeometry.Width / area.Width, (float)GridGeometry.Height / area.Height);
        float w = area.Width * scale, h = area.Height * scale;
        float left = (GridGeometry.Width - w) / 2f, top = (GridGeometry.Height - h) / 2f;
        canvas.DrawImage(source, SKRect.Create(area.Left, area.Top, area.Width, area.Height), SKRect.Create(left, top, w, h), Sampling, null);
    }

    /// <summary>Vrai si l'orientation échange la largeur et la hauteur (rotation d'un quart de tour, ou transposition).</summary>
    private static bool Transposes(SKEncodedOrigin origin) => origin >= SKEncodedOrigin.LeftTop;

    /// <summary>
    /// Matrice qui redresse une image de <paramref name="w"/> × <paramref name="h"/> pixels stockée avec cette
    /// orientation EXIF (celle de <c>SkEncodedOriginToMatrix</c> de Skia).
    /// </summary>
    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, float w, float h)
    {
        static SKMatrix Make(float scaleX, float skewX, float transX, float skewY, float scaleY, float transY) =>
            new(scaleX, skewX, transX, skewY, scaleY, transY, 0, 0, 1);

        return origin switch
        {
            SKEncodedOrigin.TopRight => Make(-1, 0, w, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Make(-1, 0, w, 0, -1, h),
            SKEncodedOrigin.BottomLeft => Make(1, 0, 0, 0, -1, h),
            SKEncodedOrigin.LeftTop => Make(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Make(0, -1, h, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Make(0, -1, h, -1, 0, w),
            SKEncodedOrigin.LeftBottom => Make(0, 1, 0, -1, 0, w),
            _ => SKMatrix.Identity,
        };
    }

    /// <summary>
    /// Ouvre le fichier. Rend null, après avoir averti, s'il manque, s'il n'est pas une image ou si un côté dépasse
    /// <see cref="MaxSide"/>.
    /// </summary>
    public static SKCodec? OpenCodec(string path, Action<string>? warn)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            warn?.Invoke(Missing(path));
            return null;
        }

        SKCodec? codec = null;
        FileStream? file = null;
        try
        {
            // Ouvert par .NET avec FileShare.Delete : sous Windows, le fichier peut être remplacé ou supprimé pendant la
            // lecture (l'ouverture native de Skia l'interdirait). Le codec lit le flux au fil de l'eau, sans le charger.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            codec = SKCodec.Create(new SKManagedStream(file, disposeManagedStream: true));
            if (codec is not null)
            {
                file = null; // le flux appartient maintenant au codec
            }
            if (codec is not null && codec.Info.Width > 0 && codec.Info.Height > 0 &&
                codec.Info.Width <= MaxSide && codec.Info.Height <= MaxSide)
            {
                return codec;
            }
        }
        catch (Exception)
        {
            // Fichier verrouillé, chemin invalide, échec d'allocation : traité comme illisible.
        }

        codec?.Dispose();
        file?.Dispose();
        warn?.Invoke(Unreadable(path));
        return null;
    }

    /// <summary>
    /// Décode la première image à la plus petite échelle qui couvre encore l'écran (1/8, 1/4, 1/2 ou 1 ; les formats
    /// sans décodage réduit, comme PNG, sont décodés en taille réelle), puis la recadre en 1920 × 462.
    /// </summary>
    public static SKImage? DecodeCover(SKCodec codec, out SKSizeI decodedSize)
    {
        decodedSize = codec.Info.Size;
        var origin = codec.EncodedOrigin; // l'orientation EXIF des photos de téléphone
        bool turned = Transposes(origin);
        foreach (var fraction in new[] { 1f / 8, 1f / 4, 1f / 2 })
        {
            var scaled = codec.GetScaledDimensions(fraction);
            int shownWidth = turned ? scaled.Height : scaled.Width, shownHeight = turned ? scaled.Width : scaled.Height;
            if (shownWidth >= GridGeometry.Width && shownHeight >= GridGeometry.Height)
            {
                decodedSize = scaled;
                break;
            }
        }

        using var decoded = new SKBitmap(new SKImageInfo(decodedSize.Width, decodedSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) return null;
        decoded.SetImmutable();

        using var source = SKImage.FromBitmap(decoded);
        using var screen = new SKBitmap(ScreenInfo);
        using (var canvas = new SKCanvas(screen))
        {
            canvas.Clear(SKColors.Black); // un fond transparent (PNG, GIF) ne doit pas laisser voir à travers l'écran
            DrawCover(canvas, source, origin);
        }
        return Freeze(screen);
    }
}
