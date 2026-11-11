using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

/// <summary>Les commandes de raccourci et la migration, une seule fois, des raccourcis par défaut d'un fichier d'avant les vues.</summary>
public sealed class ShortcutMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-tests-" + Guid.NewGuid().ToString("N"));

    public ShortcutMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "layout.json");

    /// <summary>Les liaisons par défaut de la 1.1.0, telles qu'elles étaient écrites.</summary>
    private const string OldDefaults = """
        {"Keys":"Ctrl+Alt+Left","Command":"PreviousTab","Index":0,"Target":null},
        {"Keys":"Ctrl+Alt+Right","Command":"NextTab","Index":0,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+1","Command":"SelectTab","Index":0,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+2","Command":"SelectTab","Index":1,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+3","Command":"SelectTab","Index":2,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+4","Command":"SelectTab","Index":3,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+5","Command":"SelectTab","Index":4,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+6","Command":"SelectTab","Index":5,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+7","Command":"SelectTab","Index":6,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+8","Command":"SelectTab","Index":7,"Target":null},
        {"Keys":"Ctrl+Alt+Shift+9","Command":"SelectTab","Index":8,"Target":null}
        """;

    private const string OneCase =
        """{"InstanceId":"aaaaaaaa-0000-0000-0000-000000000001","TypeId":"clock","Column":0,"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{}}""";

    /// <summary>Un fichier d'avant les vues (cases à la racine) avec ces raccourcis (<c>null</c> : pas de propriété <c>Shortcuts</c>).</summary>
    private static string LegacyFile(string? shortcuts) =>
        $$"""{"Rotation":180,"Placements":[{{OneCase}}]{{(shortcuts is null ? "" : ",\"Shortcuts\":[" + shortcuts + "]")}}}""";

    /// <summary>Un fichier au format des vues avec ces raccourcis.</summary>
    private static string ViewsFile(string shortcuts) =>
        $$"""{"Rotation":180,"Views":[{"Id":"bbbbbbbb-0000-0000-0000-000000000001","Name":"Vue 1","Placements":[{{OneCase}}]}],"Shortcuts":[{{shortcuts}}]}""";

    private DashboardLayout Load(string json)
    {
        File.WriteAllText(FilePath, json);
        return new LayoutStore(FilePath).Load();
    }

    private static ShortcutBinding? At(DashboardLayout layout, string keys) =>
        layout.Shortcuts!.SingleOrDefault(b => ShortcutKeys.Same(b.Keys, keys));

    [Fact]
    public void Legacy_DefaultBindings_MoveTabsToUpDownAndAddViews()
    {
        var layout = Load(LegacyFile(OldDefaults));

        Assert.Equal(ShortcutCommand.PreviousTab, At(layout, "Ctrl+Alt+Up")!.Command);
        Assert.Equal(ShortcutCommand.NextTab, At(layout, "Ctrl+Alt+Down")!.Command);
        Assert.Equal(ShortcutCommand.PreviousView, At(layout, "Ctrl+Alt+Left")!.Command);
        Assert.Equal(ShortcutCommand.NextView, At(layout, "Ctrl+Alt+Right")!.Command);
        for (var n = 1; n <= 9; n++)
            Assert.Equal(new ShortcutBinding($"Ctrl+Alt+Shift+{n}", ShortcutCommand.SelectTab, n - 1), At(layout, $"Ctrl+Alt+Shift+{n}"));
        Assert.Equal(13, layout.Shortcuts!.Count);
        Assert.Equal(ShortcutDefaults.All, layout.Shortcuts);
    }

    [Fact]
    public void Legacy_CustomLeftBinding_IsKeptAndLeftNotGivenToViews()
    {
        var custom = """{"Keys":"Ctrl+Alt+Left","Command":"SelectTab","Index":2,"Target":null}""";
        var right = """{"Keys":"Ctrl+Alt+Right","Command":"NextTab","Index":0,"Target":null}""";

        var layout = Load(LegacyFile(custom + "," + right));

        Assert.Equal(new ShortcutBinding("Ctrl+Alt+Left", ShortcutCommand.SelectTab, 2), At(layout, "Ctrl+Alt+Left"));
        Assert.DoesNotContain(layout.Shortcuts!, b => b.Command == ShortcutCommand.PreviousView);
        Assert.Equal(ShortcutCommand.NextView, At(layout, "Ctrl+Alt+Right")!.Command);
        Assert.Equal(ShortcutCommand.NextTab, At(layout, "Ctrl+Alt+Down")!.Command);
        Assert.Null(At(layout, "Ctrl+Alt+Up"));
    }

    [Fact]
    public void Legacy_NoShortcutsProperty_GetsNewDefaults()
    {
        var layout = Load(LegacyFile(null));

        Assert.Null(layout.Shortcuts);
        Assert.Equal(ShortcutDefaults.All, layout.Shortcuts ?? ShortcutDefaults.All);
    }

    [Fact]
    public void NewFormat_LeftBoundToPreviousTab_IsNotMigratedAgain()
    {
        var layout = Load(ViewsFile("""{"Keys":"Ctrl+Alt+Left","Command":"PreviousTab","Index":0,"Target":null}"""));

        Assert.Equal([new ShortcutBinding("Ctrl+Alt+Left", ShortcutCommand.PreviousTab)], layout.Shortcuts);
    }

    [Fact]
    public void Legacy_DefaultBindingsWrittenInOtherCase_AreStillRecognised()
    {
        var layout = Load(LegacyFile(
            """{"Keys":"alt+ctrl+left","Command":"PreviousTab"},{"Keys":"CTRL+ALT+RIGHT","Command":"NextTab"}"""));

        Assert.Equal(
            [
                (ShortcutCommand.PreviousTab, "Ctrl+Alt+Up"),
                (ShortcutCommand.NextTab, "Ctrl+Alt+Down"),
                (ShortcutCommand.PreviousView, "Ctrl+Alt+Left"),
                (ShortcutCommand.NextView, "Ctrl+Alt+Right"),
            ],
            layout.Shortcuts!.Select(b => (b.Command, b.Keys)));
    }

    [Fact]
    public void Legacy_BindingWithATargetOrAnIndex_IsNotAnOldDefault()
    {
        var target = Guid.NewGuid();
        var layout = Load(LegacyFile(
            $$"""{"Keys":"Ctrl+Alt+Left","Command":"PreviousTab","Index":0,"Target":"{{target}}"},{"Keys":"Ctrl+Alt+Right","Command":"NextTab","Index":1,"Target":null}"""));

        Assert.Equal(
            [
                new ShortcutBinding("Ctrl+Alt+Left", ShortcutCommand.PreviousTab, 0, target),
                new ShortcutBinding("Ctrl+Alt+Right", ShortcutCommand.NextTab, 1),
            ],
            layout.Shortcuts);
    }

    [Fact]
    public void Legacy_UpAlreadyTakenByAlex_KeepsTheOldDefaultWhereItIs()
    {
        var layout = Load(LegacyFile(
            """{"Keys":"Ctrl+Alt+Up","Command":"SelectTab","Index":4},{"Keys":"Ctrl+Alt+Left","Command":"PreviousTab"}"""));

        Assert.Equal(
            [
                new ShortcutBinding("Ctrl+Alt+Up", ShortcutCommand.SelectTab, 4),
                new ShortcutBinding("Ctrl+Alt+Left", ShortcutCommand.PreviousTab),
            ],
            layout.Shortcuts);
    }

    [Fact]
    public void Legacy_EmptyShortcuts_StayEmpty()
    {
        Assert.Empty(Load(LegacyFile("")).Shortcuts!);
    }

    [Fact]
    public void Migration_RunsOnce_ASavedFileIsNotMigratedAgain()
    {
        var store = new LayoutStore(FilePath);
        File.WriteAllText(FilePath, LegacyFile(OldDefaults));
        var migrated = store.Load();
        store.Save(migrated);

        var reloaded = store.Load();

        Assert.Equal(migrated.Shortcuts, reloaded.Shortcuts);
    }

    [Fact]
    public void Binding_SelectView_RoundTrips()
    {
        var layout = DashboardLayout.Default with { Shortcuts = [new ShortcutBinding("Ctrl+Alt+Shift+V", ShortcutCommand.SelectView, 3)] };
        var store = new LayoutStore(FilePath);

        store.Save(layout);
        var loaded = store.Load();

        Assert.Equal([new ShortcutBinding("Ctrl+Alt+Shift+V", ShortcutCommand.SelectView, 3)], loaded.Shortcuts);
        Assert.Contains("\"SelectView\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Binding_Community_RoundTripsModuleAndShortcutIds()
    {
        var binding = new ShortcutBinding("Ctrl+Alt+Shift+G", ShortcutCommand.Community, 0, Guid.NewGuid(), "acme.github", "refresh");
        var layout = DashboardLayout.Default with { Shortcuts = [binding] };
        var store = new LayoutStore(FilePath);

        store.Save(layout);
        var loaded = store.Load();

        Assert.Equal([binding], loaded.Shortcuts);
    }

    [Fact]
    public void Binding_WithoutModuleOrShortcutId_DoesNotWriteThem()
    {
        var store = new LayoutStore(FilePath);

        store.Save(DashboardLayout.Default with { Shortcuts = [new ShortcutBinding("Ctrl+Alt+Right", ShortcutCommand.NextView)] });

        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain("ModuleId", json);
        Assert.DoesNotContain("ShortcutId", json);
    }

    [Fact]
    public void ToModuleCommand_OnlyTheThreeTabCommands()
    {
        Assert.Equal(ModuleCommandKind.PreviousTab, ShortcutCommand.PreviousTab.ToModuleCommand());
        Assert.Equal(ModuleCommandKind.NextTab, ShortcutCommand.NextTab.ToModuleCommand());
        Assert.Equal(ModuleCommandKind.SelectTab, ShortcutCommand.SelectTab.ToModuleCommand());
        Assert.Null(ShortcutCommand.PreviousView.ToModuleCommand());
        Assert.Null(ShortcutCommand.NextView.ToModuleCommand());
        Assert.Null(ShortcutCommand.SelectView.ToModuleCommand());
        Assert.Null(ShortcutCommand.Community.ToModuleCommand());
    }

    [Fact]
    public void NumericCommandValues_KeepTheirOldMeaningForTabs()
    {
        var layout = Load(ViewsFile("""{"Keys":"Ctrl+Alt+F5","Command":2,"Index":1}"""));

        Assert.Equal([new ShortcutBinding("Ctrl+Alt+F5", ShortcutCommand.SelectTab, 1)], layout.Shortcuts);
    }
}
