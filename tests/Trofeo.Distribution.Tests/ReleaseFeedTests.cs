using Trofeo.Core;

namespace Trofeo.Distribution.Tests;

public sealed class ReleaseFeedTests
{
    private static string Exemple() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "releases-github.json"));

    private static AppVersion V(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        return version;
    }

    [Fact]
    public void Parse_garde_les_versions_publiees_dans_l_ordre_du_flux()
    {
        var versions = ReleaseFeed.Parse(Exemple()).Select(r => r.Version.ToString());

        Assert.Equal(["1.1.0-beta.1", "1.0.0"], versions);
    }

    [Fact]
    public void Parse_ignore_les_brouillons()
    {
        Assert.DoesNotContain(ReleaseFeed.Parse(Exemple()), r => r.Version.Equals(V("1.2.0")));
    }

    [Fact]
    public void Parse_ignore_une_etiquette_illisible()
    {
        // « nightly » a pourtant un fichier d'installation à son nom.
        Assert.DoesNotContain(ReleaseFeed.Parse(Exemple()), r => r.SetupUrl.Contains("nightly"));
    }

    [Fact]
    public void Parse_ignore_une_version_sans_installateur()
    {
        Assert.DoesNotContain(ReleaseFeed.Parse(Exemple()), r => r.Version.Equals(V("0.9.0")));
    }

    [Fact]
    public void Parse_lit_l_installateur_sa_taille_la_date_et_le_drapeau_de_pre_version()
    {
        var releases = ReleaseFeed.Parse(Exemple());

        var beta = releases.Single(r => r.Version.Equals(V("1.1.0-beta.1")));
        Assert.Equal("https://github.com/Ashewos/ashboard-vision/releases/download/v1.1.0-beta.1/Ashboard-Vision-Setup-1.1.0-beta.1.exe", beta.SetupUrl);
        Assert.Equal(51_500_000, beta.SetupSize);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 18, 30, 0, TimeSpan.Zero), beta.PublishedAt);
        Assert.True(beta.Prerelease);

        var stable = releases.Single(r => r.Version.Equals(V("1.0.0")));
        Assert.False(stable.Prerelease);
    }

    [Fact]
    public void ChecksumUrl_pointe_vers_le_fichier_sha256_de_meme_nom_et_vaut_null_s_il_manque()
    {
        var releases = ReleaseFeed.Parse(Exemple());

        var beta = releases.Single(r => r.Version.Equals(V("1.1.0-beta.1")));
        Assert.Equal("https://github.com/Ashewos/ashboard-vision/releases/download/v1.1.0-beta.1/Ashboard-Vision-Setup-1.1.0-beta.1.exe.sha256", beta.ChecksumUrl);

        var stable = releases.Single(r => r.Version.Equals(V("1.0.0")));
        Assert.Null(stable.ChecksumUrl);
    }

    [Fact]
    public void Parse_accepte_une_liste_vide()
    {
        Assert.Empty(ReleaseFeed.Parse("[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[{\"tag_name\": \"v1.0.0\", \"assets\": [")]
    public void Parse_leve_UpdateException_si_le_json_est_vide_inattendu_ou_tronque(string json)
    {
        var erreur = Assert.Throws<UpdateException>(() => ReleaseFeed.Parse(json));

        Assert.False(string.IsNullOrWhiteSpace(erreur.Message));
    }

    [Fact]
    public void Url_vise_le_depot_du_produit()
    {
        Assert.Equal(
            $"https://api.github.com/repos/{ProductIdentity.Repository}/releases?per_page=30",
            ReleaseFeed.Url);
    }
}
