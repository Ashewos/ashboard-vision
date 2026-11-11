using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public sealed class AppearanceCommandTests : IDisposable
{
    private sealed class FakeFonts(params string[] installed) : ISystemFonts
    {
        public IReadOnlyList<string> Families { get; } = installed;
        public SKTypeface? Match(string family) =>
            installed.Contains(family, StringComparer.OrdinalIgnoreCase) ? SKTypeface.Default : null;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-appearance-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public AppearanceCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LayoutPath => Path.Combine(_dir, "layout.json");

    private static DashboardLayout SampleLayout() => DashboardLayout.SingleView(
        180,
        [
            new ModulePlacement(Guid.NewGuid(), "clock", 0, 0, 4, 2, TextSize.Large, JsonDocument.Parse("""{"format":"12h"}""").RootElement.Clone()),
            new ModulePlacement(Guid.NewGuid(), "network", 4, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone()),
        ],
        [new ShortcutBinding("Ctrl+Alt+Right", ModuleCommandKind.NextTab), new ShortcutBinding("Ctrl+Shift+F5", ModuleCommandKind.SelectTab, 4, Guid.NewGuid())]);

    private int Run(params string[] args) =>
        AppearanceCommand.Run(LayoutPath, args, new FakeFonts("Segoe UI", "Arial"), _out, _err);

    private DashboardLayout Reload() => new LayoutStore(LayoutPath).Load();

    [Fact]
    public void HardwareCheck_Appearance_KeepsPlacements()
    {
        var original = SampleLayout();
        new LayoutStore(LayoutPath).Save(original);

        var code = Run("theme=liquid-glass", "opacity=0,5", "font=Segoe UI", "background=color:#1A2B3C");

        Assert.Equal(0, code);
        var loaded = Reload();
        Assert.Equal(original.Rotation, loaded.Rotation);
        Assert.Equal(original.Shortcuts, loaded.Shortcuts);
        Assert.Equal(original.Views[0].Placements.Count, loaded.Views[0].Placements.Count);
        for (var i = 0; i < original.Views[0].Placements.Count; i++)
        {
            var (before, after) = (original.Views[0].Placements[i], loaded.Views[0].Placements[i]);
            Assert.Equal(
                (before.InstanceId, before.TypeId, before.Column, before.Row, before.Columns, before.Rows, before.TextSize),
                (after.InstanceId, after.TypeId, after.Column, after.Row, after.Columns, after.Rows, after.TextSize));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before.Settings.GetRawText()), JsonNode.Parse(after.Settings.GetRawText())));
        }

        Assert.Equal(
            new AppearanceSettings("liquid-glass", 0.5f, "Segoe UI", new BackgroundSettings(BackgroundKind.Color, Color: "#1A2B3C")),
            loaded.Appearance);
        Assert.False(File.Exists(LayoutPath + ".tmp"));
    }

    [Fact]
    public void Run_SeveralCalls_KeepEarlierSettings()
    {
        new LayoutStore(LayoutPath).Save(SampleLayout());

        Run("theme=pixel", "opacity=0.3");
        Run("font=Arial");
        Run("opacity=theme");

        Assert.Equal(new AppearanceSettings("pixel", null, "Arial"), Reload().Appearance);
    }

    [Fact]
    public void Run_NoLayoutFile_CreatesDefaultLayoutWithAppearance()
    {
        Assert.Equal(0, Run("theme=pixel"));

        var loaded = Reload();
        Assert.Equal("pixel", loaded.Appearance!.Theme);
        Assert.Equal(DashboardLayout.Default.Views[0].Placements.Count, loaded.Views[0].Placements.Count);
    }

    [Theory]
    [InlineData("theme=inconnu")]
    [InlineData("opacity=1.5")]
    [InlineData("opacity=-0.1")]
    [InlineData("opacity=beaucoup")]
    [InlineData("opacity=NaN")]
    [InlineData("background=hologramme:x")]
    [InlineData("background=color:rouge")]
    [InlineData("background=image:")]
    [InlineData("background=image:C:\\nulle\\part.png")]
    [InlineData("couleur=rouge")]
    [InlineData("theme")]
    public void Run_InvalidValue_ExitsWith2AndLeavesFileUntouched(string arg)
    {
        new LayoutStore(LayoutPath).Save(SampleLayout());
        var before = File.ReadAllBytes(LayoutPath);

        var code = Run("opacity=0.4", arg); // une valeur valide avant : rien ne doit être écrit non plus

        Assert.Equal(2, code);
        Assert.NotEmpty(_err.ToString());
        Assert.Equal(before, File.ReadAllBytes(LayoutPath));
    }

    [Theory]
    [InlineData("legibility=off", false)]
    [InlineData("legibility=OFF", false)]
    [InlineData("legibility=on", true)]
    [InlineData("legibility=ON", true)]
    public void Run_Legibility_OnOff_IsStored(string arg, bool expected)
    {
        var original = SampleLayout();
        new LayoutStore(LayoutPath).Save(original);

        var code = Run("opacity=0.2", arg);

        Assert.Equal(0, code);
        var appearance = Reload().Appearance!;
        Assert.Equal(expected, appearance.Legibility);
        Assert.Equal(0.2f, appearance.PanelOpacity); // les autres réglages sont gardés
        Assert.Equal(original.Views[0].Placements.Count, Reload().Views[0].Placements.Count);
    }

    [Fact]
    public void Run_Legibility_Theme_RestoresTheDefault()
    {
        Run("legibility=off");
        Assert.False(Reload().Appearance!.Legibility);

        var code = Run("legibility=theme");

        Assert.Equal(0, code);
        Assert.Null(Reload().Appearance!.Legibility);
    }

    [Theory]
    [InlineData("legibility=peut-etre")]
    [InlineData("legibility=")]
    [InlineData("legibility=1")]
    public void Run_Legibility_UnknownValue_ExitsWith2AndWritesNothing(string arg)
    {
        new LayoutStore(LayoutPath).Save(SampleLayout());
        var before = File.ReadAllBytes(LayoutPath);

        var code = Run("opacity=0.4", arg);

        Assert.Equal(2, code);
        Assert.Contains("legibility", _err.ToString());
        Assert.Equal(before, File.ReadAllBytes(LayoutPath));
    }

    [Fact]
    public void Run_Describe_ShowsLegibility()
    {
        Run();
        Assert.Contains("Lisibilité", _out.ToString());
        Assert.Contains("garde active", _out.ToString());

        _out.GetStringBuilder().Clear();
        Run("legibility=off");
        Assert.Contains("garde coupée", _out.ToString());
    }

    [Fact]
    public void Run_ValueSplitAtTheComma_ExitsWith2_AndSuggestsADot()
    {
        // Start-Process -ArgumentList opacity=0,5 envoie « opacity=0 » puis « 5 » : rien n'est écrit, et le message dit quoi faire.
        new LayoutStore(LayoutPath).Save(SampleLayout());
        var before = File.ReadAllBytes(LayoutPath);

        var code = Run("opacity=0", "5");

        Assert.Equal(2, code);
        Assert.Contains("opacity=0.5", _err.ToString());
        Assert.Equal(before, File.ReadAllBytes(LayoutPath));
    }

    [Theory]
    [InlineData("opacity=0,5", 0.5f)]
    [InlineData("opacity=0.5", 0.5f)]
    [InlineData("opacity=1", 1f)]
    public void Run_Opacity_AcceptsACommaOrADot(string arg, float expected)
    {
        Assert.Equal(0, Run(arg));

        Assert.Equal(expected, Reload().Appearance!.PanelOpacity);
    }

    [Fact]
    public void Run_UnknownTheme_ListsTheKnownOnes()
    {
        Run("theme=inconnu");

        Assert.Contains("liquid-glass", _err.ToString());
        Assert.Contains("pixel", _err.ToString());
    }

    [Fact]
    public void Run_UnknownFont_IsAcceptedWithAWarning()
    {
        var code = Run("font=Police Absente");

        Assert.Equal(0, code);
        Assert.Contains("Police Absente", _out.ToString());
        Assert.Contains("police du thème", _out.ToString());
        Assert.Equal("Police Absente", Reload().Appearance!.Font);
    }

    [Fact]
    public void Run_InstalledFont_NoWarning()
    {
        Run("font=Segoe UI");

        Assert.DoesNotContain("Attention", _out.ToString());
    }

    [Fact]
    public void Run_BackgroundFiles_AreStoredWithTheirFullPathAndKind()
    {
        var gif = Path.Combine(_dir, "fond.gif");
        File.WriteAllBytes(gif, [1]);

        Assert.Equal(0, Run($"background=gif:{gif}"));
        Assert.Equal(new BackgroundSettings(BackgroundKind.Animated, Path: gif), Reload().Appearance!.Background);

        var video = Path.Combine(_dir, "fond.mp4");
        File.WriteAllBytes(video, [1]);
        Assert.Equal(0, Run($"background=video:{video}"));
        Assert.Equal(new BackgroundSettings(BackgroundKind.Video, Path: video), Reload().Appearance!.Background);

        Assert.Equal(0, Run("background=theme"));
        Assert.Null(Reload().Appearance!.Background);
    }

    [Fact]
    public void Run_NoArguments_ShowsSettingsAndFontsWithoutWriting()
    {
        var code = Run();

        Assert.Equal(0, code);
        Assert.Contains("classique", _out.ToString());
        Assert.Contains("Segoe UI", _out.ToString());
        Assert.Contains("Arial", _out.ToString());
        Assert.False(File.Exists(LayoutPath));
    }

    [Fact]
    public void Run_UnreadableLayout_IsNotOverwritten()
    {
        File.WriteAllText(LayoutPath, "{ pas du json");

        var code = Run("theme=pixel");

        Assert.Equal(1, code);
        Assert.Equal("{ pas du json", File.ReadAllText(LayoutPath));
    }
}
