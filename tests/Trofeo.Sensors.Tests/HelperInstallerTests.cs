using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace Trofeo.Sensors.Tests;

public sealed class HelperInstallerTests : IDisposable
{
    private const string Task = "\"" + TaskDefinition.TaskName + "\"";
    private const string LegacyTask = "\"" + TaskDefinition.LegacyTaskName + "\"";
    private const string PawnIoSetup = @"C:\dl\PawnIO_setup.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "trofeo-installer-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _install;
    private readonly string _legacy;
    private readonly FakeRunner _runner = new();
    private readonly FakePawnIo _pawnIo = new();
    private readonly List<TimeSpan> _sleeps = [];

    public HelperInstallerTests()
    {
        _source = Path.Combine(_root, "source");
        _install = Path.Combine(_root, "install") + Path.DirectorySeparatorChar;
        _legacy = Path.Combine(_root, "legacy") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_source);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HelperInstaller Installer() => new(_runner, _pawnIo, _source, _install, _sleeps.Add, [_legacy]);

    [Fact]
    public void Install_PawnIoMissing_NoPath_ChangesNothing()
    {
        File.WriteAllText(Path.Combine(_source, "Trofeo.SensorHelper.exe"), "exe");

        var result = Installer().Install(null);

        Assert.Equal(InstallResult.PawnIoMissing, result);
        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(_install));
    }

    [Fact]
    public void Install_RunsPawnIoSetupThenTask()
    {
        var result = Installer().Install(PawnIoSetup);

        Assert.Equal(InstallResult.Installed, result);
        Assert.Equal(
        [
            (PawnIoSetup, "-install -silent"),
            ("schtasks", $"/End /TN {LegacyTask}"),
            ("schtasks", $"/Delete /TN {LegacyTask} /F"),
            ("schtasks", $"/End /TN {Task}"),
            ("schtasks", $"/Create /TN {Task} /XML \"{_runner.XmlPath}\" /F"),
            ("schtasks", $"/Run /TN {Task}"),
        ], _runner.Calls);
    }

    [Fact]
    public void Install_PawnIoAlreadyInstalled_SkipsSetup()
    {
        _pawnIo.Installed = true;

        var result = Installer().Install(PawnIoSetup);

        Assert.Equal(InstallResult.Installed, result);
        Assert.DoesNotContain(_runner.Calls, c => c.File == PawnIoSetup);
        Assert.Equal(5, _runner.Calls.Count); // les deux commandes de l'ancienne tâche, puis arrêt, création et démarrage
    }

    [Fact]
    public void Install_RemovesLegacyTaskAndFolderFirst()
    {
        _pawnIo.Installed = true;
        Directory.CreateDirectory(Path.Combine(_legacy, "sub"));
        File.WriteAllText(Path.Combine(_legacy, "Trofeo.SensorHelper.exe"), "ancien");
        File.WriteAllText(Path.Combine(_legacy, "sub", "a.dll"), "a");
        var legacyFolderGoneAtCreate = false;
        _runner.OnRun = args =>
        {
            if (args.StartsWith("/Create", StringComparison.Ordinal)) legacyFolderGoneAtCreate = !Directory.Exists(_legacy);
        };

        var result = Installer().Install(null);

        Assert.Equal(InstallResult.Installed, result);
        var arguments = _runner.Calls.Select(c => c.Args).ToList();
        Assert.Equal($"/End /TN {LegacyTask}", arguments[0]);
        Assert.Equal($"/Delete /TN {LegacyTask} /F", arguments[1]);
        Assert.True(arguments.FindIndex(a => a.StartsWith("/Create", StringComparison.Ordinal)) > 1); // avant la création de la nouvelle tâche
        Assert.True(legacyFolderGoneAtCreate);
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void Install_RemovesEveryLegacyFolder()
    {
        _pawnIo.Installed = true;
        var otherLegacy = Path.Combine(_root, "legacy2") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_legacy);
        Directory.CreateDirectory(otherLegacy);
        File.WriteAllText(Path.Combine(_legacy, "a.dll"), "a");
        File.WriteAllText(Path.Combine(otherLegacy, "b.dll"), "b");
        var installer = new HelperInstaller(_runner, _pawnIo, _source, _install, _sleeps.Add, [_legacy, otherLegacy]);

        Assert.Equal(InstallResult.Installed, installer.Install(null));

        Assert.False(Directory.Exists(_legacy));
        Assert.False(Directory.Exists(otherLegacy));
    }

    [Fact]
    public void Install_RemovesTheLegacyParentWhenEmpty_EvenIfTheFolderWasAlreadyGone()
    {
        // Le cas du PC d'Alex : %ProgramFiles%\trofeo-HB-dashboard restait vide après le retrait de son sous-dossier sensors.
        _pawnIo.Installed = true;
        var emptyParent = Path.Combine(_root, "ancien-vide");
        var fullParent = Path.Combine(_root, "ancien-plein");
        Directory.CreateDirectory(emptyParent);
        Directory.CreateDirectory(Path.Combine(fullParent, "sensors"));
        File.WriteAllText(Path.Combine(fullParent, "autre.txt"), "à garder");
        var installer = new HelperInstaller(_runner, _pawnIo, _source, _install, _sleeps.Add,
            [Path.Combine(emptyParent, "sensors") + Path.DirectorySeparatorChar, Path.Combine(fullParent, "sensors") + Path.DirectorySeparatorChar]);

        Assert.Equal(InstallResult.Installed, installer.Install(null));

        Assert.False(Directory.Exists(emptyParent));
        Assert.False(Directory.Exists(Path.Combine(fullParent, "sensors")));
        Assert.True(File.Exists(Path.Combine(fullParent, "autre.txt"))); // un parent qui contient autre chose reste
    }

    [Fact]
    public void Install_DestinationInsideSource_DoesNotCopyIntoItself()
    {
        // Cas réel : l'installateur lance l'assistant depuis le dossier de l'application, et la destination s'y trouve.
        _pawnIo.Installed = true;
        File.WriteAllText(Path.Combine(_source, "Trofeo.SensorHelper.exe"), "exe");
        Directory.CreateDirectory(Path.Combine(_source, "lib"));
        File.WriteAllText(Path.Combine(_source, "lib", "a.dll"), "a");
        var nested = Path.Combine(_source, "sensors") + Path.DirectorySeparatorChar;
        var installer = new HelperInstaller(_runner, _pawnIo, _source, nested, _sleeps.Add);

        Assert.Equal(InstallResult.Installed, installer.Install(null));

        Assert.True(File.Exists(Path.Combine(nested, "Trofeo.SensorHelper.exe")));
        Assert.True(File.Exists(Path.Combine(nested, "lib", "a.dll")));
        Assert.False(Directory.Exists(Path.Combine(nested, "sensors"))); // pas de copie de la destination dans elle-même
    }

    [Fact]
    public void Install_DestinationDeeperInsideSource_CopiesTheRestOnce()
    {
        _pawnIo.Installed = true;
        File.WriteAllText(Path.Combine(_source, "Trofeo.SensorHelper.exe"), "exe");
        Directory.CreateDirectory(Path.Combine(_source, "lib"));
        File.WriteAllText(Path.Combine(_source, "lib", "a.dll"), "a");
        var nested = Path.Combine(_source, "lib", "sensors") + Path.DirectorySeparatorChar;
        var installer = new HelperInstaller(_runner, _pawnIo, _source, nested, _sleeps.Add);

        Assert.Equal(InstallResult.Installed, installer.Install(null));

        Assert.True(File.Exists(Path.Combine(nested, "Trofeo.SensorHelper.exe")));
        Assert.True(File.Exists(Path.Combine(nested, "lib", "a.dll")));
        Assert.False(Directory.Exists(Path.Combine(nested, "lib", "sensors")));
    }

    [Fact]
    public void Install_LegacyTaskAbsent_IsSilent()
    {
        _pawnIo.Installed = true;
        _runner.ExitCodes["schtasks /End"] = 1;
        _runner.ExitCodes["schtasks /Delete"] = 1; // l'ancienne tâche n'existe pas : aucune erreur, aucun message

        var result = Installer().Install(null);

        Assert.Equal(InstallResult.Installed, result);
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void Install_LegacyFolderStaysLocked_StillInstalls()
    {
        _pawnIo.Installed = true;
        Directory.CreateDirectory(_legacy);
        var locked = Path.Combine(_legacy, "Trofeo.SensorHelper.exe");
        File.WriteAllText(locked, "ancien");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = Installer().Install(null); // l'ancien dossier est laissé en place : la nouvelle installation n'en dépend pas

        Assert.Equal(InstallResult.Installed, result);
        Assert.True(File.Exists(locked));
        Assert.Contains(_runner.Calls, c => c.Args.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_RunFromTheLegacyFolder_KeepsIt()
    {
        _pawnIo.Installed = true;
        File.WriteAllText(Path.Combine(_source, "Trofeo.SensorHelper.exe"), "exe");
        var installer = new HelperInstaller(_runner, _pawnIo, _source, _install, _sleeps.Add, [_source + Path.DirectorySeparatorChar]);

        Assert.Equal(InstallResult.Installed, installer.Install(null));

        Assert.True(File.Exists(Path.Combine(_source, "Trofeo.SensorHelper.exe"))); // l'assistant en cours ne se supprime pas lui-même
        Assert.True(File.Exists(Path.Combine(_install, "Trofeo.SensorHelper.exe")));
    }

    [Fact]
    public void Install_PawnIoAlreadyInstalled_NoPath_Installs()
    {
        _pawnIo.Installed = true;
        Assert.Equal(InstallResult.Installed, Installer().Install(null));
    }

    [Fact]
    public void Install_PawnIoReboot_ReturnsRebootRequired()
    {
        _runner.ExitCodes[PawnIoSetup] = 3010;

        var result = Installer().Install(PawnIoSetup);

        Assert.Equal(InstallResult.InstalledRebootRequired, result);
        Assert.Contains(_runner.Calls, c => c.Args.StartsWith("/Run", StringComparison.Ordinal)); // la suite a bien lieu
    }

    [Fact]
    public void Install_PawnIoFails_StopsThere()
    {
        _runner.ExitCodes[PawnIoSetup] = 1603;
        File.WriteAllText(Path.Combine(_source, "a.txt"), "a");

        var result = Installer().Install(PawnIoSetup);

        Assert.Equal(InstallResult.PawnIoFailed, result);
        Assert.Single(_runner.Calls);
        Assert.False(Directory.Exists(_install));
    }

    [Fact]
    public void Install_IgnoresEndExitCode_ButCreateFailureThrows()
    {
        _pawnIo.Installed = true;
        _runner.ExitCodes["schtasks /End"] = 1; // tâche absente : sans conséquence
        Assert.Equal(InstallResult.Installed, Installer().Install(null));

        _runner.ExitCodes["schtasks /Create"] = 1;
        Assert.Throws<InvalidOperationException>(() => Installer().Install(null));
    }

    [Fact]
    public void Install_CopiesFilesRecursively_Overwriting()
    {
        _pawnIo.Installed = true;
        Directory.CreateDirectory(Path.Combine(_source, "sub", "deep"));
        File.WriteAllText(Path.Combine(_source, "Trofeo.SensorHelper.exe"), "nouveau");
        File.WriteAllText(Path.Combine(_source, "sub", "deep", "lib.dll"), "lib");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "Trofeo.SensorHelper.exe"), "ancien");
        File.WriteAllText(Path.Combine(_install, "autre.txt"), "gardé");

        Installer().Install(null);

        Assert.Equal("nouveau", File.ReadAllText(Path.Combine(_install, "Trofeo.SensorHelper.exe")));
        Assert.Equal("lib", File.ReadAllText(Path.Combine(_install, "sub", "deep", "lib.dll")));
        Assert.Equal("gardé", File.ReadAllText(Path.Combine(_install, "autre.txt"))); // rien n'est supprimé avant la copie
    }

    [Fact]
    public void Install_FromTheInstalledCopy_DoesNotCopy()
    {
        _pawnIo.Installed = true;
        File.WriteAllText(Path.Combine(_source, "a.txt"), "a");

        var installer = new HelperInstaller(_runner, _pawnIo, _source + Path.DirectorySeparatorChar, _source);
        Assert.Equal(InstallResult.Installed, installer.Install(null));

        Assert.Equal("a", File.ReadAllText(Path.Combine(_source, "a.txt")));
        Assert.Equal(5, _runner.Calls.Count);
    }

    [Fact]
    public void Install_TaskXml_TargetsInstalledExe_AndTempFileIsRemoved()
    {
        _pawnIo.Installed = true;

        Installer().Install(null);

        var xml = XDocument.Parse(_runner.XmlAtCreate!);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var command = xml.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command")!.Value;
        Assert.Equal(Path.Combine(_install, "Trofeo.SensorHelper.exe"), command);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, _runner.XmlBytesAtCreate![..2]); // UTF-16 LE avec BOM
        Assert.False(File.Exists(_runner.XmlPath));
    }

    [Fact]
    public void Uninstall_DeletesTaskAndFolder()
    {
        Directory.CreateDirectory(Path.Combine(_install, "sub"));
        File.WriteAllText(Path.Combine(_install, "sub", "a.txt"), "a");

        Installer().Uninstall();

        Assert.Equal(
        [
            ("schtasks", $"/End /TN {Task}"),
            ("schtasks", $"/Delete /TN {Task} /F"),
        ], _runner.Calls);
        Assert.False(Directory.Exists(_install));
    }

    [Fact]
    public void Uninstall_NothingInstalled_DoesNotThrow()
    {
        _runner.ExitCodes["schtasks"] = 1;
        Installer().Uninstall();
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public void Install_StagesTaskXmlInsideInstallDir_NotInTemp()
    {
        _pawnIo.Installed = true;

        Installer().Install(null);

        // Dossier réservé aux administrateurs, pas le dossier temporaire de l'utilisateur.
        Assert.Equal(Path.Combine(_install, "task.xml"), _runner.XmlPath);
        Assert.False(File.Exists(_runner.XmlPath));
    }

    [Fact]
    public void Install_SkipsJunctions_AndDoesNotFollowThem()
    {
        _pawnIo.Installed = true;
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
        File.WriteAllText(Path.Combine(_source, "ok.txt"), "ok");
        CreateJunction(Path.Combine(_source, "link"), outside);
        CreateJunction(Path.Combine(_source, "loop"), _source); // boucle vers le parent

        try
        {
            Installer().Install(null);

            Assert.True(File.Exists(Path.Combine(_install, "ok.txt")));
            Assert.False(Directory.Exists(Path.Combine(_install, "link")));
            Assert.False(Directory.Exists(Path.Combine(_install, "loop")));
        }
        finally
        {
            // Le nettoyage récursif refuse de traverser une jonction : on retire d'abord les liens eux-mêmes.
            Directory.Delete(Path.Combine(_source, "link"));
            Directory.Delete(Path.Combine(_source, "loop"));
        }
    }

    [Fact]
    public void Install_RetriesCopy_WhileTheOldHelperHoldsAFile()
    {
        _pawnIo.Installed = true;
        File.WriteAllText(Path.Combine(_source, "a.dll"), "nouveau");
        Directory.CreateDirectory(_install);
        var locked = Path.Combine(_install, "a.dll");
        File.WriteAllText(locked, "ancien");
        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var installer = new HelperInstaller(_runner, _pawnIo, _source, _install, delay =>
        {
            _sleeps.Add(delay);
            if (_sleeps.Count == 2) hold.Dispose(); // l'ancien assistant se termine après deux attentes
        });

        try { Assert.Equal(InstallResult.Installed, installer.Install(null)); }
        finally { hold.Dispose(); }

        Assert.Equal(2, _sleeps.Count);
        Assert.Equal("nouveau", File.ReadAllText(locked));
    }

    [Fact]
    public void Install_GivesUpAfterBoundedWait_WhenFileStaysLocked()
    {
        _pawnIo.Installed = true;
        File.WriteAllText(Path.Combine(_source, "a.dll"), "nouveau");
        Directory.CreateDirectory(_install);
        var locked = Path.Combine(_install, "a.dll");
        File.WriteAllText(locked, "ancien");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<IOException>(() => Installer().Install(null));

        Assert.InRange(_sleeps.Count, 3, 20);
        Assert.InRange(_sleeps.Sum(s => s.TotalSeconds), 5, 15);
        Assert.DoesNotContain(_runner.Calls, c => c.Args.StartsWith("/Create", StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_RetriesDelete_WhileAFileIsHeld()
    {
        Directory.CreateDirectory(_install);
        var locked = Path.Combine(_install, "a.dll");
        File.WriteAllText(locked, "x");
        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var installer = new HelperInstaller(_runner, _pawnIo, _source, _install, delay =>
        {
            _sleeps.Add(delay);
            if (_sleeps.Count == 2) hold.Dispose();
        });

        try { installer.Uninstall(); }
        finally { hold.Dispose(); }

        Assert.Equal(2, _sleeps.Count);
        Assert.False(Directory.Exists(_install));
    }

    [Fact]
    public void Uninstall_FromTheInstalledCopy_RefusesBeforeChangingAnything()
    {
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "a.txt"), "a");
        var installer = new HelperInstaller(_runner, _pawnIo, _install.TrimEnd(Path.DirectorySeparatorChar), _install, _sleeps.Add);

        var ex = Assert.Throws<InvalidOperationException>(installer.Uninstall);

        Assert.Contains("dossier de l'application", ex.Message);
        Assert.Empty(_runner.Calls);
        Assert.True(File.Exists(Path.Combine(_install, "a.txt")));
    }

    private static void CreateJunction(string link, string target)
    {
        using var cmd = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        cmd.StandardOutput.ReadToEnd();
        cmd.WaitForExit();
        Assert.True(Directory.Exists(link), "jonction non créée");
    }

    private sealed class FakePawnIo : IPawnIoDetector
    {
        public bool Installed { get; set; }

        public bool IsInstalled() => Installed;
    }

    private sealed class FakeRunner : ICommandRunner
    {
        public List<(string File, string Args)> Calls { get; } = [];

        /// <summary>Code de sortie par fichier, ou par « fichier /Verbe » ; 0 par défaut.</summary>
        public Dictionary<string, int> ExitCodes { get; } = [];

        /// <summary>Appelé à chaque commande, avant sa réponse (pour observer le disque à ce moment-là).</summary>
        public Action<string>? OnRun { get; set; }

        public string? XmlPath { get; private set; }

        public string? XmlAtCreate { get; private set; }

        public byte[]? XmlBytesAtCreate { get; private set; }

        public int Run(string fileName, string arguments)
        {
            Calls.Add((fileName, arguments));
            OnRun?.Invoke(arguments);
            if (arguments.StartsWith("/Create", StringComparison.Ordinal))
            {
                var start = arguments.IndexOf("/XML \"", StringComparison.Ordinal) + 6;
                XmlPath = arguments[start..arguments.IndexOf('"', start)];
                XmlBytesAtCreate = File.ReadAllBytes(XmlPath);
                XmlAtCreate = File.ReadAllText(XmlPath, Encoding.Unicode);
            }

            var verb = arguments.Split(' ')[0];
            if (ExitCodes.TryGetValue($"{fileName} {verb}", out var byVerb)) return byVerb;
            return ExitCodes.GetValueOrDefault(fileName);
        }
    }
}
