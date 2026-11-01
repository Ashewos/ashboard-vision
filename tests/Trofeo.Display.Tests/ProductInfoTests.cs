using System.Diagnostics;

namespace Trofeo.Display.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void ServiceAssembly_ShowsTheProductNameInsteadOfTheProjectName()
    {
        // Le Gestionnaire des tâches et « Applications de démarrage » lisent ces champs, pas le nom du fichier.
        var info = FileVersionInfo.GetVersionInfo(typeof(TrayIcon).Assembly.Location);

        Assert.Equal("Ashboard Vision", info.ProductName);
        Assert.Equal("Ashboard Vision", info.FileDescription);
        Assert.Equal("Ashewos", info.CompanyName);
    }
}
