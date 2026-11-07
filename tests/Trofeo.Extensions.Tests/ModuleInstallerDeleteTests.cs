namespace Trofeo.Extensions.Tests;

/// <summary>
/// La suppression d'un dossier de module : OneDrive ou l'antivirus gardent souvent un fichier ou le dossier ouvert un court instant juste
/// après l'arrêt de l'hôte (cas vu chez Alex, dossier des modules dans OneDrive). La suppression réessaie avant de renoncer.
/// </summary>
public sealed class ModuleInstallerDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "avmod-delete-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TryDelete_FileHeldBriefly_RetriesAndDeletes()
    {
        var folder = Path.Combine(_root, "alex.demo", "1.0.0");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "module.dll");
        await File.WriteAllTextAsync(file, "x");

        // Tenu sans partage en suppression pendant 400 ms, comme OneDrive juste après l'arrêt de l'hôte.
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Delay(400).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

        var problem = ModuleInstaller.TryDelete(Path.Combine(_root, "alex.demo"), removeEmptyParent: false);
        await release;

        Assert.Null(problem);
        Assert.False(Directory.Exists(Path.Combine(_root, "alex.demo")));
    }

    [Fact]
    public void TryDelete_FileHeldForGood_GivesUpWithTheReason()
    {
        var folder = Path.Combine(_root, "alex.demo", "1.0.0");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "module.dll");
        File.WriteAllText(file, "x");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(ModuleInstaller.TryDelete(Path.Combine(_root, "alex.demo"), removeEmptyParent: false));
        }

        Assert.True(File.Exists(file)); // rien n'est perdu en route : le fichier tenu est toujours là
    }
}
