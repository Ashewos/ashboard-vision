using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.Tests;

public sealed class LayoutDocumentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly LayoutStore _store;
    private readonly FakeServiceLink _link = new();

    public LayoutDocumentTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "layout.json");
        _store = new LayoutStore(_file);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Document_ExternalChange_ReloadsInsteadOfOverwriting()
    {
        _store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        var replaced = 0;
        document.Replaced += () => replaced++;

        // Quelqu'un d'autre réécrit le fichier (rotation 0) entre Load et SaveAsync.
        var elsewhere = DashboardLayout.Default with { Rotation = 0 };
        _store.Save(elsewhere);
        var onDisk = File.ReadAllText(_file);

        var outcome = await document.SaveAsync(DashboardLayout.Default with { MaxFps = 7 });

        Assert.False(outcome.Saved);
        Assert.Equal("La disposition a changé en dehors des réglages : elle a été relue.", outcome.Message);
        Assert.Equal(1, replaced);
        Assert.Equal(onDisk, File.ReadAllText(_file));
        Assert.Equal(0, document.Current.Rotation);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Document_Save_WritesThenAsksServiceToReload()
    {
        _store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, ["Le fond a été remplacé."]));

        var layout = DashboardLayout.Default with { MaxFps = 7 };
        var outcome = await document.SaveAsync(layout);

        Assert.True(outcome.Saved);
        Assert.Null(outcome.Message);
        Assert.Equal(["Le fond a été remplacé."], outcome.Warnings);
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal(7, _store.Load().MaxFps);
        Assert.Equal(7, document.Current.MaxFps);

        // La seconde modification part de l'empreinte que la première a laissée : pas de fausse alerte.
        var second = await document.SaveAsync(layout with { MaxFps = 8 });
        Assert.True(second.Saved);
    }

    [Fact]
    public async Task Document_ServiceRefuses_ReportsReason()
    {
        _store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        _link.OnReload = () => Task.FromResult(new Reloaded(false, "Type de module inconnu : « meteo ».", []));

        var outcome = await document.SaveAsync(DashboardLayout.Default with { MaxFps = 5 });

        Assert.True(outcome.Saved);
        Assert.Equal("Type de module inconnu : « meteo ».", outcome.Message);
    }

    [Fact]
    public async Task Document_ServiceUnreachable_StillSaves_AndSaysWhy()
    {
        _store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        _link.OnReload = () => throw new ControlException("La connexion au service est coupée.");

        var outcome = await document.SaveAsync(DashboardLayout.Default with { MaxFps = 5 });

        Assert.True(outcome.Saved);
        Assert.Equal("La connexion au service est coupée.", outcome.Message);
        Assert.Equal(5, _store.Load().MaxFps);
    }

    // ---- Fichier illisible à l'ouverture ----

    private const string Garbage = "{ ceci n'est pas du JSON";

    [Fact]
    public async Task Save_WhileTheFileIsUnreadable_RefusesAndLeavesTheFileUntouched()
    {
        File.WriteAllText(_file, Garbage);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        Assert.NotNull(document.LoadError);

        var outcome = await document.SaveAsync(DashboardLayout.Default with { MaxFps = 7 });

        Assert.False(outcome.Saved);
        Assert.StartsWith("layout.json est illisible : ", outcome.Message);
        Assert.EndsWith(". Corrige-le, ou repars de la disposition par défaut.", outcome.Message);
        Assert.Equal(Garbage, File.ReadAllText(_file));
        Assert.False(File.Exists(_file + ".bad"));
        Assert.Equal(0, _link.ReloadCount);
        Assert.NotNull(document.LoadError);
    }

    [Fact]
    public async Task Save_TheFileBecomesReadable_RereadsInsteadOfWritingTheStaleLayout()
    {
        File.WriteAllText(_file, Garbage); // un verrou passager, ou une faute que l'utilisateur corrige entre-temps
        var document = new LayoutDocument(_store, _link);
        document.Load();
        var replaced = 0;
        document.Replaced += () => replaced++;
        _store.Save(DashboardLayout.Default with { Rotation = 0 });
        var onDisk = File.ReadAllText(_file);

        var outcome = await document.SaveAsync(DashboardLayout.Default with { MaxFps = 7 }); // calculée sur la disposition par défaut

        Assert.False(outcome.Saved);
        Assert.Equal(Texts.LayoutChangedOutside, outcome.Message);
        Assert.Null(document.LoadError);
        Assert.Equal(0, document.Current.Rotation);
        Assert.Equal(1, replaced);
        Assert.Equal(onDisk, File.ReadAllText(_file));

        // La modification suivante part de la disposition relue.
        var next = await document.SaveAsync(document.Current with { MaxFps = 7 });
        Assert.True(next.Saved);
        Assert.Equal(7, _store.Load().MaxFps);
    }

    [Fact]
    public async Task ResetToDefault_CopiesTheFileFirst_ThenSavesTheDefault()
    {
        File.WriteAllText(_file, Garbage);
        var document = new LayoutDocument(_store, _link);
        document.Load();

        var outcome = await document.ResetToDefaultAsync();

        Assert.True(outcome.Saved);
        Assert.Null(outcome.Message);
        Assert.Equal(Garbage, File.ReadAllText(_file + ".avant-reglages"));
        Assert.Equal(DashboardLayout.Default.Views[0].Placements.Count, _store.Load().Views[0].Placements.Count);
        Assert.Null(document.LoadError);
        Assert.Equal(1, _link.ReloadCount);

        // L'empreinte suit l'écriture : la modification suivante n'est pas prise pour un changement extérieur.
        Assert.True((await document.SaveAsync(document.Current with { MaxFps = 6 })).Saved);
    }

    [Fact]
    public async Task ResetToDefault_WhenNothingIsWrong_DoesNothing()
    {
        _store.Save(DashboardLayout.Default with { Rotation = 0 });
        var document = new LayoutDocument(_store, _link);
        document.Load();
        var onDisk = File.ReadAllText(_file);

        var outcome = await document.ResetToDefaultAsync();

        Assert.False(outcome.Saved);
        Assert.Equal(onDisk, File.ReadAllText(_file));
        Assert.False(File.Exists(_file + ".avant-reglages"));
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task ResetToDefault_TheFileWasFixedMeanwhile_RereadsInsteadOfOverwriting()
    {
        File.WriteAllText(_file, Garbage);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        _store.Save(DashboardLayout.Default with { Rotation = 0 });
        var onDisk = File.ReadAllText(_file);

        var outcome = await document.ResetToDefaultAsync();

        Assert.False(outcome.Saved);
        Assert.Equal(Texts.LayoutChangedOutside, outcome.Message);
        Assert.Equal(onDisk, File.ReadAllText(_file));
        Assert.Equal(0, document.Current.Rotation);
        Assert.Null(document.LoadError);
    }

    [Fact]
    public async Task ResetToDefault_WhenTheCopyFails_KeepsTheFile()
    {
        File.WriteAllText(_file, Garbage);
        Directory.CreateDirectory(_file + ".avant-reglages"); // la copie ne peut pas s'y écrire
        var document = new LayoutDocument(_store, _link);
        document.Load();

        var outcome = await document.ResetToDefaultAsync();

        Assert.False(outcome.Saved);
        Assert.Contains("Impossible d'écrire la disposition", outcome.Message);
        Assert.Equal(Garbage, File.ReadAllText(_file));
        Assert.NotNull(document.LoadError);
    }

    // ---- Fichier absent à l'ouverture (premier lancement du service) ----

    [Fact]
    public async Task Save_TheServiceWroteTheDefaultAfterTheOpening_IsNotAnExternalChange()
    {
        var document = new LayoutDocument(_store, _link);
        document.Load(); // pas de layout.json : la disposition par défaut
        Assert.False(File.Exists(_file));
        var replaced = 0;
        document.Replaced += () => replaced++;
        _store.Save(DashboardLayout.Default); // le service démarré depuis la fenêtre écrit le défaut

        var outcome = await document.SaveAsync(DashboardLayout.Default with { MaxFps = 7 });

        Assert.True(outcome.Saved);
        Assert.Equal(0, replaced);
        Assert.Equal(7, _store.Load().MaxFps);
    }

    [Fact]
    public async Task Save_AFileWithOtherContentAppearedAfterTheOpening_IsAnExternalChange()
    {
        var document = new LayoutDocument(_store, _link);
        document.Load();
        _store.Save(DashboardLayout.Default with { Rotation = 0 });
        var onDisk = File.ReadAllText(_file);

        var outcome = await document.SaveAsync(DashboardLayout.Default with { MaxFps = 7 });

        Assert.False(outcome.Saved);
        Assert.Equal(Texts.LayoutChangedOutside, outcome.Message);
        Assert.Equal(onDisk, File.ReadAllText(_file));
        Assert.Equal(0, document.Current.Rotation);
    }
}
