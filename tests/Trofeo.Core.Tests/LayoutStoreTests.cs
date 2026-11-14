using System.Text.Json;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public sealed class LayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-tests-" + Guid.NewGuid().ToString("N"));

    public LayoutStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "layout.json");

    [Fact]
    public void Load_Missing_ReturnsDefault()
    {
        var layout = new LayoutStore(FilePath).Load();

        Assert.Equal(180, layout.Rotation);
        Assert.Same(DashboardLayout.Default, layout);
    }

    [Fact]
    public void Default_HasFiveModules_NoOverlap()
    {
        var layout = DashboardLayout.Default;

        Assert.Equal(180, layout.Rotation);
        Assert.Equal(
            [("claudecode", 0, 0), ("monitoring", 8, 0), ("nowplaying", 8, 2), ("clock", 12, 0), ("network", 12, 2)],
            layout.Views[0].Placements.Select(p => (p.TypeId, p.Column, p.Row)));
        Assert.All(layout.Views[0].Placements.Where(p => p.TypeId != "claudecode"), p => Assert.Equal((4, 2), (p.Columns, p.Rows)));
        var claude = layout.Views[0].Placements.Single(p => p.TypeId == "claudecode");
        Assert.Equal((8, 4), (claude.Columns, claude.Rows));
        Assert.Equal(new Guid("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17"), claude.InstanceId);
        Assert.Equal("feed", claude.Settings.GetProperty("view").GetString());
        Assert.True(claude.Settings.GetProperty("autoFollow").GetBoolean());
        Assert.Null(layout.Shortcuts);
        Assert.Equal(layout.Views[0].Placements.Count, layout.Views[0].Placements.Select(p => p.InstanceId).Distinct().Count());
        Assert.Equal(new Guid("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01"), layout.Views[0].Placements.Single(p => p.TypeId == "clock").InstanceId);
        Assert.Equal("pc", layout.Views[0].Placements.Single(p => p.TypeId == "network").Settings.GetProperty("source").GetString());
        // Les modules de données restent dans les colonnes 8 à 15 ; Claude Code occupe les colonnes 0 à 7.
        Assert.All(layout.Views[0].Placements.Where(p => p.TypeId != "claudecode"), p => Assert.True(p.Column >= 8));
        foreach (var a in layout.Views[0].Placements)
        {
            foreach (var b in layout.Views[0].Placements.Where(b => b != a))
            {
                Assert.False(LayoutValidator.Intersects(a, b), $"{a.TypeId} chevauche {b.TypeId}");
            }
        }
    }

    [Fact]
    public void Default_SurvivesSaveAndLoad()
    {
        var store = new LayoutStore(FilePath);

        store.Save(DashboardLayout.Default);
        var loaded = store.Load();

        Assert.Equal(DashboardLayout.Default.Views[0].Placements.Select(p => p.InstanceId), loaded.Views[0].Placements.Select(p => p.InstanceId));
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var id = Guid.NewGuid();
        var settings = JsonDocument.Parse("""{"format":"12h"}""").RootElement.Clone();
        var layout = DashboardLayout.SingleView(0, [new ModulePlacement(id, "clock", 3, 1, 4, 2, TextSize.Large, settings)]);
        var store = new LayoutStore(FilePath);

        store.Save(layout);
        var loaded = store.Load();

        Assert.Equal(0, loaded.Rotation);
        var p = Assert.Single(loaded.Views[0].Placements);
        Assert.Equal(id, p.InstanceId);
        Assert.Equal("clock", p.TypeId);
        Assert.Equal((3, 1, 4, 2), (p.Column, p.Row, p.Columns, p.Rows));
        Assert.Equal(TextSize.Large, p.TextSize);
        Assert.Equal("12h", p.Settings.GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Object, p.Settings.ValueKind);
        Assert.Contains("\"Large\"", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Layout_WithShortcuts_RoundTrips()
    {
        var target = Guid.NewGuid();
        var placement = new ModulePlacement(
            Guid.NewGuid(), "clock", 0, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());
        var layout = DashboardLayout.SingleView(180, [placement],
        [
            new ShortcutBinding("Ctrl+Alt+Right", ShortcutCommand.NextTab),
            new ShortcutBinding("Ctrl+Shift+F5", ShortcutCommand.SelectTab, 4, target),
        ]);
        var store = new LayoutStore(FilePath);

        store.Save(layout);
        var loaded = store.Load();

        Assert.NotNull(loaded.Shortcuts);
        Assert.Equal(
            [
                new ShortcutBinding("Ctrl+Alt+Right", ShortcutCommand.NextTab),
                new ShortcutBinding("Ctrl+Shift+F5", ShortcutCommand.SelectTab, 4, target),
            ],
            loaded.Shortcuts);
        Assert.Contains("\"NextTab\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Layout_OldFile_ShortcutsNull()
    {
        File.WriteAllText(FilePath, $$"""
            {"Rotation":180,"Placements":[{"InstanceId":"11111111-1111-1111-1111-111111111111","TypeId":"clock","Column":0,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{{ClockSettings}}}]}
            """);

        var loaded = new LayoutStore(FilePath).Load();

        Assert.Single(loaded.Views[0].Placements);
        Assert.Null(loaded.Shortcuts);
    }

    [Fact]
    public void Layout_NullShortcutEntry_IsDroppedWithWarning()
    {
        File.WriteAllText(FilePath, """{"Rotation":180,"Placements":[],"Shortcuts":[null]}""");

        var warnings = new List<string>();
        var loaded = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.NotSame(DashboardLayout.Default, loaded);
        Assert.Empty(loaded.Shortcuts!);
        Assert.Equal(["Raccourci « ? » ignoré : touches absentes."], warnings);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_CorruptFile_FallsBackAndKeepsBackup()
    {
        File.WriteAllText(FilePath, "{nope");

        var layout = new LayoutStore(FilePath).Load();

        Assert.Same(DashboardLayout.Default, layout);
        Assert.Equal("{nope", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void Load_LockedFile_ReturnsDefault()
    {
        new LayoutStore(FilePath).Save(DashboardLayout.SingleView(0, []));
        using var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var layout = new LayoutStore(FilePath).Load();

        Assert.Same(DashboardLayout.Default, layout);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    private const string ClockSettings = "{}";

    private static string LayoutJson(int rotation, params (int c, int r, int cs, int rs)[] placements)
    {
        var items = placements.Select(p =>
            $$"""{"InstanceId":"{{Guid.NewGuid()}}","TypeId":"clock","Column":{{p.c}},"Row":{{p.r}},"Columns":{{p.cs}},"Rows":{{p.rs}},"TextSize":"Normal","Settings":{{ClockSettings}}}""");
        return $$"""{"Rotation":{{rotation}},"Placements":[{{string.Join(",", items)}}]}""";
    }

    private void AssertFallsBackAndKeepsBackup(string json)
    {
        File.WriteAllText(FilePath, json);

        var layout = new LayoutStore(FilePath).Load();

        Assert.Equal(180, layout.Rotation);
        Assert.Same(DashboardLayout.Default, layout);
        Assert.Equal(json, File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void Load_Rotation90_FallsBackAndKeepsBackup() =>
        AssertFallsBackAndKeepsBackup(LayoutJson(90, (0, 0, 4, 2)));

    [Fact]
    public void Load_OverlappingPlacements_FallsBackAndKeepsBackup() =>
        AssertFallsBackAndKeepsBackup(LayoutJson(180, (0, 0, 4, 2), (3, 1, 4, 2)));

    [Theory]
    [InlineData(14, 0, 4, 2)]
    [InlineData(0, 3, 4, 2)]
    [InlineData(-1, 0, 4, 2)]
    [InlineData(0, 0, 0, 2)]
    public void Load_OutOfGrid_FallsBackAndKeepsBackup(int c, int r, int cs, int rs) =>
        AssertFallsBackAndKeepsBackup(LayoutJson(180, (c, r, cs, rs)));

    [Theory]
    [InlineData("""{"InstanceId":"11111111-1111-1111-1111-111111111111","Column":0,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{}}""")]
    [InlineData("""{"InstanceId":"11111111-1111-1111-1111-111111111111","TypeId":null,"Column":0,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{}}""")]
    [InlineData("""{"InstanceId":"11111111-1111-1111-1111-111111111111","TypeId":"  ","Column":0,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{}}""")]
    public void Load_MissingTypeId_FallsBackAndKeepsBackup(string placement) =>
        AssertFallsBackAndKeepsBackup("""{"Rotation":180,"Placements":[""" + placement + "]}");

    [Fact]
    public void Load_DuplicateInstanceId_FallsBackAndKeepsBackup()
    {
        string Place(int column) =>
            """{"InstanceId":"11111111-1111-1111-1111-111111111111","TypeId":"clock","Column":COL,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{}}"""
                .Replace("COL", column.ToString());

        AssertFallsBackAndKeepsBackup("""{"Rotation":180,"Placements":[""" + Place(0) + "," + Place(4) + "]}");
    }

    [Fact]
    public void Load_IntOverflowPlacement_FallsBackAndKeepsBackup() =>
        AssertFallsBackAndKeepsBackup(LayoutJson(180, (int.MaxValue, 0, 2, 2)));

    [Fact]
    public void Load_ValidHandWrittenFile_IsAccepted()
    {
        File.WriteAllText(FilePath, LayoutJson(0, (0, 0, 4, 2), (4, 0, 4, 2)));

        var layout = new LayoutStore(FilePath).Load();

        Assert.Equal(0, layout.Rotation);
        Assert.Equal(2, layout.Views[0].Placements.Count);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    private static string WithAppearance(string layoutJson, string appearanceJson) =>
        layoutJson.TrimEnd().TrimEnd('}') + ",\"Appearance\":" + appearanceJson + "}";

    [Fact]
    public void Load_WithoutAppearance_IsNull()
    {
        File.WriteAllText(FilePath, LayoutJson(180, (0, 0, 4, 2)));

        var layout = new LayoutStore(FilePath).Load();

        Assert.Null(layout.Appearance);
        Assert.Single(layout.Views[0].Placements);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void SaveLoad_WithoutAppearance_StaysNull()
    {
        var store = new LayoutStore(FilePath);

        store.Save(DashboardLayout.Default);

        Assert.Null(store.Load().Appearance);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void SaveLoad_Legibility_RoundTrips(bool? legibility)
    {
        var store = new LayoutStore(FilePath);

        store.Save(DashboardLayout.Default with { Appearance = new AppearanceSettings("pixel", Legibility: legibility) });

        Assert.Equal(legibility, store.Load().Appearance!.Legibility);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_AppearanceWithoutLegibility_IsNullAndValid()
    {
        var warnings = new List<string>();
        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), """{"Theme":"pixel","PanelOpacity":0.4}"""));

        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.Null(layout.Appearance!.Legibility);
        Assert.Equal(0.4f, layout.Appearance.PanelOpacity);
        Assert.Empty(warnings);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_AppearanceWithLegibilityFalse_IsRead()
    {
        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), """{"Theme":"pixel","Legibility":false}"""));

        Assert.False(new LayoutStore(FilePath).Load().Appearance!.Legibility);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveLoad_AnimateViewSwitch_RoundTrips(bool animate)
    {
        var store = new LayoutStore(FilePath);

        store.Save(DashboardLayout.Default with { Appearance = new AppearanceSettings("pixel", AnimateViewSwitch: animate) });

        Assert.Equal(animate, store.Load().Appearance!.AnimateViewSwitch);
    }

    [Fact]
    public void Load_AppearanceWithoutAnimateViewSwitch_AnimatesByDefault()
    {
        var warnings = new List<string>();
        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), """{"Theme":"pixel"}"""));

        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.True(layout.Appearance!.AnimateViewSwitch);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Load_AnimateViewSwitchFalse_IsRead()
    {
        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), """{"Theme":"pixel","AnimateViewSwitch":false}"""));

        Assert.False(new LayoutStore(FilePath).Load().Appearance!.AnimateViewSwitch);
    }

    [Theory]
    [InlineData("\"non\"")]
    [InlineData("3")]
    [InlineData("[]")]
    public void Load_UnreadableAnimateViewSwitch_DefaultsToTrue_KeepsTheRestOfTheAppearance(string value)
    {
        File.WriteAllText(
            FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), $$"""{"Theme":"pixel","PanelOpacity":0.4,"AnimateViewSwitch":{{value}}}"""));

        var warnings = new List<string>();
        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.True(layout.Appearance!.AnimateViewSwitch);
        Assert.Equal("pixel", layout.Appearance.Theme);
        Assert.Equal(0.4f, layout.Appearance.PanelOpacity);
        Assert.Equal("clock", Assert.Single(layout.Views[0].Placements).TypeId);
        Assert.Equal(
            ["Réglage « AnimateViewSwitch » illisible dans layout.json : passage animé entre les vues par défaut."], warnings);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_UnknownTheme_KeepsPlacements()
    {
        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), """{"Theme":"neon"}"""));

        var layout = new LayoutStore(FilePath).Load();

        Assert.Single(layout.Views[0].Placements);
        Assert.Equal("neon", layout.Appearance!.Theme);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"pixel\"")]
    [InlineData("[]")]
    [InlineData("""{"Theme":"pixel","Background":{"Kind":"Plasma"}}""")]
    [InlineData("""{"Theme":"pixel","Background":{"Kind":99}}""")]
    [InlineData("""{"Theme":42}""")]
    [InlineData("""{"Theme":null}""")]
    [InlineData("""{"Theme":"pixel","PanelOpacity":"beaucoup"}""")]
    [InlineData("""{"Theme":"pixel","Font":7}""")]
    public void Load_BrokenAppearance_KeepsLayout(string appearance)
    {
        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), appearance));

        var warnings = new List<string>();
        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.Equal(180, layout.Rotation);
        Assert.Equal("clock", Assert.Single(layout.Views[0].Placements).TypeId);
        Assert.Null(layout.Appearance);
        Assert.False(File.Exists(FilePath + ".bad"));
        Assert.Equal(["Réglages d'apparence illisibles dans layout.json : apparence par défaut utilisée."], warnings);
    }

    [Fact]
    public void Load_ValidOrNullAppearance_DoesNotWarn()
    {
        var warnings = new List<string>();
        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), """{"Theme":"pixel"}"""));
        Assert.Equal("pixel", new LayoutStore(FilePath, warnings.Add).Load().Appearance!.Theme);

        File.WriteAllText(FilePath, WithAppearance(LayoutJson(180, (0, 0, 4, 2)), "null"));
        Assert.Null(new LayoutStore(FilePath, warnings.Add).Load().Appearance);

        File.WriteAllText(FilePath, LayoutJson(180, (0, 0, 4, 2)));
        Assert.Null(new LayoutStore(FilePath, warnings.Add).Load().Appearance);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Load_DuplicateKeys_StayReadableLastValueWins()
    {
        // Avant le plan 4, le sérialiseur acceptait un nom de propriété en double (la dernière valeur gagne).
        var json = LayoutJson(180, (0, 0, 4, 2)).TrimEnd().TrimEnd('}') + ",\"Rotation\":0,\"Appearance\":{\"Theme\":\"classique\"}}";
        File.WriteAllText(FilePath, json);

        var layout = new LayoutStore(FilePath).Load();

        Assert.Equal(0, layout.Rotation);
        Assert.Equal("clock", Assert.Single(layout.Views[0].Placements).TypeId);
        Assert.Equal("classique", layout.Appearance!.Theme);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_DuplicateKey_NullShortcutEntry_IsDroppedWithWarning()
    {
        // Chemin d'avant (clé en double) : les entrées null de Shortcuts n'arrivent pas jusqu'aux appelants.
        var json = LayoutJson(180, (0, 0, 4, 2)).TrimEnd().TrimEnd('}') +
                   ",\"Rotation\":0,\"Shortcuts\":[null,{\"Keys\":\"Ctrl+Alt+F6\",\"Command\":\"NextTab\"}]}";
        File.WriteAllText(FilePath, json);

        var warnings = new List<string>();
        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.Equal(0, layout.Rotation);
        var binding = Assert.Single(layout.Shortcuts!);
        Assert.Equal("Ctrl+Alt+F6", binding.Keys);
        Assert.Equal(["Raccourci « ? » ignoré : touches absentes."], warnings);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_DuplicateAppearanceKey_StaysReadable()
    {
        var json = LayoutJson(180, (0, 0, 4, 2)).TrimEnd().TrimEnd('}') +
                   ",\"Appearance\":{\"Theme\":\"pixel\"},\"Appearance\":{\"Theme\":\"liquid-glass\"}}";
        File.WriteAllText(FilePath, json);

        var layout = new LayoutStore(FilePath).Load();

        Assert.Single(layout.Views[0].Placements);
        Assert.Equal("liquid-glass", layout.Appearance!.Theme);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_InvalidPlacementsWithBrokenAppearance_StillFallsBack()
    {
        AssertFallsBackAndKeepsBackup(WithAppearance(LayoutJson(180, (int.MaxValue, 0, 2, 2)), "42"));
    }

    [Fact]
    public void SaveLoad_RoundTrips_Appearance()
    {
        var appearance = new AppearanceSettings(
            "liquid-glass", 0.5f, "Manrope",
            new BackgroundSettings(BackgroundKind.Animated, null, @"C:\Users\alex\Images\pluie.gif"));
        var layout = DashboardLayout.Default with { Appearance = appearance };
        var store = new LayoutStore(FilePath);

        store.Save(layout);
        var loaded = store.Load();

        Assert.Equal(appearance, loaded.Appearance);
        Assert.Equal(layout.Views[0].Placements.Select(p => p.InstanceId), loaded.Views[0].Placements.Select(p => p.InstanceId));
        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"Kind\": \"Animated\"", text);
        Assert.Contains("\"Theme\": \"liquid-glass\"", text);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void SaveLoad_RoundTrips_DefaultAppearance()
    {
        var layout = DashboardLayout.Default with { Appearance = AppearanceSettings.Default };
        var store = new LayoutStore(FilePath);

        store.Save(layout);

        Assert.Equal(AppearanceSettings.Default, store.Load().Appearance);
    }

    // ---- Plan 5, tâche 1 : modules retirés, plafond d'images, lecture sans repli ----

    private static ModulePlacement Clock(int column, int row, int columns = 4, int rows = 2, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "clock", column, row, columns, rows, TextSize.Normal,
            JsonDocument.Parse("{}").RootElement.Clone());

    private static ModulePlacement Network(int column, int row, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "network", column, row, 4, 2, TextSize.Normal,
            JsonDocument.Parse("""{"source":"pc"}""").RootElement.Clone());

    private static string WithKey(string layoutJson, string key, string valueJson) =>
        layoutJson.TrimEnd().TrimEnd('}') + $",\"{key}\":" + valueJson + "}";

    [Fact]
    public void Load_OldFile_HasNoShelfNoMaxFps()
    {
        File.WriteAllText(FilePath, LayoutJson(180, (0, 0, 4, 2)));

        var layout = new LayoutStore(FilePath).Load();

        Assert.Single(layout.Views[0].Placements);
        Assert.Null(layout.Shelf);
        Assert.Null(layout.MaxFps);
    }

    [Fact]
    public void SaveLoad_ShelfAndMaxFps_RoundTrip()
    {
        var shelved = Network(0, 0);
        var layout = DashboardLayout.SingleView(180, [Clock(0, 0)], shelf: [shelved], maxFps: 8);
        var store = new LayoutStore(FilePath);

        store.Save(layout);
        var loaded = store.Load();

        Assert.Equal(8, loaded.MaxFps);
        var back = Assert.Single(loaded.Shelf!);
        Assert.Equal((shelved.InstanceId, "network"), (back.InstanceId, back.TypeId));
        Assert.Equal("pc", back.Settings.GetProperty("source").GetString());
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("40")]
    [InlineData("-3")]
    [InlineData("\"vite\"")]
    public void Load_MaxFpsOutOfRange_IsNullWithWarning(string maxFps)
    {
        File.WriteAllText(FilePath, WithKey(LayoutJson(180, (0, 0, 4, 2)), "MaxFps", maxFps));

        var warnings = new List<string>();
        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.Single(layout.Views[0].Placements);
        Assert.Null(layout.MaxFps);
        Assert.Equal(["Plafond d'images invalide : 12 images/s utilisées."], warnings);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("12")]
    public void Load_MaxFpsInRange_IsKept(string maxFps)
    {
        File.WriteAllText(FilePath, WithKey(LayoutJson(180, (0, 0, 4, 2)), "MaxFps", maxFps));

        var warnings = new List<string>();
        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.Equal(int.Parse(maxFps), layout.MaxFps);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Load_DuplicateIdAcrossShelf_IsRejected()
    {
        var id = Guid.NewGuid();
        var store = new LayoutStore(FilePath);
        store.Save(DashboardLayout.SingleView(180, [Clock(0, 0, id: id)], shelf: [Network(0, 0, id)]));

        Assert.Same(DashboardLayout.Default, store.Load());
        Assert.True(File.Exists(FilePath + ".bad"));
        File.Delete(FilePath + ".bad");

        var result = store.TryLoad();
        Assert.Null(result.Layout);
        Assert.Equal("Identifiant de module en double.", result.Error);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_ShelfOffGrid_IsAccepted()
    {
        var store = new LayoutStore(FilePath);
        store.Save(DashboardLayout.SingleView(180, [Clock(0, 0)], shelf: [Network(20, 9)]));

        var layout = store.Load();

        Assert.Equal(20, Assert.Single(layout.Shelf!).Column);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Theory]
    [InlineData("""{"InstanceId":"22222222-2222-2222-2222-222222222222","TypeId":"","Column":0,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{}}""")]
    [InlineData("""{"InstanceId":"22222222-2222-2222-2222-222222222222","TypeId":"network","Column":0,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal"}""")]
    [InlineData("""{"InstanceId":"22222222-2222-2222-2222-222222222222","TypeId":"network","Column":0,"Row":0,"Columns":0,"Rows":2,"TextSize":"Normal","Settings":{}}""")]
    [InlineData("null")]
    public void Load_InvalidShelfEntry_IsRejected(string entry)
    {
        File.WriteAllText(FilePath, WithKey(LayoutJson(180, (0, 0, 4, 2)), "Shelf", "[" + entry + "]"));

        var result = new LayoutStore(FilePath).TryLoad();

        Assert.Null(result.Layout);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_OneInvalidShortcut_KeepsLayoutAndOthers()
    {
        File.WriteAllText(FilePath, WithKey(LayoutJson(180, (0, 0, 4, 2)), "Shortcuts", """
            [
              {"Keys":"Ctrl+Alt+F6","Command":"NextTab","Index":0,"Target":null},
              {"Keys":"Ctrl+Alt+X","Command":"Fly","Index":0,"Target":null},
              {"Command":"NextTab"},
              {"Keys":"Ctrl","Command":"NextTab"},
              {"Keys":"Ctrl+Alt+F5","Command":"SelectTab","Index":3,"Target":null}
            ]
            """));

        var warnings = new List<string>();
        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.Single(layout.Views[0].Placements);
        Assert.Equal(
            [
                new ShortcutBinding("Ctrl+Alt+F6", ShortcutCommand.NextTab),
                new ShortcutBinding("Ctrl+Alt+F5", ShortcutCommand.SelectTab, 3),
            ],
            layout.Shortcuts);
        Assert.Equal(
            [
                "Raccourci « Ctrl+Alt+X » ignoré : commande inconnue.",
                "Raccourci « ? » ignoré : touches absentes.",
                "Raccourci « Ctrl » ignoré : touches non reconnues.",
            ],
            warnings);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Load_ShortcutsNotAnArray_IsNullWithWarning()
    {
        File.WriteAllText(FilePath, WithKey(LayoutJson(180, (0, 0, 4, 2)), "Shortcuts", "\"oups\""));

        var warnings = new List<string>();
        var layout = new LayoutStore(FilePath, warnings.Add).Load();

        Assert.Single(layout.Views[0].Placements);
        Assert.Null(layout.Shortcuts);
        Assert.Single(warnings);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void TryLoad_Overlap_ReturnsReasonAndNoBackup()
    {
        new LayoutStore(FilePath).Save(DashboardLayout.SingleView(180, [Clock(0, 0), Network(2, 0)]));

        var result = new LayoutStore(FilePath).TryLoad();

        Assert.Null(result.Layout);
        Assert.Equal("Deux modules se chevauchent : clock et network.", result.Error);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void TryLoad_Errors_AreInFrench()
    {
        File.WriteAllText(FilePath, "{nope");
        Assert.Equal("Le fichier layout.json est illisible (JSON invalide).", new LayoutStore(FilePath).TryLoad().Error);

        File.WriteAllText(FilePath, LayoutJson(90, (0, 0, 4, 2)));
        Assert.Equal("Rotation 90° refusée : 0 ou 180.", new LayoutStore(FilePath).TryLoad().Error);

        File.WriteAllText(FilePath, LayoutJson(180, (14, 0, 4, 2)));
        Assert.Equal("Module hors du damier : clock.", new LayoutStore(FilePath).TryLoad().Error);

        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void TryLoad_Valid_ReturnsLayoutAndWarnings()
    {
        File.WriteAllText(FilePath, WithKey(LayoutJson(180, (0, 0, 4, 2)), "MaxFps", "99"));

        var result = new LayoutStore(FilePath).TryLoad();

        Assert.Null(result.Error);
        Assert.Single(result.Layout!.Views[0].Placements);
        Assert.Equal(["Plafond d'images invalide : 12 images/s utilisées."], result.Warnings);
    }

    [Fact]
    public void TryLoad_Missing_IsDefault()
    {
        var result = new LayoutStore(FilePath).TryLoad();

        Assert.Same(DashboardLayout.Default, result.Layout);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TryLoad_LockedFile_ReturnsErrorNotDefault()
    {
        new LayoutStore(FilePath).Save(DashboardLayout.SingleView(0, []));
        using var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = new LayoutStore(FilePath).TryLoad();

        Assert.Null(result.Layout);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Stamp_ChangesWhenFileIsRewritten()
    {
        var store = new LayoutStore(FilePath);
        Assert.Null(store.Stamp());

        store.Save(DashboardLayout.SingleView(180, []));
        var first = store.Stamp();
        Assert.NotNull(first);

        store.Save(DashboardLayout.SingleView(180, [Clock(0, 0)]));
        File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddMinutes(5));
        var second = store.Stamp();

        Assert.NotEqual(first, second);
    }
}
