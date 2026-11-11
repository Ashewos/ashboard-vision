using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;

namespace Trofeo.Extensions.Tests;

/// <summary>Les charges utiles JSON que le service et l'hôte de module s'échangent : ce qui sort d'un côté entre intact de l'autre.</summary>
public sealed class HostPayloadsTests
{
    [Fact]
    public void NowPlaying_RoundTrip_KeepsEveryField()
    {
        var info = new NowPlayingInfo("Titre", "Artiste", "Lecteur", TimeSpan.FromSeconds(83.5), TimeSpan.FromMinutes(3.25), true, [1, 2, 3, 255, 0]);

        var back = HostPayloads.ReadNowPlaying(HostPayloads.NowPlayingToJson(info));

        Assert.NotNull(back);
        Assert.Equal(info with { CoverPng = null }, back with { CoverPng = null });
        Assert.Equal(info.CoverPng, back.CoverPng);
    }

    [Fact]
    public void NowPlaying_WithoutCover_RoundTrips()
    {
        var info = new NowPlayingInfo("T", "A", "App", TimeSpan.Zero, TimeSpan.Zero, false, null);

        Assert.Equal(info, HostPayloads.ReadNowPlaying(HostPayloads.NowPlayingToJson(info)));
    }

    [Fact]
    public void Sensors_RoundTrip_KeepsNamesAndValues()
    {
        var reading = new SensorReading("Ryzen", null, new Dictionary<string, double> { ["cpu.load"] = 42.5, ["gpu.temp"] = 61 });

        var back = HostPayloads.ReadSensors(HostPayloads.SensorsToJson(reading));

        Assert.Equal("Ryzen", back!.CpuName);
        Assert.Null(back.GpuName);
        Assert.Equal(reading.Values, back.Values);
    }

    [Fact]
    public void Appearance_RoundTrip_KeepsTheChoices()
    {
        var appearance = new AppearanceSettings("pixel", 0.5f, "VT323", new BackgroundSettings(BackgroundKind.Color, "#102030"), false);

        Assert.Equal(appearance, HostPayloads.ReadAppearance(HostPayloads.AppearanceToJson(appearance)));
    }

    [Fact]
    public void NullPayloads_GiveDefaults()
    {
        var nothing = JsonSerializer.SerializeToElement<object?>(null);

        Assert.Null(HostPayloads.ReadSensors(nothing));
        Assert.Null(HostPayloads.ReadNowPlaying(nothing));
        Assert.Equal(AppearanceSettings.Default, HostPayloads.ReadAppearance(nothing));
    }
}
