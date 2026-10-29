namespace Trofeo.Distribution.Tests;

public sealed class UpdatePolicyTests
{
    private static readonly UpdatePreferences Stable = new(UpdateChannel.Stable, false);
    private static readonly UpdatePreferences Beta = new(UpdateChannel.Beta, false);
    private static readonly UpdatePreferences StableEnAttente = new(UpdateChannel.Stable, true);

    private static AppVersion V(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        return version;
    }

    private static GitHubRelease Release(string version)
    {
        var v = V(version);
        return new GitHubRelease(
            v,
            v.IsPrerelease,
            $"https://example.test/Ashboard-Vision-Setup-{v}.exe",
            1_000,
            null,
            DateTimeOffset.UnixEpoch);
    }

    private static UpdateDecision Choose(string installee, UpdatePreferences prefs, params string[] publiees) =>
        UpdatePolicy.Choose([.. publiees.Select(Release)], V(installee), prefs);

    [Fact]
    public void Par_defaut_on_suit_la_stable_sans_attendre()
    {
        Assert.Equal(Stable, UpdatePreferences.Default);
    }

    [Fact]
    public void Stable_a_jour_avec_une_beta_plus_recente_ne_donne_aucune_offre()
    {
        var decision = Choose("1.0.0", Stable, "1.0.0", "1.1.0-beta.1");

        Assert.Null(decision.Offer);
        Assert.False(decision.NoStableYet);
    }

    [Fact]
    public void Stable_avec_une_stable_plus_recente_donne_une_offre_sans_retour_en_arriere()
    {
        var decision = Choose("1.0.0", Stable, "1.0.1");

        Assert.Equal(V("1.0.1"), decision.Offer!.Release.Version);
        Assert.False(decision.Offer.IsDowngrade);
    }

    [Fact]
    public void Beta_propose_la_plus_recente_stable_ou_non()
    {
        var decision = Choose("1.0.0", Beta, "1.0.1", "1.1.0-beta.1");

        Assert.Equal(V("1.1.0-beta.1"), decision.Offer!.Release.Version);
        Assert.False(decision.Offer.IsDowngrade);
    }

    [Fact]
    public void Beta_installee_et_sa_stable_publiee_donne_une_offre()
    {
        var decision = Choose("1.1.0-beta.1", Beta, "1.1.0");

        Assert.Equal(V("1.1.0"), decision.Offer!.Release.Version);
        Assert.False(decision.Offer.IsDowngrade);
    }

    [Fact]
    public void Beta_installee_sur_le_canal_stable_propose_de_revenir_en_arriere()
    {
        var decision = Choose("1.1.0-beta.1", Stable, "1.0.1");

        Assert.Equal(V("1.0.1"), decision.Offer!.Release.Version);
        Assert.True(decision.Offer.IsDowngrade);
    }

    [Fact]
    public void Beta_installee_qui_attend_la_prochaine_stable_ne_revient_pas_en_arriere()
    {
        var decision = Choose("1.1.0-beta.1", StableEnAttente, "1.0.1");

        Assert.Null(decision.Offer);
        Assert.False(decision.NoStableYet);
    }

    [Fact]
    public void Beta_installee_qui_attend_la_prochaine_stable_prend_la_stable_qui_la_depasse()
    {
        var decision = Choose("1.1.0-beta.1", StableEnAttente, "1.1.0");

        Assert.Equal(V("1.1.0"), decision.Offer!.Release.Version);
        Assert.False(decision.Offer.IsDowngrade);
    }

    [Fact]
    public void Beta_installee_sur_le_canal_stable_sans_aucune_stable_publiee()
    {
        var decision = Choose("1.0.0-beta.1", Stable, "1.0.0-beta.1", "1.0.0-beta.2");

        Assert.Null(decision.Offer);
        Assert.True(decision.NoStableYet);
    }

    [Fact]
    public void Aucune_version_publiee_ne_donne_aucune_offre()
    {
        var decision = Choose("1.0.0", Beta);

        Assert.Null(decision.Offer);
        Assert.False(decision.NoStableYet);
    }

    [Fact]
    public void Le_choix_ne_depend_pas_de_l_ordre_du_flux()
    {
        var decision = Choose("1.0.0", Stable, "1.0.1", "1.0.3", "1.0.2");

        Assert.Equal(V("1.0.3"), decision.Offer!.Release.Version);
    }
}
