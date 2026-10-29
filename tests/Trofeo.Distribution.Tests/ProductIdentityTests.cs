using Trofeo.Core;

namespace Trofeo.Distribution.Tests;

public sealed class ProductIdentityTests
{
    [Fact]
    public void SetupFileName_donne_le_nom_de_l_installateur_de_la_version()
    {
        Assert.Equal("Ashboard-Vision-Setup-1.0.0-beta.1.exe", ProductIdentity.SetupFileName("1.0.0-beta.1"));
    }

    [Fact]
    public void L_identifiant_technique_est_nouveau_et_l_ancien_reste_connu()
    {
        Assert.Equal("ashboard-vision", ProductIdentity.Id);
        Assert.Equal("trofeo-HB-dashboard", ProductIdentity.LegacyId);
    }
}
