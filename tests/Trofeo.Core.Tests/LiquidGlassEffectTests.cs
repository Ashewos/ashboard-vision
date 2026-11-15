using SkiaSharp;
using Trofeo.Core.Rendering;

namespace Trofeo.Core.Tests;

/// <summary>
/// L'effet de verre de liquidglass adapté en SkSL : chaque étape de <c>FS_GLASS</c> se vérifie sur des images de synthèse
/// (rayures pour la réfraction, uni ou dégradé pour les couleurs et le sens des axes).
/// </summary>
public class LiquidGlassEffectTests
{
    private const int ScreenWidth = 1920;
    private const int ScreenHeight = 462;

    /// <summary>La case des essais : 200 × 120 en (300, 100).</summary>
    private static readonly SKRect Shape = SKRect.Create(300, 100, 200, 120);

    /// <summary>Un verre sans aucun effet : il ne reste que le fond, le masque et le <c>×(1 + 0,06 × depth)</c>.</summary>
    private static readonly GlassSettings Neutral = new GlassSettings() with
    {
        Blur = 0f,
        Refraction = 0f,
        ChromaticAberration = 0f,
        EdgeHighlight = 0f,
        Specular = 0f,
        Fresnel = 0f,
        Distortion = 0f,
        Brightness = 0f,
        Saturation = 0f,
        Tint = 0f,
        Opacity = 1f,
    };

    private static SKImage Scene(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(ScreenWidth, ScreenHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap)) draw(canvas);
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    private static SKImage Solid(SKColor color) => Scene(canvas => canvas.Clear(color));

    /// <summary>Rayures verticales noir et blanc de 8 px, décalées de 4 px : le centre de la case tombe au milieu d'une rayure.</summary>
    private static SKImage Stripes() => Scene(canvas =>
    {
        canvas.Clear(SKColors.Black);
        using var white = new SKPaint { Color = SKColors.White };
        for (var x = 4; x < ScreenWidth; x += 16) canvas.DrawRect(x, 0, 8, ScreenHeight, white);
    });

    /// <summary>Dégradé de gris : chaque pixel vaut sa colonne (ou sa ligne) dans la case, de 20 à 220.</summary>
    private static SKImage Ramp(bool vertical) => Scene(canvas =>
    {
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint();
        var length = vertical ? ScreenHeight : ScreenWidth;
        for (var i = 0; i < length; i++)
        {
            var start = vertical ? Shape.Top : Shape.Left;
            var size = vertical ? Shape.Height : Shape.Width;
            var level = (byte)Math.Clamp(20f + 200f * (i - start) / size, 0f, 255f);
            paint.Color = new SKColor(level, level, level);
            if (vertical) canvas.DrawRect(0, i, ScreenWidth, 1, paint);
            else canvas.DrawRect(i, 0, 1, ScreenHeight, paint);
        }
    });

    /// <summary>Rend le verre sans flou séparé (la scène nette sert aussi de scène floutée) et le lit en pixels.</summary>
    private static SKBitmap Glass(SKImage scene, GlassSettings settings, SKRect? shape = null)
    {
        using var effect = new LiquidGlassEffect();
        using var image = effect.RenderGlass(scene, scene, SKPoint.Empty, shape ?? Shape, settings);
        return SKBitmap.FromImage(image);
    }

    /// <summary>La distance signée au rectangle arrondi, comme <c>rrSDF</c>, pour le pixel (x, y) d'une image de cette taille.</summary>
    private static float Sdf(int x, int y, float width, float height, float radius)
    {
        var r = MathF.Min(radius, MathF.Min(width, height) / 2f);
        var qx = MathF.Abs(x + 0.5f - width / 2f) - width / 2f + r;
        var qy = MathF.Abs(y + 0.5f - height / 2f) - height / 2f + r;
        var outside = MathF.Sqrt(MathF.Pow(MathF.Max(qx, 0f), 2) + MathF.Pow(MathF.Max(qy, 0f), 2));
        return MathF.Min(MathF.Max(qx, qy), 0f) + outside - r;
    }

    [Fact]
    public void Compiles()
    {
        using var effect = new LiquidGlassEffect();
    }

    [Fact]
    public void NoEffect_CenterPixel_IsTheBackground()
    {
        using var scene = Solid(new SKColor(100, 120, 140));
        using var glass = Glass(scene, Neutral);

        // Au centre, inside (60) dépasse zRadius (40) : depth vaut 1, donc le fond est multiplié par 1,06.
        var center = glass.GetPixel(100, 60);
        Assert.InRange(center.Red, 104, 108);
        Assert.InRange(center.Green, 125, 129);
        Assert.InRange(center.Blue, 146, 150);
        Assert.Equal(255, center.Alpha);
    }

    [Fact]
    public void Refraction_BendsNearEdges_NotAtCenter()
    {
        using var scene = Stripes();
        using var flat = Glass(scene, Neutral);
        using var bent = Glass(scene, Neutral with { Refraction = 0.69f });

        var differing = 0;
        for (var y = 40; y < 80; y++)
            if (Math.Abs(flat.GetPixel(6, y).Red - bent.GetPixel(6, y).Red) > 40) differing++;
        Assert.True(differing >= 30, $"à 6 px du bord gauche, {differing} pixels sur 40 changent");

        var a = flat.GetPixel(100, 60);
        var b = bent.GetPixel(100, 60);
        Assert.InRange(b.Red - a.Red, -2, 2);
        Assert.InRange(b.Green - a.Green, -2, 2);
        Assert.InRange(b.Blue - a.Blue, -2, 2);
    }

    [Fact]
    public void Refraction_SamplesTowardTheCenter_OnBothAxes()
    {
        // Une lentille convexe ramène vers le centre : près d'un bord, on voit ce qui se trouve plus à l'intérieur.
        using var horizontal = Ramp(vertical: false);
        using var flatH = Glass(horizontal, Neutral);
        using var bentH = Glass(horizontal, Neutral with { Refraction = 0.69f });
        Assert.True(bentH.GetPixel(8, 60).Red > flatH.GetPixel(8, 60).Red + 10, "bord gauche : plus clair (vient de la droite)");
        Assert.True(bentH.GetPixel(191, 60).Red < flatH.GetPixel(191, 60).Red - 10, "bord droit : plus sombre (vient de la gauche)");

        using var vertical = Ramp(vertical: true);
        using var flatV = Glass(vertical, Neutral);
        using var bentV = Glass(vertical, Neutral with { Refraction = 0.69f });
        Assert.True(bentV.GetPixel(100, 8).Red > flatV.GetPixel(100, 8).Red + 10, "bord haut : plus clair (vient du bas)");
        Assert.True(bentV.GetPixel(100, 111).Red < flatV.GetPixel(100, 111).Red - 10, "bord bas : plus sombre (vient du haut)");
    }

    [Fact]
    public void TopStroke_IsBrighterThanBottom()
    {
        // Un gris sombre : avec le reflet du bord et la lueur (0,22 + 0,15), un gris moyen saturerait à 255 en haut.
        using var scene = Solid(new SKColor(20, 20, 20));
        using var glass = Glass(scene, Neutral with { EdgeHighlight = 1f });

        // Le trait intérieur est à son maximum à 1,5 px du bord : lignes 1 et 118.
        var top = glass.GetPixel(100, 1);
        var bottom = glass.GetPixel(100, 118);
        var center = glass.GetPixel(100, 60);
        Assert.True(top.Red > bottom.Red + 40, $"haut {top.Red}, bas {bottom.Red}");
        Assert.True(bottom.Red > center.Red + 20, $"bas {bottom.Red}, centre {center.Red}");
    }

    [Fact]
    public void Specular_LightsTheRim_NoLineInsideAWidePanel()
    {
        // Un panneau large, comme une case de module : avec la normale du biseau, la lumière du bas de FS_GLASS dessinait
        // une ligne droite à environ 18 px du bord bas (0,45 × zRadius), qui se lisait comme une seconde pastille.
        var panel = SKRect.Create(100, 100, 800, 200);
        using var scene = Solid(new SKColor(40, 40, 40));
        using var matte = Glass(scene, Neutral, panel);
        using var shiny = Glass(scene, Neutral with { Specular = 1f }, panel);

        int Gain(int y) => shiny.GetPixel(400, y).Red - matte.GetPixel(400, y).Red;
        var center = Gain(100);
        for (var inset = 6; inset <= 40; inset++)
        {
            var gain = Gain(199 - inset);
            Assert.True(gain <= center + 15, $"à {inset} px du bord bas, le reflet ajoute {gain} (centre : {center})");
        }

        // Le reflet est sur le bord lui-même, à 1,5 px.
        Assert.True(Gain(198) > center + 40, $"bord bas {Gain(198)}, centre {center}");
    }

    [Theory]
    [InlineData(800, 200, 8)]
    [InlineData(333, 117, 8)] // une hauteur qui ne se partage pas juste
    [InlineData(200, 120, 3)]
    [InlineData(120, 20, 8)] // une seule bande de 16 lignes au moins : tout sur le fil appelant
    public void Strips_GiveTheSamePixels_AsOneSurface(int width, int height, int strips)
    {
        var shape = SKRect.Create(100, 100, width, height);
        var settings = GlassPresets.Find(GlassPresets.Prisme)!;
        using var scene = Stripes();
        using var single = new LiquidGlassEffect { Strips = 1 };
        using var parallel = new LiquidGlassEffect { Strips = strips };

        using (var one = SKBitmap.FromImage(single.RenderGlass(scene, scene, SKPoint.Empty, shape, settings)))
        using (var many = SKBitmap.FromImage(parallel.RenderGlass(scene, scene, SKPoint.Empty, shape, settings)))
        {
            Assert.Equal(one.Bytes, many.Bytes);
        }

        using var oneShadow = single.RenderShadow(shape, settings with { ShadowOpacity = 0.5f }, out var areaOne)!;
        using var manyShadow = parallel.RenderShadow(shape, settings with { ShadowOpacity = 0.5f }, out var areaMany)!;
        Assert.Equal(areaOne, areaMany);
        using var a = SKBitmap.FromImage(oneShadow);
        using var b = SKBitmap.FromImage(manyShadow);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    [Fact]
    public void DefaultStrips_LeavesOneCore_AtMostEight()
    {
        Assert.Equal(Math.Clamp(Environment.ProcessorCount - 1, 1, 8), LiquidGlassEffect.DefaultStrips);
        using var effect = new LiquidGlassEffect();
        Assert.Equal(LiquidGlassEffect.DefaultStrips, effect.Strips);
    }

    [Fact]
    public void Chroma_SplitsChannelsAtEdges()
    {
        using var stripes = Stripes();
        using var split = Glass(stripes, Neutral with { ChromaticAberration = 0.3f });
        var fringes = 0;
        for (var x = 2; x < 20; x++)
            if (Math.Abs(split.GetPixel(x, 60).Red - split.GetPixel(x, 60).Blue) > 20) fringes++;
        Assert.True(fringes >= 1, "aucune frange près du bord gauche");

        using var gray = Solid(new SKColor(90, 90, 90));
        using var plain = Glass(gray, Neutral);
        for (var y = 0; y < plain.Height; y++)
            for (var x = 0; x < plain.Width; x++)
            {
                var pixel = plain.GetPixel(x, y);
                Assert.True(pixel.Red == pixel.Blue, $"R ≠ B en ({x}, {y})");
            }
    }

    [Fact]
    public void Mask_IsTransparentOutsideAndAntialiased()
    {
        using var scene = Solid(new SKColor(100, 120, 140));
        using var glass = Glass(scene, Neutral with { Opacity = 0.5f });

        Assert.Equal(200, glass.Width);
        Assert.Equal(120, glass.Height);
        Assert.Equal(0, glass.GetPixel(0, 0).Alpha);
        Assert.Equal(0, glass.GetPixel(199, 119).Alpha);
        // L'alpha vaut (masque × opacité)², comme sur le canevas non prémultiplié de la démo : 255 × 0,5².
        Assert.InRange(glass.GetPixel(100, 60).Alpha, 63, 65);

        // Sur la diagonale du coin (rayon 30), l'alpha passe de 0 à 64 par au moins une valeur intermédiaire.
        var partial = 0;
        for (var i = 0; i < 30; i++)
        {
            var alpha = glass.GetPixel(i, i).Alpha;
            if (alpha is > 3 and < 58) partial++;
        }
        Assert.True(partial >= 1, "pas de pixel à demi couvert sur la courbe du coin");
    }

    [Fact]
    public void Output_IsPremultiplied()
    {
        using var scene = Solid(SKColors.White);
        using var effect = new LiquidGlassEffect();
        using var image = effect.RenderGlass(scene, scene, SKPoint.Empty, Shape, Neutral with { Opacity = 0.5f });
        Assert.Equal(SKAlphaType.Premul, image.AlphaType);

        using var bitmap = SKBitmap.FromImage(image);
        var bytes = bitmap.GetPixelSpan();
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var alpha = bytes[i + 3];
            Assert.True(bytes[i] <= alpha && bytes[i + 1] <= alpha && bytes[i + 2] <= alpha, $"couleur au-dessus de l'alpha, octet {i}");
        }
    }

    [Fact]
    public void Edge_FollowsTheDemoCanvas_ColorTimesAlphaCubed()
    {
        // Ligne 0 : sdf = −0,5, donc masque = 0,5. Sur du blanc, la démo montre la couleur c·a à l'alpha a² :
        // en prémultiplié, alpha = 0,25 (64) et couleur = a³ = 0,125 (32).
        using var scene = Solid(SKColors.White);
        using var effect = new LiquidGlassEffect();
        using var image = effect.RenderGlass(scene, scene, SKPoint.Empty, Shape, Neutral);
        using var bitmap = SKBitmap.FromImage(image);
        var bytes = bitmap.GetPixelSpan();
        var edge = 100 * 4;
        Assert.InRange(bytes[edge + 3], 62, 66);
        Assert.InRange(bytes[edge], 30, 34);
        Assert.InRange(bytes[edge + 1], 30, 34);
        Assert.InRange(bytes[edge + 2], 30, 34);

        // Avec une opacité de 0,5 au centre : alpha 0,25, couleur 1 × 0,5³.
        using var half = effect.RenderGlass(scene, scene, SKPoint.Empty, Shape, Neutral with { Opacity = 0.5f });
        using var halfBitmap = SKBitmap.FromImage(half);
        var center = (60 * 200 + 100) * 4;
        var halfBytes = halfBitmap.GetPixelSpan();
        Assert.InRange(halfBytes[center + 3], 62, 66);
        Assert.InRange(halfBytes[center], 30, 34);
    }

    [Fact]
    public void ImageOrigin_PlacesTheScene()
    {
        // La même scène, donnée en un morceau qui commence en (280, 90) : le résultat ne change pas.
        using var full = Stripes();
        using var crop = full.Subset(SKRectI.Create(280, 90, 240, 140));
        using var effect = new LiquidGlassEffect();
        var settings = Neutral with { Refraction = 0.3f };
        using var fromFull = effect.RenderGlass(full, full, SKPoint.Empty, Shape, settings);
        using var fromCrop = effect.RenderGlass(crop, crop, new SKPoint(280, 90), Shape, settings);
        using var a = SKBitmap.FromImage(fromFull);
        using var b = SKBitmap.FromImage(fromCrop);
        for (var y = 0; y < a.Height; y += 3)
            for (var x = 0; x < a.Width; x += 3)
                Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
    }

    [Fact]
    public void Blurred_FillsTheCenter_SharpStaysAtTheRim()
    {
        // edgeMix = 1 − edge × 0,15 : au centre on ne voit que la scène floutée, au bord il reste 15 % de la scène nette.
        using var sharp = Solid(SKColors.Black);
        using var blurred = Solid(new SKColor(200, 200, 200));
        using var effect = new LiquidGlassEffect();
        using var image = effect.RenderGlass(sharp, blurred, SKPoint.Empty, Shape, Neutral);
        using var glass = SKBitmap.FromImage(image);

        Assert.InRange(glass.GetPixel(100, 60).Red, 210, 214);
        // Ligne 3 : inside = 3,5, edge ≈ 0,93, depth ≈ 0,02 → 200 × (1 − 0,93 × 0,15) ≈ 172.
        Assert.InRange(glass.GetPixel(100, 3).Red, 168, 178);
    }

    [Fact]
    public void Dome_DiffersFromPill()
    {
        using var scene = Stripes();
        using var pill = Glass(scene, Neutral with { Refraction = 0.69f, Bevel = GlassBevel.Pill });
        using var dome = Glass(scene, Neutral with { Refraction = 0.69f, Bevel = GlassBevel.Dome });

        var differing = 0;
        for (var y = 0; y < pill.Height; y++)
            for (var x = 0; x < pill.Width; x++)
                if (Math.Abs(pill.GetPixel(x, y).Red - dome.GetPixel(x, y).Red) > 40) differing++;
        Assert.True(differing > 500, $"{differing} pixels diffèrent entre la pastille et le dôme");
    }

    [Fact]
    public void ExtremeValues_NoNaN_NoPixelOutside()
    {
        // Point de vigilance 3 : une case 1 × 1 (108 × 99), tous les réglages au bout de leurs bornes.
        var shape = SKRect.Create(12, 12, 108, 99);
        var extreme = new GlassSettings() with
        {
            CornerRadius = 49f,
            ZRadius = 100f,
            Bevel = GlassBevel.Dome,
            Blur = 1f,
            Refraction = 2f,
            ChromaticAberration = 0.3f,
            Distortion = 1f,
            EdgeHighlight = 1f,
            Specular = 1f,
            Fresnel = 2f,
            Saturation = 1f,
            Tint = 1f,
            Brightness = 0.5f,
        };
        using var scene = Solid(SKColors.White);

        foreach (var settings in new[] { extreme, extreme with { Bevel = GlassBevel.Pill }, extreme with { Saturation = -1f, Brightness = -0.5f, ZRadius = 1f } })
        {
            using var glass = Glass(scene, settings, shape);
            Assert.Equal(108, glass.Width);
            Assert.Equal(99, glass.Height);
            for (var y = 0; y < glass.Height; y++)
                for (var x = 0; x < glass.Width; x++)
                {
                    var sdf = Sdf(x, y, 108, 99, 49);
                    var pixel = glass.GetPixel(x, y);
                    if (sdf < -2f)
                    {
                        // Un NaN donnerait un pixel vide : sur du blanc, le verre est opaque et jamais noir.
                        Assert.True(pixel.Alpha == 255, $"alpha {pixel.Alpha} en ({x}, {y})");
                        Assert.True(pixel.Red > 0 && pixel.Green > 0 && pixel.Blue > 0, $"pixel noir en ({x}, {y})");
                    }
                    else if (sdf > 1f)
                    {
                        Assert.True(pixel.Alpha == 0, $"pixel hors de la case en ({x}, {y})");
                    }
                }
        }
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(3f, 2f)]
    [InlineData(40.4f, 3.2f)]
    public void TinyShapes_RenderWithoutNaN(float width, float height)
    {
        using var scene = Solid(SKColors.White);
        var settings = new GlassSettings() with { CornerRadius = 49f, ZRadius = 100f, Bevel = GlassBevel.Dome, Refraction = 2f, Fresnel = 2f, EdgeHighlight = 1f };
        foreach (var bevel in new[] { GlassBevel.Pill, GlassBevel.Dome })
        {
            using var glass = Glass(scene, settings with { Bevel = bevel }, SKRect.Create(50.3f, 60.7f, width, height));
            Assert.Equal((int)MathF.Ceiling(width), glass.Width);
            Assert.Equal((int)MathF.Ceiling(height), glass.Height);
            for (var y = 0; y < glass.Height; y++)
                for (var x = 0; x < glass.Width; x++)
                {
                    var pixel = glass.GetPixel(x, y);
                    // Sur du blanc, un pixel d'alpha a² a pour couleur a (ou plus clair) : un NaN le rendrait noir ou vide.
                    if (pixel.Alpha < 16) continue;
                    var expected = 255f * MathF.Sqrt(pixel.Alpha / 255f);
                    Assert.True(pixel.Red > expected * 0.8f - 12f, $"pixel sombre en ({x}, {y}) : {pixel.Red} pour un alpha de {pixel.Alpha}");
                }
        }
    }

    [Fact]
    public void Shadow_NullWhenOpacityZero()
    {
        using var effect = new LiquidGlassEffect();
        using var shadow = effect.RenderShadow(Shape, new GlassSettings() with { ShadowOpacity = 0f }, out _);
        Assert.Null(shadow);
    }

    [Fact]
    public void Shadow_IsOutsideOnly_AndOffsetDown()
    {
        var settings = new GlassSettings() with { ShadowOpacity = 1f, ShadowSpread = 10f, ShadowOffsetY = 10f };
        using var effect = new LiquidGlassEffect();
        using var image = effect.RenderShadow(Shape, settings, out var area);
        Assert.NotNull(image);
        using var shadow = SKBitmap.FromImage(image);

        // La zone contient la case élargie de la marge, et l'image a sa taille.
        var margin = LiquidGlassEffect.ShadowMargin(settings);
        Assert.True(margin >= 3 * 10 + 10 + 2);
        Assert.True(area.Left <= Shape.Left - margin && area.Top <= Shape.Top - margin);
        Assert.True(area.Right >= Shape.Right + margin && area.Bottom >= Shape.Bottom + margin);
        Assert.Equal((int)area.Width, shadow.Width);
        Assert.Equal((int)area.Height, shadow.Height);

        SKColor At(float screenX, float screenY) => shadow.GetPixel((int)(screenX - area.Left), (int)(screenY - area.Top));

        // Vide dans le rectangle arrondi : au centre, et juste à l'intérieur de chaque bord.
        Assert.Equal(0, At(400, 160).Alpha);
        Assert.Equal(0, At(400, 101).Alpha);
        Assert.Equal(0, At(400, 218).Alpha);
        Assert.Equal(0, At(301, 160).Alpha);
        Assert.Equal(0, At(498, 160).Alpha);

        // Noire, et plus forte sous le bas que sur le haut (décalage de 10 px vers le bas).
        var below = At(400, 224);
        var above = At(400, 95);
        Assert.True(below.Alpha > above.Alpha + 40, $"dessous {below.Alpha}, dessus {above.Alpha}");
        Assert.Equal(0, below.Red + below.Green + below.Blue);

        // Rien de visible n'est coupé au bord de la zone.
        for (var x = 0; x < shadow.Width; x++)
        {
            Assert.True(shadow.GetPixel(x, 0).Alpha <= 1);
            Assert.True(shadow.GetPixel(x, shadow.Height - 1).Alpha <= 1);
        }
        for (var y = 0; y < shadow.Height; y++)
        {
            Assert.True(shadow.GetPixel(0, y).Alpha <= 1);
            Assert.True(shadow.GetPixel(shadow.Width - 1, y).Alpha <= 1);
        }
    }

    [Fact]
    public void Shadow_AlphaIsSquared_LikeTheDemoCanvas()
    {
        // Au contact (d = 0), FS_GLASS sort (0,65 + 0,35) × opacité = 0,3 : la démo en montre le carré, 0,09 (23 sur 255).
        var settings = new GlassSettings() with { ShadowOpacity = 0.3f, ShadowSpread = 10f, ShadowOffsetY = 0f };
        using var effect = new LiquidGlassEffect();
        using var image = effect.RenderShadow(Shape, settings, out var area);
        Assert.NotNull(image);
        using var shadow = SKBitmap.FromImage(image);

        var contact = shadow.GetPixel((int)(400 - area.Left), (int)(220 - area.Top));
        Assert.InRange(contact.Alpha, 21, 25);

        // À 10,5 px du bord (d = 9,5) : (0,65 × e^−0,9025 + 0,35 × e^−1,9) × 0,3 = 0,0948, au carré 0,0090 (2 sur 255).
        var far = shadow.GetPixel((int)(400 - area.Left), (int)(230 - area.Top));
        Assert.InRange(far.Alpha, 1, 4);
    }

    [Fact]
    public void Shadow_ExtremeValues_StayInsideTheArea()
    {
        using var effect = new LiquidGlassEffect();
        foreach (var (spread, offset) in new[] { (0f, -20f), (40f, 20f), (40f, -20f), (0f, 0f) })
        {
            var settings = new GlassSettings() with { ShadowOpacity = 1f, ShadowSpread = spread, ShadowOffsetY = offset, CornerRadius = 49f };
            using var image = effect.RenderShadow(SKRect.Create(500, 200, 108, 99), settings, out var area);
            Assert.NotNull(image);
            using var shadow = SKBitmap.FromImage(image);
            Assert.Equal((int)area.Width, shadow.Width);
            for (var x = 0; x < shadow.Width; x++)
            {
                Assert.True(shadow.GetPixel(x, 0).Alpha <= 1, $"étendue {spread}, décalage {offset} : coupée en haut");
                Assert.True(shadow.GetPixel(x, shadow.Height - 1).Alpha <= 1, $"étendue {spread}, décalage {offset} : coupée en bas");
            }
        }
    }

    [Fact]
    public void BlurSigma_MatchesTheDemo_FilteringIncluded()
    {
        // σ des 6 passes de la démo, filtrage linéaire compris (calculé à part) : il pèse surtout pour les petits réglages.
        Assert.Equal(0f, LiquidGlassEffect.BlurSigma(0f));
        Assert.Equal(0.447f, LiquidGlassEffect.BlurSigma(0.01f), 2);
        Assert.Equal(1.412f, LiquidGlassEffect.BlurSigma(0.1f), 2);
        Assert.Equal(1.830f, LiquidGlassEffect.BlurSigma(0.15f), 2);
        Assert.Equal(6.267f, LiquidGlassEffect.BlurSigma(0.6f), 2);
        Assert.Equal(10.381f, LiquidGlassEffect.BlurSigma(1f), 2);
        Assert.Equal(0f, LiquidGlassEffect.BlurSigma(float.NaN));
        Assert.Equal(0f, LiquidGlassEffect.BlurSigma(-1f));

        var previous = 0f;
        for (var i = 1; i <= 100; i++)
        {
            var sigma = LiquidGlassEffect.BlurSigma(i / 100f);
            Assert.True(sigma > previous, $"le flou baisse à {i / 100f}");
            previous = sigma;
        }
    }

    [Fact]
    public void Sksl_CarriesTheMitNotice()
    {
        using var stream = typeof(LiquidGlassEffect).Assembly.GetManifestResourceStream("Trofeo.Core.Rendering.LiquidGlass.liquid-glass.sksl");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        Assert.Contains("Copyright (c) ybouane", source);
        Assert.Contains("https://github.com/ybouane/liquidglass", source);
        Assert.Contains("59af227103795f06421a96e2c7d3dceb081decd2", source);
    }
}
