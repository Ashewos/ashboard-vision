namespace Trofeo.Distribution.Tests;

public sealed class AppVersionTests
{
    private static AppVersion V(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var version), $"« {text} » devrait être lisible");
        return version;
    }

    [Theory]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("1.0.0+abc123", "1.0.0")]
    [InlineData("v1.2.3-beta.4+build.7", "1.2.3-beta.4")]
    public void TryParse_accepte_le_prefixe_v_et_ignore_les_metadonnees(string text, string attendu)
    {
        Assert.Equal(attendu, V(text).ToString());
    }

    [Fact]
    public void Une_version_avec_ou_sans_v_ou_metadonnees_est_la_meme()
    {
        Assert.Equal(V("1.0.0"), V("v1.0.0+abc123"));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("bêta")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.0.0-")]
    [InlineData("1.0.x")]
    public void TryParse_refuse_ce_qui_n_est_pas_une_version(string? text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
    }

    [Fact]
    public void L_ordre_suit_SemVer()
    {
        string[] croissant = ["1.0.0-beta.2", "1.0.0-beta.10", "1.0.0", "1.0.1", "1.1.0-beta.1"];

        for (var i = 0; i < croissant.Length; i++)
        {
            for (var j = 0; j < croissant.Length; j++)
            {
                Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(V(croissant[i]).CompareTo(V(croissant[j]))));
            }
        }
    }

    [Fact]
    public void Un_identifiant_numerique_se_compare_comme_un_nombre_et_passe_avant_un_identifiant_texte()
    {
        Assert.True(V("1.0.0-beta.2").CompareTo(V("1.0.0-beta.10")) < 0);
        Assert.True(V("1.0.0-1").CompareTo(V("1.0.0-beta")) < 0);
        Assert.True(V("1.0.0-beta").CompareTo(V("1.0.0-beta.1")) < 0);
        Assert.True(V("1.0.0-alpha").CompareTo(V("1.0.0-beta")) < 0);
    }

    [Fact]
    public void IsPrerelease_est_vrai_pour_une_beta_et_faux_pour_une_stable()
    {
        Assert.True(V("1.0.0-beta.1").IsPrerelease);
        Assert.False(V("1.0.0").IsPrerelease);
    }
}
