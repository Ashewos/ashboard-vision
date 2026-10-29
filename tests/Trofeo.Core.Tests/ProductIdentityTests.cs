using Trofeo.Core.Layout;
using Trofeo.Core.Secrets;

namespace Trofeo.Core.Tests;

public sealed class ProductIdentityTests
{
    /// <summary>
    /// Ces noms gardent exprès l'ancien identifiant : les changer rendrait illisible le coffre à secrets (entropie DPAPI)
    /// ou ferait refuser à une Freebox son autorisation (<c>FreeboxSource.AppId</c>, vérifié dans Network.Tests), et
    /// ferait refuser les dispositions déjà exportées.
    /// </summary>
    [Fact]
    public void FrozenNames_KeepLegacyId()
    {
        Assert.Equal(ProductIdentity.LegacyId, System.Text.Encoding.UTF8.GetString(DpapiSecretVault.Entropy));
        Assert.Equal("trofeo-HB-dashboard/disposition/1", LayoutTransfer.Format);
        Assert.StartsWith(ProductIdentity.LegacyId + "/", LayoutTransfer.Format);
    }

    [Fact]
    public void AppPaths_SettingsDir_EndsWithAshboardVision()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(appData, "ashboard-vision"), AppPaths.SettingsDir);
    }

    [Fact]
    public void AppPaths_LegacySettingsDir_IsTheFolderOfTheOldName()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(appData, "trofeo-HB-dashboard"), AppPaths.LegacySettingsDir);
    }
}
