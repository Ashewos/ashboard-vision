namespace Trofeo.Distribution.Tests;

public sealed class UpdatePreferencesStoreTests
{
    private static readonly AppVersion Stable = new(1, 0, 0);
    private static readonly AppVersion Beta = new(1, 0, 0, "beta.1");

    [Fact]
    public void Load_MissingOrCorrupt_ReturnsDefault()
    {
        using var dir = new TempDir();
        var path = dir.Combine("mises-a-jour.json");
        var store = new UpdatePreferencesStore(path);

        Assert.Equal(UpdatePreferences.Default, store.Load(Stable));

        File.WriteAllText(path, "{ pas du json");
        Assert.Equal(UpdatePreferences.Default, store.Load(Stable));

        File.WriteAllText(path, """{ "Channel": "Inconnu", "WaitForNextStable": 12 }""");
        Assert.Equal(UpdatePreferences.Default, store.Load(Stable));
    }

    [Fact]
    public void Load_NoFile_PrereleaseInstalled_DefaultsToBetaChannel()
    {
        // Un testeur en 1.0.0-beta.1 doit recevoir beta.2, sans avoir à chercher le réglage.
        using var dir = new TempDir();
        var store = new UpdatePreferencesStore(dir.Combine("mises-a-jour.json"));

        Assert.Equal(new UpdatePreferences(UpdateChannel.Beta, false), store.Load(Beta));
    }

    [Fact]
    public void Load_NoFile_StableInstalled_DefaultsToStableChannel()
    {
        using var dir = new TempDir();
        var store = new UpdatePreferencesStore(dir.Combine("mises-a-jour.json"));

        Assert.Equal(new UpdatePreferences(UpdateChannel.Stable, false), store.Load(Stable));
    }

    [Fact]
    public void Load_SavedChoice_AlwaysWinsOverTheInstalledVersion()
    {
        using var dir = new TempDir();
        var store = new UpdatePreferencesStore(dir.Combine("mises-a-jour.json"));
        store.Save(new UpdatePreferences(UpdateChannel.Stable, true));

        Assert.Equal(new UpdatePreferences(UpdateChannel.Stable, true), store.Load(Beta));

        store.Save(new UpdatePreferences(UpdateChannel.Beta, false));
        Assert.Equal(new UpdatePreferences(UpdateChannel.Beta, false), store.Load(Stable));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var dir = new TempDir();
        // Le dossier n'existe pas encore : l'enregistrement le crée.
        var path = dir.Combine("reglages", "mises-a-jour.json");
        var store = new UpdatePreferencesStore(path);
        var prefs = new UpdatePreferences(UpdateChannel.Beta, true);

        store.Save(prefs);

        Assert.Equal(prefs, new UpdatePreferencesStore(path).Load(Stable));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
