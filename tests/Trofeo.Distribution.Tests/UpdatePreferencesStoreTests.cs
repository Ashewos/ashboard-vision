namespace Trofeo.Distribution.Tests;

public sealed class UpdatePreferencesStoreTests
{
    [Fact]
    public void Load_MissingOrCorrupt_ReturnsDefault()
    {
        using var dir = new TempDir();
        var path = dir.Combine("mises-a-jour.json");
        var store = new UpdatePreferencesStore(path);

        Assert.Equal(UpdatePreferences.Default, store.Load());

        File.WriteAllText(path, "{ pas du json");
        Assert.Equal(UpdatePreferences.Default, store.Load());

        File.WriteAllText(path, """{ "Channel": "Inconnu", "WaitForNextStable": 12 }""");
        Assert.Equal(UpdatePreferences.Default, store.Load());
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

        Assert.Equal(prefs, new UpdatePreferencesStore(path).Load());
        Assert.False(File.Exists(path + ".tmp"));
    }
}
