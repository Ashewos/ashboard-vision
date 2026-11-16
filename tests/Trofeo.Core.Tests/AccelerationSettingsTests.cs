using Trofeo.Core.Layout;

namespace Trofeo.Core.Tests;

/// <summary>Les réglages d'accélération matérielle de la disposition : lecture indulgente, aller-retour, et jamais partagés.</summary>
public sealed class AccelerationSettingsTests : IDisposable
{
    private const string Views = "\"Views\":[{\"Id\":\"7e4b2a91-c3d5-4f08-a6b1-92d0e5f13c48\",\"Name\":\"Vue 1\",\"Placements\":[]}]";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-acceleration-" + Guid.NewGuid().ToString("N"));

    public AccelerationSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static LayoutLoadResult Parse(string accelerationJson, List<string> warnings) =>
        LayoutJson.Parse($$"""{"Rotation":180,{{Views}},"Acceleration":{{accelerationJson}}}""", warnings.Add);

    private static readonly GpuAdapterId Card = new(0x10DE, 0x2F04, 0x12345678, "NVIDIA GeForce RTX 5070");

    [Fact]
    public void Missing_GivesNull()
    {
        var warnings = new List<string>();

        var result = LayoutJson.Parse($$"""{"Rotation":180,{{Views}}}""", warnings.Add);

        Assert.Null(result.Layout!.Acceleration);
        Assert.Empty(warnings);
        Assert.Equal(new AccelerationSettings(true, null, true, true), result.Layout.Acceleration ?? AccelerationSettings.Default);
    }

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var settings = new AccelerationSettings(false, Card, true, false);
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));

        store.Save(DashboardLayout.Default with { Acceleration = settings });

        Assert.Equal(settings, store.Load().Acceleration);
    }

    [Fact]
    public void RoundTrip_AutomaticAdapter_StaysNull()
    {
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));

        store.Save(DashboardLayout.Default with { Acceleration = new AccelerationSettings(true, null, false, true) });

        Assert.Equal(new AccelerationSettings(true, null, false, true), store.Load().Acceleration);
    }

    [Fact]
    public void Unreadable_FieldFallsBackWithWarning()
    {
        var warnings = new List<string>();

        var result = Parse("""{"Enabled":"oui","Adapter":3,"Drawing":false,"VideoDecoding":null}""", warnings);

        Assert.Equal(new AccelerationSettings(true, null, false, true), result.Layout!.Acceleration);
        Assert.Equal(3, warnings.Count);
        Assert.Contains("Réglage d'accélération illisible (Enabled) : valeur par défaut utilisée.", warnings);
        Assert.Contains("Réglage d'accélération illisible (Adapter) : valeur par défaut utilisée.", warnings);
        Assert.Contains("Réglage d'accélération illisible (VideoDecoding) : valeur par défaut utilisée.", warnings);
        Assert.Single(result.Layout.Views); // le reste de la disposition est gardé
    }

    [Fact]
    public void Unreadable_WholeSetting_IsDroppedWithWarning()
    {
        var warnings = new List<string>();

        var result = Parse("12", warnings);

        Assert.Null(result.Layout!.Acceleration);
        Assert.Single(warnings);
    }

    [Fact]
    public void Unreadable_DuplicateKeyFile_StillReadsAcceleration()
    {
        // Un nom de propriété en double : chemin d'avant, lu sans JsonNode ; l'accélération y reste lue avec indulgence.
        var warnings = new List<string>();

        var result = LayoutJson.Parse(
            $$$"""{"Rotation":180,"Rotation":180,{{{Views}}},"Acceleration":{"Enabled":false,"Drawing":"non"}}""", warnings.Add);

        Assert.Equal(new AccelerationSettings(false, null, true, true), result.Layout!.Acceleration);
        Assert.Single(warnings);
    }

    [Fact]
    public void Save_WithoutAcceleration_LeavesItOutOfTheFile()
    {
        var path = Path.Combine(_dir, "layout.json");

        new LayoutStore(path).Save(DashboardLayout.Default);

        Assert.DoesNotContain("Acceleration", File.ReadAllText(path));
    }

    [Fact]
    public void Export_LeavesAccelerationOut()
    {
        var json = LayoutTransfer.Export(DashboardLayout.Default with { Acceleration = new AccelerationSettings(false, Card, true, false) });

        Assert.DoesNotContain("\"Acceleration\"", json);
        Assert.DoesNotContain("RTX 5070", json);
    }

    [Fact]
    public void Import_DropsAcceleration()
    {
        var shared = "{\"Format\":\"" + LayoutTransfer.Format + "\",\"Layout\":{\"Rotation\":180," + Views + ","
            + "\"Acceleration\":{\"Enabled\":false,\"Adapter\":{\"VendorId\":1,\"DeviceId\":2,\"SubSysId\":3,\"Name\":\"Autre\"},\"Drawing\":\"non\"}}}";

        var result = LayoutTransfer.Import(shared);

        Assert.Null(result.Layout!.Acceleration);
        Assert.Empty(result.Warnings);
    }
}
