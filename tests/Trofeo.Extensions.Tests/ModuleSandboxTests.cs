namespace Trofeo.Extensions.Tests;

/// <summary>L'aide de test qui crée les dossiers de module : une fuite doit faire échouer le test, pas remplir %TEMP% en silence.</summary>
public sealed class ModuleSandboxTests
{
    [WindowsHostFact]
    public void Dispose_FolderWithAFileStillOpen_ThrowsNamingTheFolder_ThenCleansUpOnceReleased()
    {
        var sandbox = new ModuleSandbox();
        var folder = sandbox.SmallModule();
        var held = new FileStream(Path.Combine(folder, "module.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var error = Assert.Throws<IOException>(sandbox.Dispose);

            Assert.Contains(folder, error.Message);
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            held.Dispose();
            sandbox.Dispose(); // le fichier est libre : le dossier part, et le test ne fuit pas
        }

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Dispose_FolderAlreadyGone_IsFine()
    {
        var sandbox = new ModuleSandbox();
        Directory.Delete(sandbox.NewFolder());

        sandbox.Dispose();
    }
}
