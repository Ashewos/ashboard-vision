using System.Diagnostics;

namespace Trofeo.Settings.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void SettingsAssembly_ShowsTheProductNameInsteadOfTheProjectName()
    {
        // Le Gestionnaire des tâches lit ces champs, pas le nom du fichier.
        var info = FileVersionInfo.GetVersionInfo(typeof(Texts).Assembly.Location);

        Assert.Equal("Ashboard Vision", info.ProductName);
        Assert.StartsWith("Ashboard Vision", info.FileDescription);
        Assert.Equal("Ashewos", info.CompanyName);
    }
}
