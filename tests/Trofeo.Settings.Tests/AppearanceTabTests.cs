using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Personnalisation sans fenêtre : un faux lien, un vrai fichier dans un dossier temporaire, un faux sélecteur de fichier.</summary>
public sealed class AppearanceTabTests : IDisposable
{
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-appearance-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly FakeFilePicker _picker = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;

    public AppearanceTabTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModulePlacement Clock() =>
        new(ClockId, "clock", 12, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    private static readonly string[] SystemFonts = ["Arial", "Segoe UI", "Verdana"];

    private AppearanceTabViewModel Open(AppearanceSettings? appearance = null)
    {
        _store.Save(DashboardLayout.SingleView(180, [Clock()], appearance: appearance));
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        return new AppearanceTabViewModel(_session, _picker, SystemFonts);
    }

    private AppearanceSettings Saved => _document.Current.Appearance ?? AppearanceSettings.Default;

    private AppearanceSettings OnDisk => _store.Load().Appearance ?? AppearanceSettings.Default;

    private void ReloadWith(params string[] warnings) =>
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, warnings));

    // ---- Thème ----

    [Fact]
    public void Themes_AreThreeCards_WithTheirSentence()
    {
        var tab = Open();

        Assert.Equal(["Classique", "Liquid Glass", "Pixel"], tab.Themes.Select(t => t.Name));
        Assert.Equal(
            ["Panneaux mats, typo de tableau de bord.", "Verre dépoli sur un fond de halos.", "Blocs nets et typo pixel."],
            tab.Themes.Select(t => t.Description));
        Assert.Equal(["classique", "liquid-glass", "pixel"], tab.Themes.Select(t => t.Id));
        Assert.Equal([true, false, false], tab.Themes.Select(t => t.IsSelected));
    }

    [Fact]
    public async Task Theme_Change_SavesAppearanceOnly()
    {
        var tab = Open(new AppearanceSettings("classique", 0.5f, "Manrope"));
        var before = _document.Current;

        await tab.SetThemeAsync("pixel");

        Assert.Equal(new AppearanceSettings("pixel", 0.5f, "Manrope"), OnDisk);   // le reste de l'apparence est gardé
        Assert.Equal(before.Views[0].Placements, _document.Current.Views[0].Placements);             // Placements et Shortcuts inchangés
        Assert.Same(before.Shortcuts, _document.Current.Shortcuts);
        Assert.Equal(before.Rotation, _document.Current.Rotation);
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal([false, false, true], tab.Themes.Select(t => t.IsSelected));
        Assert.True(_session.CanUndo);
    }

    [Fact]
    public async Task Theme_Unchanged_SavesNothing()
    {
        var tab = Open();

        await tab.SetThemeAsync("classique");

        Assert.Equal(0, _link.ReloadCount);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task Theme_Unknown_IsRefused()
    {
        var tab = Open();

        await tab.SetThemeAsync("néon");

        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Theme_BackToDefaultSettings_LeavesNoAppearanceKey()
    {
        var tab = Open();
        await tab.SetThemeAsync("pixel");
        await tab.SetThemeAsync("classique");

        Assert.Null(_document.Current.Appearance);   // « aucun réglage » : la clé disparaît du fichier
    }

    // ---- Transparence ----

    [Fact]
    public async Task Opacity_ThemeDefault_WritesNull()
    {
        var tab = Open(new AppearanceSettings("classique", 0.5f));
        Assert.False(tab.IsOpacityFromTheme);
        Assert.Equal(50, tab.OpacityPercent);

        await tab.UseThemeOpacityAsync();

        Assert.Null(OnDisk.PanelOpacity);
        Assert.True(tab.IsOpacityFromTheme);
        Assert.Equal(86, tab.OpacityPercent);     // l'opacité du thème Classique s'affiche sur le curseur
    }

    [Fact]
    public async Task Opacity_Dragging_SavesNothing_UntilReleased_AsOneUndoStep()
    {
        var tab = Open();
        Assert.True(tab.IsOpacityFromTheme);

        tab.OpacityPercent = 20;
        tab.OpacityPercent = 30;
        tab.OpacityPercent = 40;
        Assert.Equal(0, _link.ReloadCount);               // pendant le glissement : rien
        Assert.Null(Saved.PanelOpacity);
        Assert.Equal("40 %", tab.OpacityText);

        await tab.CommitOpacityAsync();

        Assert.Equal(0.4f, OnDisk.PanelOpacity);
        Assert.Equal(1, _link.ReloadCount);
        Assert.False(tab.IsOpacityFromTheme);

        await _session.UndoAsync();                       // un seul pas d'annulation
        Assert.Null(OnDisk.PanelOpacity);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task Opacity_Release_AtTheSameValue_SavesNothing()
    {
        var tab = Open(new AppearanceSettings("classique", 0.5f));

        await tab.CommitOpacityAsync();

        Assert.Equal(0, _link.ReloadCount);
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(100, 1f)]
    [InlineData(35, 0.35f)]
    public async Task Opacity_Range_IsZeroToOneHundredPercent(int percent, float expected)
    {
        var tab = Open();

        tab.OpacityPercent = percent;
        await tab.CommitOpacityAsync();

        Assert.Equal(expected, OnDisk.PanelOpacity);
    }

    [Fact]
    public async Task Opacity_ChangedElsewhere_MovesTheSlider_ButNotWhileDragging()
    {
        var tab = Open(new AppearanceSettings("classique", 0.5f));

        await _session.ApplyAsync(l => l with { Appearance = new AppearanceSettings("classique", 0.7f) });   // Ctrl+Z, autre onglet…
        Assert.Equal(70, tab.OpacityPercent);

        tab.OpacityPercent = 10;                                                                              // l'utilisateur tire
        await _session.ApplyAsync(l => l with { Rotation = 0 });                                              // une autre modification passe
        Assert.Equal(10, tab.OpacityPercent);                                                                 // le curseur n'est pas ramené
    }

    // ---- Police ----

    [Fact]
    public void Font_Embedded_ListedFirst()
    {
        var tab = Open();

        var names = tab.Fonts.Select(f => f.IsSeparator ? "—" : f.Name).ToList();
        Assert.Equal(
            ["Celle du thème", "Barlow Semi Condensed", "Manrope", "Pixelify Sans", "VT323", "—", "Arial", "Segoe UI", "Verdana"],
            names);
        Assert.Equal("Celle du thème", tab.Fonts[0].Name);
        Assert.True(tab.Fonts[0].IsThemeDefault);
        Assert.Null(tab.Fonts[0].Value);
        Assert.True(tab.Fonts[5].IsSeparator);
        Assert.Equal(tab.Fonts[0], tab.SelectedFont);   // aucun réglage : « Celle du thème »
        // Chaque nom est écrit dans sa propre police.
        Assert.Equal("Manrope", tab.Fonts[2].FontFamily.Name);
        Assert.Equal("Verdana", tab.Fonts[8].FontFamily.Name);
    }

    [Fact]
    public async Task Font_Choose_SavesFamily_AndThemeDefaultWritesNull()
    {
        var tab = Open();

        await tab.SetFontAsync(tab.Fonts.Single(f => f.Name == "VT323"));
        Assert.Equal("VT323", OnDisk.Font);
        Assert.Equal("VT323", tab.SelectedFont!.Name);

        await tab.SetFontAsync(tab.Fonts.Single(f => f.Name == "Verdana"));
        Assert.Equal("Verdana", OnDisk.Font);

        await tab.SetFontAsync(tab.Fonts[0]);
        Assert.Null(OnDisk.Font);
        Assert.Equal(3, _link.ReloadCount);
    }

    [Fact]
    public async Task Font_Separator_IsNotAChoice()
    {
        var tab = Open();

        await tab.SetFontAsync(tab.Fonts.Single(f => f.IsSeparator));

        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public void Font_NotInstalled_SelectsNothing()
    {
        var tab = Open(new AppearanceSettings("classique", Font: "Comic Papyrus"));

        Assert.Null(tab.SelectedFont);
    }

    [Fact]
    public void Font_SavedIgnoringCase_IsSelected()
    {
        var tab = Open(new AppearanceSettings("classique", Font: "manrope"));

        Assert.Equal("Manrope", tab.SelectedFont!.Name);
    }

    // ---- Fond ----

    [Fact]
    public async Task Background_Theme_WritesNull()
    {
        var tab = Open(new AppearanceSettings("classique", Background: new BackgroundSettings(BackgroundKind.Color, Color: "#112233")));
        Assert.Equal(BackgroundKind.Color, tab.BackgroundKind);

        await tab.SetBackgroundKindAsync(BackgroundKind.Theme);

        Assert.Null(OnDisk.Background);
        Assert.Equal(BackgroundKind.Theme, tab.BackgroundKind);
    }

    [Fact]
    public async Task Background_Color_ValidatesHex()
    {
        var tab = Open();
        await tab.SetBackgroundKindAsync(BackgroundKind.Color);
        Assert.True(tab.ShowsColor);

        await tab.SetColorTextAsync("#12345");

        Assert.Equal("Couleur invalide : écris-la comme #1A2B3C.", tab.ColorError);
        Assert.True(tab.HasColorError);
        Assert.Equal(0, _link.ReloadCount);
        Assert.Null(OnDisk.Background);

        await tab.SetColorTextAsync("#1a2b3c");

        Assert.Null(tab.ColorError);
        Assert.Equal(new BackgroundSettings(BackgroundKind.Color, Color: "#1A2B3C"), OnDisk.Background);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    [InlineData("rouge")]
    [InlineData("#123")]
    public async Task Background_Color_RefusesAnythingButSixDigits(string text)
    {
        var tab = Open();

        await tab.SetColorTextAsync(text);

        Assert.True(tab.HasColorError);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Background_Color_AcceptsWithoutHash_AndSpaces()
    {
        var tab = Open();

        await tab.SetColorTextAsync("  0e1116 ");

        Assert.Equal("#0E1116", OnDisk.Background!.Color);
    }

    [Fact]
    public async Task Background_Palette_HasEightHues_AndClickSaves()
    {
        var tab = Open();
        Assert.Equal(8, tab.Palette.Count);
        Assert.Equal(8, tab.Palette.Select(p => p.Hex).Distinct().Count());
        Assert.All(tab.Palette, p => Assert.Matches("^#[0-9A-F]{6}$", p.Hex));

        await tab.SetColorAsync(tab.Palette[3].Hex);

        Assert.Equal(new BackgroundSettings(BackgroundKind.Color, Color: tab.Palette[3].Hex), OnDisk.Background);
        Assert.True(tab.Palette[3].IsSelected);
        Assert.Equal(tab.Palette[3].Hex, tab.ColorText);
    }

    [Fact]
    public async Task Background_ChoosingAKind_SavesNothingUntilAValueIsPicked()
    {
        var tab = Open();

        await tab.SetBackgroundKindAsync(BackgroundKind.Image);

        Assert.Equal(BackgroundKind.Image, tab.BackgroundKind);   // le choix s'affiche…
        Assert.True(tab.ShowsFile);
        Assert.Equal(0, _link.ReloadCount);                       // …mais le fond ne change qu'avec un fichier
        Assert.Null(OnDisk.Background);
    }

    [Theory]
    [InlineData(BackgroundKind.Image, "png", "jpg", "jpeg", "webp", "bmp")]
    [InlineData(BackgroundKind.Animated, "gif", "webp")]
    [InlineData(BackgroundKind.Video, "mp4", "m4v", "mov", "wmv", "avi")]
    public async Task Background_File_AsksThePickerForTheKindExtensions_AndSavesThePath(BackgroundKind kind, params string[] extensions)
    {
        var tab = Open();
        var file = Path.Combine(_dir, "fond.bin");
        _picker.Result = file;
        await tab.SetBackgroundKindAsync(kind);

        await tab.ChooseFileAsync();

        Assert.Equal(extensions, _picker.Extensions);
        Assert.Equal(new BackgroundSettings(kind, Path: file), OnDisk.Background);
        Assert.Equal("fond.bin", tab.FileName);
        Assert.True(tab.HasFileName);
    }

    [Fact]
    public async Task Background_File_PickerCancelled_ChangesNothing()
    {
        var tab = Open();
        _picker.Result = null;
        await tab.SetBackgroundKindAsync(BackgroundKind.Image);

        await tab.ChooseFileAsync();

        Assert.Equal(1, _picker.Calls);
        Assert.Equal(0, _link.ReloadCount);
        Assert.False(tab.HasFileName);
    }

    [Fact]
    public async Task Background_Video_ShowsAdvice()
    {
        var tab = Open();
        Assert.False(tab.ShowsVideoAdvice);

        await tab.SetBackgroundKindAsync(BackgroundKind.Video);
        Assert.True(tab.ShowsVideoAdvice);
        Assert.Equal(
            "Une vidéo à la taille de l'écran (1920 × 462) coûte deux fois moins de processeur qu'une vidéo 1080p.",
            Texts.AppearanceVideoAdvice);

        await tab.SetBackgroundKindAsync(BackgroundKind.Image);
        Assert.False(tab.ShowsVideoAdvice);
    }

    [Fact]
    public void Background_Video_SavedAlready_ShowsAdviceAndFileName()
    {
        var tab = Open(new AppearanceSettings("classique", Background: new BackgroundSettings(BackgroundKind.Video, Path: @"C:\fonds\boucle.mp4")));

        Assert.True(tab.ShowsVideoAdvice);
        Assert.Equal(@"boucle.mp4", Path.GetFileName(tab.FileName!.Replace('\\', '/')));
    }

    [Fact]
    public async Task Background_FromFileToTheme_OldFileNameGoes()
    {
        var tab = Open(new AppearanceSettings("classique", Background: new BackgroundSettings(BackgroundKind.Image, Path: "/a/b/photo.png")));
        Assert.Equal("photo.png", tab.FileName);

        await tab.SetBackgroundKindAsync(BackgroundKind.Theme);

        Assert.False(tab.HasFileName);
    }

    // ---- Garde de lisibilité ----

    [Fact]
    public async Task Legibility_Off_WritesFalse()
    {
        var tab = Open();
        Assert.True(tab.IsLegibilityOn);

        await tab.SetLegibilityAsync(false);

        Assert.False(OnDisk.Legibility!.Value);
        Assert.False(tab.IsLegibilityOn);

        await tab.SetLegibilityAsync(true);

        Assert.True(OnDisk.Legibility!.Value);
        Assert.True(tab.IsLegibilityOn);
    }

    // ---- Avertissements du rechargement ----

    private const string BackgroundMissing = "Fond d'écran introuvable : C:\\fonds\\photo.png. Fond du thème utilisé.";
    private const string FontMissing = "Police « Comic Papyrus » introuvable : police du thème utilisée.";

    [Fact]
    public async Task ReloadWarning_ShownUnderBackground()
    {
        var tab = Open();
        ReloadWith(BackgroundMissing);
        _picker.Result = @"C:\fonds\photo.png";
        await tab.SetBackgroundKindAsync(BackgroundKind.Image);

        await tab.ChooseFileAsync();

        Assert.Equal(BackgroundMissing, tab.BackgroundWarning);
        Assert.True(tab.HasBackgroundWarning);
        Assert.False(tab.HasFontWarning);
        Assert.False(tab.HasThemeWarning);
        Assert.Empty(_session.OtherWarnings);   // rien dans la ligne de la fenêtre
    }

    [Fact]
    public async Task ReloadWarning_Font_ShownUnderFontList()
    {
        var tab = Open();
        ReloadWith(FontMissing);

        await tab.SetFontAsync(new FontItem("Comic Papyrus", "Comic Papyrus"));

        Assert.Equal(FontMissing, tab.FontWarning);
        Assert.False(tab.HasBackgroundWarning);
    }

    [Fact]
    public async Task ReloadWarning_Theme_ShownUnderThemeCards()
    {
        var tab = Open();
        ReloadWith("Thème « néon » inconnu : thème Classique utilisé.");

        await tab.SetThemeAsync("pixel");

        Assert.Equal("Thème « néon » inconnu : thème Classique utilisé.", tab.ThemeWarning);
    }

    [Fact]
    public async Task ReloadWarning_StaysAfterAModuleEdit_UntilTheAppearanceChanges()
    {
        var tab = Open();
        ReloadWith(BackgroundMissing);
        _picker.Result = @"C:\fonds\photo.png";
        await tab.SetBackgroundKindAsync(BackgroundKind.Image);
        await tab.ChooseFileAsync();

        // Le service ne répète pas l'avertissement quand l'apparence ne change pas : une modification de module l'efface pas.
        ReloadWith();
        await _session.ApplyAsync(l => l.WithPlacements(l.Views[0].Id, [Clock() with { TextSize = TextSize.Large }]));
        Assert.Equal(BackgroundMissing, tab.BackgroundWarning);

        // Annuler cette modification de module (apparence inchangée) ne l'efface pas non plus.
        await _session.UndoAsync();
        Assert.Equal(BackgroundMissing, tab.BackgroundWarning);

        // Un changement d'apparence sans avertissement l'efface.
        await tab.SetBackgroundKindAsync(BackgroundKind.Theme);
        Assert.False(tab.HasBackgroundWarning);
    }

    [Fact]
    public async Task ReloadWarning_AppearanceUnchangedInTheWindow_ButServiceRebuiltIt_IsShown()
    {
        // Le service redémarré (ou le fichier réécrit à la main) reconstruit le fond au premier rechargement, même pour une
        // modification de module : son avertissement ne doit pas être perdu.
        var tab = Open(new AppearanceSettings("classique", Background: new BackgroundSettings(BackgroundKind.Image, Path: @"C:\fonds\photo.png")));
        ReloadWith(BackgroundMissing);

        await _session.ApplyAsync(l => l.WithPlacements(l.Views[0].Id, [Clock() with { TextSize = TextSize.Large }]));

        Assert.Equal(BackgroundMissing, tab.BackgroundWarning);
    }

    [Fact]
    public async Task Background_File_PickerFails_ShowsTheFrenchMessage_AndSavesNothing()
    {
        var tab = Open();
        _picker.Failure = "Le sélecteur de fichier n'a pas pu s'ouvrir : accès refusé.";
        await tab.SetBackgroundKindAsync(BackgroundKind.Image);

        await tab.ChooseFileAsync();

        Assert.Equal("Le sélecteur de fichier n'a pas pu s'ouvrir : accès refusé.", tab.FileError);
        Assert.True(tab.HasFileError);
        Assert.Equal(0, _link.ReloadCount);

        _picker.Failure = null;                 // la prochaine tentative réussit : l'erreur s'efface
        _picker.Result = Path.Combine(_dir, "a.png");
        await tab.ChooseFileAsync();
        Assert.False(tab.HasFileError);
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public void Texts_PickFailed_IsFrench()
    {
        Assert.Equal("Le sélecteur de fichier n'a pas pu s'ouvrir : x", string.Format(Texts.AppearancePickFailed, "x"));
    }

    [Fact]
    public async Task ReloadWarning_Undo_OfAnAppearanceChange_TakesTheReloadWarnings()
    {
        var tab = Open();
        ReloadWith(BackgroundMissing);
        _picker.Result = @"C:\fonds\photo.png";
        await tab.SetBackgroundKindAsync(BackgroundKind.Image);
        await tab.ChooseFileAsync();
        Assert.True(tab.HasBackgroundWarning);

        ReloadWith();
        await _session.UndoAsync();                // l'apparence change (retour au fond du thème) : les avertissements aussi

        Assert.False(tab.HasBackgroundWarning);
    }

    [Fact]
    public async Task ReloadWarning_Other_GoesToTheWindowLevel()
    {
        var tab = Open();
        ReloadWith("Les raccourcis n'ont pas pu être mis à jour : accès refusé.", FontMissing);

        await tab.SetFontAsync(new FontItem("Comic Papyrus", "Comic Papyrus"));

        Assert.Equal(["Les raccourcis n'ont pas pu être mis à jour : accès refusé."], _session.OtherWarnings);
        Assert.Equal(FontMissing, tab.FontWarning);
    }

    [Fact]
    public async Task ReloadWarning_FileChangedOutside_ClearsThem()
    {
        var tab = Open();
        ReloadWith(FontMissing);
        await tab.SetFontAsync(new FontItem("Comic Papyrus", "Comic Papyrus"));
        Assert.True(tab.HasFontWarning);

        _store.Save(DashboardLayout.SingleView(180, [Clock()]));                 // quelqu'un réécrit le fichier
        await _session.ApplyAsync(l => l with { Rotation = 0 });          // la fenêtre le relit au lieu de l'écraser

        Assert.False(tab.HasFontWarning);
    }

    [Theory]
    [InlineData("Fond d'écran introuvable : x.png. Fond du thème utilisé.", "background")]
    [InlineData("Fond d'écran illisible : x.png (codec). Fond du thème utilisé.", "background")]
    [InlineData("Couleur de fond invalide : rouge. Fond du thème utilisé.", "background")]
    [InlineData("Vidéo non prise en charge ici : x.mp4. Fond du thème utilisé.", "background")]
    [InlineData("Réglage du décodeur vidéo refusé pour x.mp4 (0x1). Le décodeur garde ses réglages par défaut.", "background")]
    [InlineData("Lecture de la vidéo interrompue : x.mp4 (erreur). L'image reste figée.", "background")]
    [InlineData("Police « Comic » introuvable : police du thème utilisée.", "font")]
    [InlineData("Thème « néon » inconnu : thème Classique utilisé.", "theme")]
    [InlineData("Opacité des modules invalide : thème par défaut utilisé.", "opacity")]
    [InlineData("Les raccourcis n'ont pas pu être mis à jour : refusé.", "other")]
    [InlineData("Module « clock » ignoré : type inconnu.", "other")]
    public void WarningRouter_MatchesEachWarningToItsSettingByContent(string warning, string expected)
    {
        var (appearance, other) = WarningRouter.Route([warning]);

        var where = new[]
        {
            appearance.Theme.Count > 0 ? "theme" : null, appearance.Opacity.Count > 0 ? "opacity" : null,
            appearance.Font.Count > 0 ? "font" : null, appearance.Background.Count > 0 ? "background" : null,
            other.Count > 0 ? "other" : null,
        }.Where(w => w is not null).ToList();
        Assert.Equal([expected], where);
    }

    // ---- Message du service, au niveau de la fenêtre ----

    [Fact]
    public async Task RefusedSave_FromPersonnalisation_ShowsTheWindowLevelMessage()
    {
        Open();
        using var model = new MainViewModel(_link, _document, new EditorPreferences(FirstRunDone: true));
        _link.OnReload = () => Task.FromResult(new Reloaded(false, "Le service n'a pas pu appliquer le fond.", []));

        await model.AppearanceTab.SetThemeAsync("pixel");

        Assert.Equal("Le service n'a pas pu appliquer le fond.", model.SaveMessage);
        Assert.True(model.HasSaveMessage);

        // La prochaine modification réussie l'efface, de quelque onglet qu'elle vienne.
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, []));
        await model.AppearanceTab.SetThemeAsync("liquid-glass");
        Assert.False(model.HasSaveMessage);
    }

    [Fact]
    public async Task OtherWarnings_AreShownInTheWindowLevelLine()
    {
        Open();
        using var model = new MainViewModel(_link, _document, new EditorPreferences(FirstRunDone: true));
        ReloadWith("Les raccourcis n'ont pas pu être mis à jour : accès refusé.");

        await model.AppearanceTab.SetThemeAsync("pixel");

        Assert.Equal("Les raccourcis n'ont pas pu être mis à jour : accès refusé.", model.SaveMessage);
    }

    [AvaloniaFact]
    public async Task Window_ShowsTheAmberLine_OnThePersonnalisationTab()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        _store.Save(DashboardLayout.SingleView(180, [Clock()]));
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        using var model = new MainViewModel(_link, _document, new EditorPreferences(FirstRunDone: true));
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            model.SelectedTab = model.AppearanceTab;
            _link.OnReload = () => Task.FromResult(new Reloaded(false, "Le service n'a pas pu appliquer le fond.", []));

            await model.AppearanceTab.SetThemeAsync("pixel");

            var line = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SaveMessage");
            Assert.True(line.IsEffectivelyVisible);
            Assert.Equal("Le service n'a pas pu appliquer le fond.", line.Text);
            Assert.Contains("warning", line.Classes);
            Assert.IsType<AppearanceTabView>(window.GetVisualDescendants().OfType<AppearanceTabView>().Single());
        }
        finally
        {
            window.Close();
        }
    }
}
