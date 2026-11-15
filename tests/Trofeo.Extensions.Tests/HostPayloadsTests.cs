using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.Core.Layout;
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
    public void Appearance_FromADamagedLayout_IsSentToTheHostsWithoutThrowing()
    {
        // Relecture finale du plan 9 (décision R11) : un layout.json abîmé ne doit plus empêcher le service de démarrer.
        // La lecture borne le verre et écarte une opacité non finie : ce qui part vers les hôtes s'écrit toujours en JSON.
        const string Views = """[{"Id":"7e4b2a91-c3d5-4f08-a6b1-92d0e5f13c48","Name":"Vue 1","Placements":[]}]""";
        string[] appearances =
        [
            """{"Theme":"liquid-glass-avance","Glass":{"Preset":"personnalise","Values":{"Blur":"NaN"}}}""",
            """{"Theme":"liquid-glass-avance","Glass":{"Preset":"personnalise","Values":{"Blur":"Infinity","Tint":"-Infinity"}}}""",
            """{"Theme":"liquid-glass-avance","Glass":{"Preset":"personnalise","Values":{"Refraction":9,"ZRadius":1e999}}}""",
            """{"Theme":"liquid-glass-avance","PanelOpacity":1e999,"Glass":{"Preset":"clair","Values":{"Blur":"NaN"}}}""",
            """{"Theme":"pixel","PanelOpacity":"NaN"}""",
        ];

        foreach (var appearance in appearances)
        {
            var json = "{\"Format\":\"" + LayoutTransfer.Format + "\",\"Layout\":{\"Rotation\":180,\"Views\":" + Views + ",\"Appearance\":" + appearance + "}}";

            var result = LayoutTransfer.Import(json);

            Assert.Null(result.Error);
            Assert.NotEmpty(result.Warnings);
            var read = result.Layout!.Appearance!;
            Assert.Equal(appearance.Contains("pixel") ? "pixel" : "liquid-glass-avance", read.Theme); // le reste est gardé
            Assert.True(read.PanelOpacity is null || float.IsFinite(read.PanelOpacity.Value));
            if (read.Glass?.Values is { } values) Assert.Equal(values, values.Clamped(new GlassSettings()));

            var sent = HostPayloads.AppearanceToJson(read); // levait ArgumentException
            Assert.Equal(read, HostPayloads.ReadAppearance(sent));
        }
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
