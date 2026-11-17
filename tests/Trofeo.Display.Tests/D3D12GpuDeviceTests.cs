using SkiaSharp;
using Vortice.Direct3D12;
using Trofeo.Core.Rendering;
using Trofeo.Display.Gpu;
using Trofeo.TestDoubles;

namespace Trofeo.Display.Tests;

/// <summary>L'appareil Direct3D 12 pour Skia, essayé sur WARP (carte logicielle de Windows).</summary>
[Collection(GpuTestSupport.CollectionName)]
public class D3D12GpuDeviceTests
{
    [WarpFact]
    public void Warp_DrawsAndReadsBack()
    {
        using var device = GpuTestSupport.Warp();
        var info = new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var surface = device.CreateSurface(info);
        surface.Canvas.Clear(SKColors.Red);
        device.Submit();

        using var bitmap = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0));
        foreach (var (x, y) in new[] { (0, 0), (63, 0), (32, 16), (0, 31), (63, 31) })
            Assert.Equal(SKColors.Red, bitmap.GetPixel(x, y));
        Assert.False(device.IsLost);
        Assert.Null(device.LostReason);
        Assert.False(string.IsNullOrWhiteSpace(device.AdapterName));
    }

    [WarpFact]
    public void Warp_RunsTheGlassProgram()
    {
        // Le programme du verre, lu comme le fait LiquidGlassEffect (sections « common » et « glass »).
        var sections = LiquidGlassEffect.ReadSections();
        using var effect = SKRuntimeEffect.CreateShader(sections["common"] + "\n" + sections["glass"], out var errors);
        Assert.True(effect is not null, errors);
        var info = new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var device = GpuTestSupport.Warp();
        using var bitmap = RenderGlass(effect!, info, device);

        Assert.NotEqual(SKColors.Black, bitmap.GetPixel(32, 16)); // le verre a laissé voir le fond
        Assert.Equal(SKColors.Black, bitmap.GetPixel(1, 1));      // hors du verre, le fond noir reste
        Assert.False(device.IsLost);
    }

    [WarpFact]
    public void Warp_GlassIsCloseToTheProcessorRendering()
    {
        // Même scène par la carte (WARP) et par le processeur : l'écart doit rester petit (voir ImageDiff).
        var sections = LiquidGlassEffect.ReadSections();
        using var effect = SKRuntimeEffect.CreateShader(sections["common"] + "\n" + sections["glass"], out var errors);
        Assert.True(effect is not null, errors);
        var info = new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var device = GpuTestSupport.Warp();
        using var gpuBitmap = RenderGlass(effect!, info, device);
        using var cpuBitmap = RenderGlass(effect!, info, null);

        var (mean, share, max) = ImageDiff.Compare(cpuBitmap, gpuBitmap);
        Assert.True(mean < 0.5 && share >= 0.999, $"Écart moyen {mean:F2}/255, {share:P2} des pixels à 4/255 au plus, écart maximal {max}/255.");
    }

    private static SKBitmap RenderGlass(SKRuntimeEffect effect, SKImageInfo info, D3D12GpuDevice? device)
    {
        SKSurface Make() => device is null ? SKSurface.Create(info)! : device.CreateSurface(info);
        using var sceneSurface = Make();
        using (var gradient = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(64, 0), [SKColors.DarkBlue, SKColors.Orange], SKShaderTileMode.Clamp),
        })
        {
            sceneSurface.Canvas.DrawPaint(gradient);
        }
        using var scene = sceneSurface.Snapshot();

        var shape = new SKRect(8, 6, 56, 26);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        using var sharp = scene.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var blurred = scene.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var uniforms = new SKRuntimeEffectUniforms(effect);
        uniforms["u_origin"] = new[] { shape.Left, shape.Top };
        uniforms["u_size"] = new[] { shape.Width, shape.Height };
        uniforms["u_radius"] = 6f;
        uniforms["u_refract"] = 0.69f;
        uniforms["u_chroma"] = 0.05f;
        uniforms["u_edgeHL"] = 0.3f;
        uniforms["u_spec"] = 0.4f;
        uniforms["u_fresnel"] = 1f;
        uniforms["u_distort"] = 0f;
        uniforms["u_alpha"] = 1f;
        uniforms["u_sat"] = 0.2f;
        uniforms["u_tint"] = 0f;
        uniforms["u_zRadius"] = 10f;
        uniforms["u_brightness"] = 0.05f;
        uniforms["u_bevelMode"] = 0f;
        using var children = new SKRuntimeEffectChildren(effect);
        children["u_sharp"] = sharp;
        children["u_blurred"] = blurred;
        using var shader = effect.ToShader(uniforms, children, SKMatrix.CreateTranslation(shape.Left, shape.Top));

        using var target = Make();
        target.Canvas.Clear(SKColors.Black);
        using (var paint = new SKPaint { Shader = shader })
        {
            target.Canvas.DrawRect(shape, paint);
        }
        device?.Submit();

        var bitmap = new SKBitmap(info);
        if (!target.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
        {
            bitmap.Dispose();
            throw new InvalidOperationException("Impossible de relire l'image du verre.");
        }
        return bitmap;
    }

    [WarpFact]
    public void Dispose_FreesTheCache()
    {
        var device = GpuTestSupport.Warp();
        var info = new SKImageInfo(256, 128, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var surface = device.CreateSurface(info))
        {
            surface.Canvas.Clear(SKColors.Blue);
            device.Submit();
        }
        Assert.True(device.CacheResources > 0);
        Assert.True(device.CacheBytes > 0);

        device.PurgeUnused();
        Assert.Equal(0, device.CacheResources);

        device.Dispose();
        device.Dispose(); // un second appel ne fait rien
        Assert.Throws<ObjectDisposedException>(() => device.CreateSurface(info));
    }

    [WarpFact]
    public void Warp_AbandonedContext_IsLost()
    {
        var device = GpuTestSupport.Warp();
        var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);

        device.Context.AbandonContext();

        Assert.True(device.IsLost);
        Assert.NotNull(device.LostReason);
        Assert.Contains("abandonné", device.LostReason);
        Assert.Throws<GpuLostException>(() => device.CreateSurface(info));
        Assert.Throws<GpuLostException>(device.Submit);
        device.PurgeUnused(); // sur une carte perdue, ne fait rien
        Assert.Null(Record.Exception(device.Dispose)); // prend la branche « abandonné » sans rien lever
        Assert.Null(Record.Exception(device.Dispose));
    }

    [WarpFact]
    public void Warp_RemovedDevice_IsLost()
    {
        var device = GpuTestSupport.Warp();
        var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var device5 = device.Device.QueryInterface<ID3D12Device5>())
        {
            device5.RemoveDevice(); // simule le retrait de la carte ou la panne du pilote
        }

        Assert.True(device.IsLost);
        Assert.NotNull(device.LostReason);
        Assert.Contains("retirée", device.LostReason);
        Assert.Throws<GpuLostException>(() => device.CreateSurface(info));
        Assert.Null(Record.Exception(device.Dispose));
    }

    [Fact]
    public void List_HasNoSoftwareAdapter()
    {
        var adapters = GpuAdapters.List();

        Assert.DoesNotContain(adapters, a => a.Id.Name.Contains("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase));
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Id.Name)));
    }

    [Fact]
    public void List_KeepsOnlyAdaptersWithDirect3D12()
    {
        // Chaque carte listée sait ouvrir un appareil Direct3D 12 au niveau 11_0 (vérifié ici sans passer par GpuAdapters.Describe).
        var adapters = GpuAdapters.List();

        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory6>();
        for (uint i = 0; factory.EnumAdapterByGpuPreference(i, Vortice.DXGI.GpuPreference.MinimumPower, out Vortice.DXGI.IDXGIAdapter1? adapter).Success; i++)
        {
            using (adapter)
            {
                var d = adapter!.Description1;
                var luid = ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
                var listed = adapters.Any(a => a.Luid == luid);
                var hardware = (d.Flags & Vortice.DXGI.AdapterFlags.Software) == 0;
                var supports = Vortice.Direct3D12.D3D12.IsSupported(adapter, Vortice.Direct3D.FeatureLevel.Level_11_0);
                Assert.Equal(hardware && supports, listed);
            }
        }
    }

    [Theory]
    [InlineData(Vortice.DXGI.AdapterFlags.None, true, true)]
    [InlineData(Vortice.DXGI.AdapterFlags.None, false, false)]
    [InlineData(Vortice.DXGI.AdapterFlags.Software, true, false)]
    [InlineData(Vortice.DXGI.AdapterFlags.Software, false, false)]
    public void IsUsable_NeedsHardwareAndDirect3D12(Vortice.DXGI.AdapterFlags flags, bool direct3D12, bool expected) =>
        Assert.Equal(expected, GpuAdapters.IsUsable(flags, direct3D12));

    [Fact]
    public void Create_WithAnUnknownAdapter_ThrowsGpuLost()
    {
        var ghost = new Trofeo.Display.GpuAdapterInfo(new Trofeo.Core.Layout.GpuAdapterId(0x1234, 0x5678, 0, "Carte absente"), 0x7777_7777L, 0);

        var e = Assert.Throws<GpuLostException>(() => D3D12GpuDevice.Create(ghost));
        Assert.Contains("Carte absente", e.Message);
    }
}
