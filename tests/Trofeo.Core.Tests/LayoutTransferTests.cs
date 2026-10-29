using System.Text.Json;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public sealed class LayoutTransferTests
{
    private static DashboardLayout Sample() => DashboardLayout.Default with
    {
        Shortcuts = [new ShortcutBinding("Ctrl+Alt+Right", ModuleCommandKind.NextTab)],
        Appearance = new AppearanceSettings("pixel", PanelOpacity: 0.4f),
        Shelf =
        [
            new ModulePlacement(
                Guid.NewGuid(), "network", 20, 0, 4, 2, TextSize.Large,
                JsonDocument.Parse("""{"source":"pc"}""").RootElement.Clone()),
        ],
        MaxFps = 6,
    };

    [Fact]
    public void Export_ThenImport_GivesSameLayout()
    {
        var layout = Sample();

        var result = LayoutTransfer.Import(LayoutTransfer.Export(layout));

        Assert.Null(result.Error);
        var back = result.Layout!;
        Assert.Equal(layout.Rotation, back.Rotation);
        Assert.Equal(layout.Placements.Select(p => (p.InstanceId, p.TypeId, p.Column, p.Row, p.Columns, p.Rows, p.TextSize)),
            back.Placements.Select(p => (p.InstanceId, p.TypeId, p.Column, p.Row, p.Columns, p.Rows, p.TextSize)));
        Assert.Equal(layout.Shortcuts, back.Shortcuts);
        Assert.Equal(layout.Appearance, back.Appearance);
        Assert.Equal(layout.MaxFps, back.MaxFps);
        Assert.Equal(layout.Shelf![0].InstanceId, back.Shelf![0].InstanceId);
        Assert.Equal("pc", back.Shelf[0].Settings.GetProperty("source").GetString());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(layout.Placements[0].Settings.GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(back.Placements[0].Settings.GetRawText())));
    }

    [Fact]
    public void Export_HasFormatMarker()
    {
        using var doc = JsonDocument.Parse(LayoutTransfer.Export(Sample()));

        Assert.Equal(LayoutTransfer.Format, doc.RootElement.GetProperty("Format").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("Layout").ValueKind);
    }

    [Theory]
    [InlineData("{nope")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"Rotation":180,"Placements":[]}""")]
    [InlineData("""{"Format":"autre/chose/1","Layout":{"Rotation":180,"Placements":[]}}""")]
    [InlineData("""{"Format":"trofeo-HB-dashboard/disposition/1"}""")]
    public void Import_WrongFormat_IsRefused(string json)
    {
        var result = LayoutTransfer.Import(json);

        Assert.Null(result.Layout);
        Assert.Equal("Ce fichier n'est pas une disposition Ashboard Vision.", result.Error);
    }

    [Fact]
    public void Import_InvalidLayout_GivesSameReasonAsTryLoad()
    {
        var overlapping = new DashboardLayout(180,
        [
            new ModulePlacement(Guid.NewGuid(), "clock", 0, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone()),
            new ModulePlacement(Guid.NewGuid(), "network", 2, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone()),
        ]);

        var result = LayoutTransfer.Import(LayoutTransfer.Export(overlapping));

        Assert.Null(result.Layout);
        Assert.Equal("Deux modules se chevauchent : clock et network.", result.Error);
    }

    [Fact]
    public void Import_KeepsWarnings()
    {
        var json = LayoutTransfer.Export(Sample() with { MaxFps = 99 });

        var result = LayoutTransfer.Import(json);

        Assert.Null(result.Layout!.MaxFps);
        Assert.Equal(["Plafond d'images invalide : 12 images/s utilisées."], result.Warnings);
    }

    [Fact]
    public void Export_NeverContainsVaultContent()
    {
        var json = LayoutTransfer.Export(Sample()).ToLowerInvariant();

        Assert.DoesNotContain("secrets", json);
        Assert.DoesNotContain("apikey", json);
        Assert.DoesNotContain("token", json);
    }
}
