using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace Trofeo.Display.Tests;

public sealed class LoopConfigFactoryTests
{
    /// <summary>Aucune police installée.</summary>
    private sealed class NoSystemFonts : ISystemFonts
    {
        public IReadOnlyList<string> Families => [];
        public SKTypeface? Match(string family) => null;
    }

    private static DashboardLayout With(AppearanceSettings? appearance) =>
        LoopRig.Layout(LoopRig.Place(LoopRig.A)) with { Appearance = appearance };

    [Fact]
    public void Build_DefaultAppearance_IsClassiqueWithoutWarning()
    {
        var warnings = new List<string>();
        var layout = With(null);

        var config = LoopConfigFactory.Build(layout, new NoSystemFonts(), null, warnings.Add);
        using var background = config.Background;

        Assert.Same(layout, config.Layout);
        Assert.Same(Themes.ClassiqueDefinition, config.Theme);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Build_UnknownFont_WarnsAndKeepsTheTheme()
    {
        var warnings = new List<string>();

        var config = LoopConfigFactory.Build(
            With(new AppearanceSettings("pixel", Font: "PoliceIntrouvable")), new NoSystemFonts(), null, warnings.Add);
        using var background = config.Background;

        Assert.Contains(warnings, w => w.Contains("PoliceIntrouvable"));
        Assert.Equal("pixel", config.Theme.Theme.Id);
    }

    [Fact]
    public void Build_MissingImage_WarnsAndFallsBackToTheThemeBackground()
    {
        var warnings = new List<string>();
        var missing = Path.Combine(Path.GetTempPath(), "trofeo-absent", "fond.png");
        var appearance = new AppearanceSettings(Background: new BackgroundSettings(BackgroundKind.Image, Path: missing));

        var config = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), null, warnings.Add);
        using var background = config.Background;

        Assert.Contains(warnings, w => w.Contains("fond.png"));
        Assert.NotNull(config.Background);
    }

    [Fact]
    public void Build_Video_UsesTheOpenerAndGivesOwnershipToTheConfig()
    {
        var opened = new List<string>();
        var video = new TrackedBackground(SKColors.Red);
        var appearance = new AppearanceSettings(Background: new BackgroundSettings(BackgroundKind.Video, Path: "clip.mp4"));

        var config = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), (path, _) => { opened.Add(path); return video; }, _ => { });

        Assert.Equal(["clip.mp4"], opened);
        Assert.Same(video, config.Background);
        Assert.False(video.Disposed);
        config.Background.Dispose();
    }

    [Fact]
    public void Build_VideoWithoutOpener_WarnsNotSupported()
    {
        var warnings = new List<string>();
        var appearance = new AppearanceSettings(Background: new BackgroundSettings(BackgroundKind.Video, Path: "clip.mp4"));

        var config = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), null, warnings.Add);
        using var background = config.Background;

        Assert.Single(warnings);
    }

    private static readonly BackgroundSettings VideoSettings = new(BackgroundKind.Video, Path: "clip.mp4");

    /// <summary>Ouvre des vidéos qui se décodent sur la carte demandée au moment de l'ouverture ; note chaque ouverture (chemin, position).</summary>
    private sealed class VideoOpener
    {
        public List<(string Path, TimeSpan StartAt)> Opened { get; } = [];
        public List<SeekableVideo> Videos { get; } = [];
        public TimeSpan PositionOfNext { get; set; } = TimeSpan.FromSeconds(1.5);
        public long? LuidOfNext { get; set; }
        public long? RefusedOfNext { get; set; }

        public IBackground? Open(string path, TimeSpan startAt)
        {
            Opened.Add((path, startAt));
            var video = new SeekableVideo(PositionOfNext, LuidOfNext, RefusedOfNext);
            Videos.Add(video);
            return video;
        }
    }

    [Fact]
    public void AccelerationChange_ReopensVideoAtPosition()
    {
        // La vidéo se décode sur le processeur ; les réglages donnent maintenant la carte 42 : elle est rouverte, là où elle en est.
        var opener = new VideoOpener { PositionOfNext = TimeSpan.FromSeconds(12.5), LuidOfNext = null };
        var appearance = new AppearanceSettings(Background: VideoSettings);
        var current = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { });
        opener.PositionOfNext = TimeSpan.Zero;
        opener.LuidOfNext = 42;

        var next = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, current, videoDecoderLuid: 42);

        Assert.Equal(2, opener.Opened.Count);
        Assert.Equal(("clip.mp4", TimeSpan.Zero), opener.Opened[0]);
        Assert.Equal(("clip.mp4", TimeSpan.FromSeconds(12.5)), opener.Opened[1]); // la position de l'ancien fond
        Assert.NotSame(current.Background, next.Background);
        Assert.Same(opener.Videos[1], next.Background);
        Assert.False(next.BackgroundIsThemeFallback);
        Assert.False(opener.Videos[0].Disposed); // la boucle libère l'ancien fond, pas la fabrique
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Fact]
    public void AccelerationTurnedOff_ReopensVideoOnTheProcessorAtPosition()
    {
        var opener = new VideoOpener { PositionOfNext = TimeSpan.FromSeconds(3), LuidOfNext = 42 };
        var appearance = new AppearanceSettings(Background: VideoSettings);
        var current = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, videoDecoderLuid: 42);
        opener.LuidOfNext = null;

        var next = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, current, videoDecoderLuid: null);

        Assert.Equal(TimeSpan.FromSeconds(3), opener.Opened[1].StartAt);
        Assert.NotSame(current.Background, next.Background);
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(42L)]
    public void SameDecoder_KeepsVideo(long? luid)
    {
        // Le rechargement levé par le contrôleur après le changement de réglages trouve la vidéo déjà sur la bonne carte : elle reste.
        var opener = new VideoOpener { LuidOfNext = luid };
        var appearance = new AppearanceSettings(Background: VideoSettings);
        var current = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, videoDecoderLuid: luid);

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings(PanelOpacity: 0.4f, Background: VideoSettings)), new NoSystemFonts(), opener.Open, _ => { }, current,
            videoDecoderLuid: luid);

        Assert.Same(current.Background, next.Background);
        Assert.Single(opener.Opened);
        Assert.False(opener.Videos[0].Disposed);
        current.Background.Dispose();
    }

    [Fact]
    public void RefusedCard_VideoIsNotReopenedWhileTheWantedCardStaysTheSame()
    {
        // La carte 42 a refusé ce fichier (la vidéo se décode sur le processeur) : chaque rechargement n'y retourne pas.
        var opener = new VideoOpener { LuidOfNext = null, RefusedOfNext = 42 };
        var appearance = new AppearanceSettings(Background: VideoSettings);
        var current = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, videoDecoderLuid: 42);

        var next = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, current, videoDecoderLuid: 42);

        Assert.Same(current.Background, next.Background);
        Assert.Single(opener.Opened);
        Assert.False(opener.Videos[0].Disposed);
        current.Background.Dispose();
    }

    [Fact]
    public void RefusedCard_VideoIsReopenedAtPositionWhenTheWantedCardChanges()
    {
        var opener = new VideoOpener { LuidOfNext = null, RefusedOfNext = 42, PositionOfNext = TimeSpan.FromSeconds(6) };
        var appearance = new AppearanceSettings(Background: VideoSettings);
        var current = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, videoDecoderLuid: 42);
        opener.RefusedOfNext = null;
        opener.LuidOfNext = 7;

        var next = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), opener.Open, _ => { }, current, videoDecoderLuid: 7);

        Assert.Equal(2, opener.Opened.Count);
        Assert.Equal(TimeSpan.FromSeconds(6), opener.Opened[1].StartAt);
        Assert.NotSame(current.Background, next.Background);
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Fact]
    public void ThemeChange_KeepsVideo()
    {
        var opener = new VideoOpener { LuidOfNext = 42 };
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings("classique", Background: VideoSettings)), new NoSystemFonts(), opener.Open, _ => { }, videoDecoderLuid: 42);

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings("pixel", Background: VideoSettings)), new NoSystemFonts(), opener.Open, _ => { }, current, videoDecoderLuid: 42);

        Assert.Equal("pixel", next.Theme.Theme.Id);
        Assert.Same(current.Background, next.Background);
        Assert.Single(opener.Opened);
        current.Background.Dispose();
    }

    [Fact]
    public void DifferentDecoder_ButBackgroundChanged_OpensFromTheStart()
    {
        // Un autre fond (ici une autre vidéo) ne reprend pas la position de l'ancien.
        var opener = new VideoOpener { PositionOfNext = TimeSpan.FromSeconds(9) };
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings(Background: VideoSettings)), new NoSystemFonts(), opener.Open, _ => { });

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings(Background: new BackgroundSettings(BackgroundKind.Video, Path: "autre.mp4"))), new NoSystemFonts(),
            opener.Open, _ => { }, current, videoDecoderLuid: 42);

        Assert.Equal(("autre.mp4", TimeSpan.Zero), opener.Opened[1]);
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Fact]
    public void VideoThatFellBack_IsNotReopenedForTheCard()
    {
        // Le fond du thème (la vidéo n'a pas pu s'ouvrir) n'a pas de carte : il n'est pas refait pour une autre.
        Func<string, TimeSpan, IBackground?> refusing = (_, _) => null;
        var appearance = new AppearanceSettings(Background: VideoSettings);
        var current = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), refusing, _ => { });

        var next = LoopConfigFactory.Build(With(appearance), new NoSystemFonts(), refusing, _ => { }, current, videoDecoderLuid: 42);

        Assert.Same(current.Background, next.Background);
        current.Background.Dispose();
    }

    [Fact]
    public void Build_SameBackgroundAndThemeDefault_KeepsTheCurrentBackground()
    {
        var opened = 0;
        Func<string, TimeSpan, IBackground?> video = (_, _) => { opened++; return new TrackedBackground(SKColors.Red); };
        var backgroundSettings = new BackgroundSettings(BackgroundKind.Video, Path: "clip.mp4");
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings(Background: backgroundSettings)), new NoSystemFonts(), video, _ => { });

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings(PanelOpacity: 0.4f, Font: "Inconnue", Legibility: false, Background: backgroundSettings)),
            new NoSystemFonts(), video, _ => { }, current);

        Assert.Same(current.Background, next.Background);
        Assert.Equal(1, opened);
        Assert.Equal(0.4f, next.Theme.Theme.PanelOpacity);
        Assert.False(next.Theme.LegibilityGuard);
        current.Background.Dispose();
    }

    [Fact]
    public void Build_ThemeWithAnotherDefaultBackground_RebuildsTheBackground()
    {
        var current = LoopConfigFactory.Build(With(new AppearanceSettings("classique")), new NoSystemFonts(), null, _ => { });

        var next = LoopConfigFactory.Build(With(new AppearanceSettings("pixel")), new NoSystemFonts(), null, _ => { }, current);

        Assert.NotSame(current.Background, next.Background);
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Fact]
    public void Build_ChosenVideoAndThemeChanged_KeepsTheCurrentBackground()
    {
        var opened = 0;
        Func<string, TimeSpan, IBackground?> video = (_, _) => { opened++; return new TrackedBackground(SKColors.Red); };
        var backgroundSettings = new BackgroundSettings(BackgroundKind.Video, Path: "clip.mp4");
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings("classique", Background: backgroundSettings)), new NoSystemFonts(), video, _ => { });

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings("pixel", Background: backgroundSettings)), new NoSystemFonts(), video, _ => { }, current);

        Assert.NotEqual(current.Theme.DefaultBackground, next.Theme.DefaultBackground);
        Assert.Equal("pixel", next.Theme.Theme.Id);
        Assert.Same(current.Background, next.Background); // la vidéo choisie ne repart pas de zéro
        Assert.False(next.BackgroundIsThemeFallback);
        Assert.Equal(1, opened);
        current.Background.Dispose();
    }

    [Fact]
    public void Build_ChosenBackgroundThatFellBackAndThemeChanged_RebuildsTheBackground()
    {
        var missing = Path.Combine(Path.GetTempPath(), "trofeo-absent", "fond.png");
        var backgroundSettings = new BackgroundSettings(BackgroundKind.Image, Path: missing);
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings("classique", Background: backgroundSettings)), new NoSystemFonts(), null, _ => { });

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings("pixel", Background: backgroundSettings)), new NoSystemFonts(), null, _ => { }, current);

        Assert.NotEmpty(current.BackgroundWarnings); // le fond d'avant est le repli de l'ancien thème
        Assert.NotSame(current.Background, next.Background);
        Assert.NotEmpty(next.BackgroundWarnings);
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Fact]
    public void Build_ChosenVideoThatTheOpenerRefusedAndThemeChanged_RebuildsTheBackground()
    {
        // L'hôte vidéo averti seul et rend null : la fabrique n'ajoute aucun avertissement, mais le fond est le repli du thème.
        var warnings = new List<string>();
        Func<string, TimeSpan, IBackground?> refusing = (_, _) => null;
        var backgroundSettings = new BackgroundSettings(BackgroundKind.Video, Path: "clip.mp4");
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings("classique", Background: backgroundSettings)), new NoSystemFonts(), refusing, warnings.Add);

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings("liquid-glass", Background: backgroundSettings)), new NoSystemFonts(), refusing, warnings.Add, current);

        Assert.Empty(current.BackgroundWarnings);
        Assert.True(current.BackgroundIsThemeFallback);
        Assert.NotEqual(current.Theme.DefaultBackground, next.Theme.DefaultBackground);
        Assert.NotSame(current.Background, next.Background); // le repli de l'ancien thème n'est pas gardé
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Fact]
    public void Build_BackgroundSettingsChanged_RebuildsTheBackground()
    {
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings(Background: new BackgroundSettings(BackgroundKind.Color, Color: "#102030"))),
            new NoSystemFonts(), null, _ => { });

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings(Background: new BackgroundSettings(BackgroundKind.Color, Color: "#405060"))),
            new NoSystemFonts(), null, _ => { }, current);

        Assert.NotSame(current.Background, next.Background);
        current.Background.Dispose();
        next.Background.Dispose();
    }

    [Fact]
    public void Build_KeptBackground_RepeatsItsWarnings()
    {
        var missing = Path.Combine(Path.GetTempPath(), "trofeo-absent", "fond.png");
        var backgroundSettings = new BackgroundSettings(BackgroundKind.Image, Path: missing);
        var first = new List<string>();
        var current = LoopConfigFactory.Build(
            With(new AppearanceSettings(Background: backgroundSettings)), new NoSystemFonts(), null, first.Add);
        var second = new List<string>();

        var next = LoopConfigFactory.Build(
            With(new AppearanceSettings(PanelOpacity: 0.5f, Background: backgroundSettings)), new NoSystemFonts(), null, second.Add, current);

        Assert.Same(current.Background, next.Background);
        Assert.NotEmpty(first);
        Assert.Equal(first, second); // la fenêtre remplace ses avertissements d'apparence : le fond de repli reste signalé
        current.Background.Dispose();
    }
}
