using System.Runtime.InteropServices;

namespace Trofeo.Distribution.Tests;

public sealed class LegacySettingsMoveTests
{
    private static string Legacy(TempDir dir)
    {
        var legacy = dir.Combine("trofeo-HB-dashboard");
        Directory.CreateDirectory(Path.Combine(legacy, "videos", "boucles"));
        File.WriteAllText(Path.Combine(legacy, "layout.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "videos", "boucles", "fond.txt"), "fond");
        return legacy;
    }

    [Fact]
    public void Run_OnlyLegacy_MovesEverything()
    {
        using var dir = new TempDir();
        var legacy = Legacy(dir);
        var target = dir.Combine("ashboard-vision");

        var result = LegacySettingsMove.Run(legacy, target);

        Assert.Equal(LegacySettingsMove.MoveResult.Moved, result);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "layout.json")));
        Assert.Equal("fond", File.ReadAllText(Path.Combine(target, "videos", "boucles", "fond.txt")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Run_NoLegacy_NothingToMove()
    {
        using var dir = new TempDir();

        var result = LegacySettingsMove.Run(dir.Combine("trofeo-HB-dashboard"), dir.Combine("ashboard-vision"));

        Assert.Equal(LegacySettingsMove.MoveResult.NothingToMove, result);
        Assert.False(Directory.Exists(dir.Combine("ashboard-vision")));
    }

    [Fact]
    public void Run_BothExist_KeepsBothAndReturnsAlreadyMoved()
    {
        using var dir = new TempDir();
        var legacy = Legacy(dir);
        var target = dir.Combine("ashboard-vision");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "layout.json"), """{"v":2}""");

        var result = LegacySettingsMove.Run(legacy, target);

        Assert.Equal(LegacySettingsMove.MoveResult.AlreadyMoved, result);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(legacy, "layout.json")));
        Assert.Equal("""{"v":2}""", File.ReadAllText(Path.Combine(target, "layout.json")));
        Assert.False(File.Exists(Path.Combine(target, "videos", "boucles", "fond.txt")));
    }

    [Fact]
    public void Run_SourceLocked_ReturnsFailedAndLegacyIntact()
    {
        using var dir = new TempDir();
        var legacy = Legacy(dir);
        var target = dir.Combine("ashboard-vision");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Un fichier ouvert sans partage (le service qui tourne encore) empêche de renommer le dossier.
            using var locked = new FileStream(Path.Combine(legacy, "layout.json"), FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Equal(LegacySettingsMove.MoveResult.Failed, LegacySettingsMove.Run(legacy, target));
        }
        else
        {
            // Sous Linux, un fichier ouvert n'empêche rien : c'est le dossier parent en lecture seule qui interdit le renommage.
            File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                if (CanWriteIn(dir.Path))
                {
                    return; // exécuté en administrateur : les permissions n'arrêtent personne, le test ne dit rien
                }

                Assert.Equal(LegacySettingsMove.MoveResult.Failed, LegacySettingsMove.Run(legacy, target));
            }
            finally
            {
                File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.Equal("{}", File.ReadAllText(Path.Combine(legacy, "layout.json")));
        Assert.Equal("fond", File.ReadAllText(Path.Combine(legacy, "videos", "boucles", "fond.txt")));
        Assert.False(Directory.Exists(target));
    }

    private static bool CanWriteIn(string directory)
    {
        var probe = Path.Combine(directory, "sonde");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
