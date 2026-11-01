using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using AshboardVision.Sdk;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Tests;

/// <summary>Un test qui a besoin de Windows (AppContainer, job object) : ignoré ailleurs.</summary>
public sealed class WindowsSandboxFactAttribute : FactAttribute
{
    public WindowsSandboxFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "L'enfermement des modules n'existe que sous Windows.";
        }
    }
}

/// <summary>
/// L'enfermement pour de vrai : un programme de test (Trofeo.SandboxProbe) est lancé dans un AppContainer et un job
/// object, et on regarde ce qu'il peut faire. Sans droits d'administrateur. Chaque test crée ses propres profils
/// (identifiants uniques) et les supprime, même en cas d'échec.
/// </summary>
[Trait("Windows", "Sandbox")]
public sealed class SandboxTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly ModulePermission[] None = [];
    private static readonly ModulePermission[] Internet = [ModulePermission.Internet];
    private static readonly ModulePermission[] BothNetworks = [ModulePermission.Internet, ModulePermission.LocalNetwork];

    private readonly List<string> _profiles = [];
    private readonly List<string> _folders = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var id in _profiles)
        {
            AppContainerProfile.Delete(id);
        }

        foreach (var folder in _folders)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- lecture de fichiers -----------------------------------------------------------------------------------------

    [WindowsSandboxFact]
    public async Task Probe_CannotReadUserFile()
    {
        var file = Path.Combine(NewFolder(Path.GetTempPath()), "secret.txt");
        File.WriteAllText(file, "secret");

        var run = await RunProbeAsync(["read", file], None);

        Assert.StartsWith("DENIED", run.Output);
        Assert.Equal(0, run.ExitCode);
    }

    [WindowsSandboxFact]
    public async Task Probe_CanReadGrantedDirectory()
    {
        var folder = NewFolder(Path.GetTempPath());
        var file = Path.Combine(folder, "lisible.txt");
        File.WriteAllText(file, "bonjour"); // créé avant l'octroi : l'héritage doit l'atteindre

        var run = await RunProbeAsync(["read", file], None, grant: [folder]);

        Assert.Equal("OK", run.Output);
    }

    [WindowsSandboxFact]
    public async Task Probe_CanReadFileCreatedAfterTheGrant()
    {
        var folder = NewFolder(Path.GetTempPath());
        var profile = NewProfile();
        profile.GrantRead(folder);
        var file = Path.Combine(folder, "plus-tard.txt");
        File.WriteAllText(file, "bonjour");

        var run = await RunProbeAsync(["read", file], None, profile: profile);

        Assert.Equal("OK", run.Output);
    }

    [WindowsSandboxFact]
    public async Task Probe_CannotWriteToAGrantedDirectory()
    {
        var folder = NewFolder(Path.GetTempPath());

        var run = await RunProbeAsync(["write", folder], None, grant: [folder]);

        Assert.StartsWith("DENIED", run.Output); // lecture + exécution seulement
    }

    [WindowsSandboxFact]
    public async Task Probe_CanWriteItsDataDirectory()
    {
        var profile = NewProfile();
        Directory.CreateDirectory(profile.DataDirectory);

        var run = await RunProbeAsync(["write", profile.DataDirectory], None, profile: profile);

        Assert.Equal("OK", run.Output);
    }

    [WindowsSandboxFact]
    public void GrantRead_Twice_AddsOneRuleForTheContainerOnly()
    {
        var folder = NewFolder(Path.GetTempPath());
        var profile = NewProfile();

        profile.GrantRead(folder);
        profile.GrantRead(folder);

        var rules = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(profile.Sid))
            .ToList();
        var rule = Assert.Single(rules);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rule.FileSystemRights);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
    }

    // ---- réseau ------------------------------------------------------------------------------------------------------

    [WindowsSandboxFact]
    [Trait("Network", "Internet")]
    public async Task Probe_WithoutNetworkPermission_CannotConnect()
    {
        var run = await RunProbeAsync(["tcp", "1.1.1.1", "443"], None);

        Assert.StartsWith("DENIED", run.Output);
    }

    [WindowsSandboxFact]
    [Trait("Network", "Internet")]
    public async Task Probe_WithInternet_CanConnect()
    {
        var run = await RunProbeAsync(["tcp", "1.1.1.1", "443"], Internet);

        Assert.Equal("OK", run.Output);
    }

    [WindowsSandboxFact]
    public async Task Probe_Localhost_IsDenied()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Le contrôle : sans enfermement, la même connexion marche (sinon le refus ne prouverait rien).
        using (var control = new TcpClient())
        {
            await control.ConnectAsync(IPAddress.Loopback, port);
            Assert.True(control.Connected);
        }

        var run = await RunProbeAsync(["tcp", "127.0.0.1", port.ToString()], BothNetworks);

        Assert.StartsWith("DENIED", run.Output);
    }

    // ---- processus et mémoire ------------------------------------------------------------------------------------------

    [WindowsSandboxFact]
    public async Task Probe_Spawn_WorksOutsideTheSandbox()
    {
        // Le contrôle du test suivant : lancé normalement, le même programme parvient à en lancer un autre.
        var run = await RunUnsandboxedAsync(["spawn", HostnameExe]);

        Assert.Equal("OK", run.Output);
    }

    [WindowsSandboxFact]
    public async Task Probe_CannotSpawn()
    {
        var run = await RunProbeAsync(["spawn", HostnameExe], None);

        Assert.StartsWith("DENIED", run.Output); // ActiveProcessLimit = 1
    }

    [WindowsSandboxFact]
    public async Task Probe_UnderMemoryLimit_Works()
    {
        var run = await RunProbeAsync(["alloc", "100"], None);

        Assert.Equal("OK", run.Output);
        Assert.False(run.Usage.MemoryLimitHit);
        Assert.True(run.Usage.PeakProcessMemory >= 100L * 1024 * 1024);
    }

    [WindowsSandboxFact]
    public async Task Probe_OverMemoryLimit_IsKilled()
    {
        var run = await RunProbeAsync(["alloc", "600"], None);

        Assert.DoesNotContain("OK", run.Output);
        Assert.NotEqual(0, run.ExitCode);
        Assert.True(run.Usage.MemoryLimitHit);
    }

    // Décision de la tâche 7 (R13), mesurée : l'hôte de module a le même System.GC.HeapHardLimit que la sonde (voir son
    // runtimeconfig.template.json). Un module qui produit beaucoup de déchets (2,5 Go de blocs d'un Mio jetés aussitôt, 100 Mio
    // vivants) doit survivre : le ramasse-miettes suit, sans que la limite du job ne soit touchée.
    [WindowsSandboxFact]
    public async Task Probe_ManagedChurn_StaysAliveUnderTheJobLimit()
    {
        var run = await RunProbeAsync(["churn", "2500", "100"], None);

        Assert.Equal("OK", run.Output);
        Assert.Equal(0, run.ExitCode);
        Assert.False(run.Usage.MemoryLimitHit, $"pic {run.Usage.PeakProcessMemory / 1048576} Mo");
        Assert.True(run.Usage.PeakProcessMemory < ModuleJob.MemoryLimit);
    }

    public async Task Probe_ManagedMemoryOverLimit_IsKilled()
    {
        var run = await RunProbeAsync(["allocgc", "700"], None);

        Assert.DoesNotContain("OK", run.Output);
        Assert.NotEqual(0, run.ExitCode);
        Assert.True(run.Usage.MemoryLimitHit, $"pic {run.Usage.PeakProcessMemory}, sortie {run.ExitCode:X}");
    }

    [WindowsSandboxFact]
    public async Task Job_Read_ReportsCpuTimeAndPeakMemory()
    {
        var run = await RunProbeAsync(["alloc", "50"], None);

        Assert.True(run.Usage.CpuTime > TimeSpan.Zero);
        Assert.True(run.Usage.PeakProcessMemory > 0);
    }

    [WindowsSandboxFact]
    public void Job_AppliesTheDocumentedLimits()
    {
        using var job = new ModuleJob();

        Assert.True(Native.QueryInformationJobObject(job.Handle, Native.JobObjectExtendedLimitInformation, out Native.JobExtendedLimitInformation limits,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.JobExtendedLimitInformation>(), 0));
        var expected = Native.JobObjectLimitProcessMemory | Native.JobObjectLimitActiveProcess | Native.JobObjectLimitKillOnJobClose
            | Native.JobObjectLimitDieOnUnhandledException;
        Assert.Equal(expected, limits.BasicLimitInformation.LimitFlags & expected);
        Assert.Equal(512u * 1024 * 1024, (uint)limits.ProcessMemoryLimit);
        Assert.Equal(1u, limits.BasicLimitInformation.ActiveProcessLimit);

        Assert.True(Native.QueryInformationJobObject(job.Handle, Native.JobObjectCpuRateControlInformation, out Native.JobCpuRateControlInformation cpu,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.JobCpuRateControlInformation>(), 0));
        Assert.Equal(Native.JobObjectCpuRateControlEnable | Native.JobObjectCpuRateControlHardCap, cpu.ControlFlags);
        Assert.Equal(2500u, cpu.CpuRate);
    }

    // ---- environnement -------------------------------------------------------------------------------------------------

    [WindowsSandboxFact]
    public async Task Environment_DoesNotLeakTheServiceVariables()
    {
        var canary = "ASHBOARD_CANARY_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(canary, "secret");
        try
        {
            var inside = await RunUnsandboxedAsync(["env", canary]);
            Assert.Equal("OK secret", inside.Output); // le contrôle : un enfant ordinaire la voit

            var sandboxed = await RunProbeAsync(["env", canary], None);
            Assert.Equal("DENIED absent", sandboxed.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(canary, null);
        }
    }

    [WindowsSandboxFact]
    public async Task Environment_TempIsInsideTheContainerAndWritable()
    {
        var profile = NewProfile();
        var temp = Path.Combine(profile.DataDirectory, "Temp");

        foreach (var name in new[] { "TEMP", "TMP" })
        {
            var run = await RunProbeAsync(["env", name], None, profile: profile);
            Assert.Equal("OK " + temp, run.Output); // le dossier temporaire que Windows réserve au conteneur
        }

        // Windows le crée au besoin ; sinon la sonde échouerait ici.
        Directory.CreateDirectory(temp);
        var write = await RunProbeAsync(["write", temp], None, profile: profile);
        Assert.Equal("OK", write.Output);
    }

    [WindowsSandboxFact]
    public async Task Environment_DoesNotExposeTheUserProfile()
    {
        foreach (var name in new[] { "USERPROFILE", "APPDATA", "HOMEPATH", "USERNAME", "COMPUTERNAME" })
        {
            var run = await RunProbeAsync(["env", name], None);
            Assert.Equal("DENIED absent", run.Output);
        }
    }

    [WindowsSandboxFact]
    public async Task Environment_KeepsTheMinimumTheRuntimeNeeds()
    {
        var root = await RunProbeAsync(["env", "SystemRoot"], None);
        Assert.Equal("OK " + Environment.GetFolderPath(Environment.SpecialFolder.Windows), root.Output);

        var path = await RunProbeAsync(["env", "PATH"], None);
        Assert.StartsWith("OK ", path.Output);
        Assert.DoesNotContain("Users", path.Output); // un PATH système seulement
    }

    // ---- poignées et fin du processus -----------------------------------------------------------------------------------

    [WindowsSandboxFact]
    public async Task Exited_CompletesWithTheExitCode()
    {
        var run = await RunProbeAsync(["inconnue", "x"], None); // le programme de test se termine par 2

        Assert.Equal(2, run.ExitCode);
    }

    [WindowsSandboxFact]
    public async Task InheritedHandle_StaysOpenInTheChild()
    {
        // Le bout d'écriture d'un tube, hérité (et seulement lui) : tant que l'enfant vit, le tube n'est pas fini.
        var profile = NewProfile();
        var exe = ProbeExe(profile);
        var job = Track(new ModuleJob());
        var pipe = Track(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable));
        var process = Track(SandboxedProcess.Start(new SandboxStart(exe, "sleep 1500", profile, None, [pipe.ClientSafePipeHandle], job)));
        pipe.DisposeLocalCopyOfClientHandle();
        var read = pipe.ReadAsync(new byte[1]).AsTask();

        await Task.Delay(700);
        Assert.False(read.IsCompleted);
        await process.Exited.WaitAsync(Patience);
        Assert.Equal(0, await read.WaitAsync(Patience));
    }

    [WindowsSandboxFact]
    public async Task DuplicateInto_GivesTheChildItsOwnCopy()
    {
        // Un bout de tube non héritable, dupliqué dans l'enfant après son démarrage : il le garde ouvert.
        var profile = NewProfile();
        var exe = ProbeExe(profile);
        var job = Track(new ModuleJob());
        var pipe = Track(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None));
        var process = Track(SandboxedProcess.Start(new SandboxStart(exe, "sleep 1500", profile, None, [], job)));

        var value = process.DuplicateInto(pipe.ClientSafePipeHandle);
        pipe.DisposeLocalCopyOfClientHandle();
        var read = pipe.ReadAsync(new byte[1]).AsTask();

        Assert.NotEqual(IntPtr.Zero, value);
        await Task.Delay(700);
        Assert.False(read.IsCompleted);
        await process.Exited.WaitAsync(Patience);
        Assert.Equal(0, await read.WaitAsync(Patience));
    }

    [WindowsSandboxFact]
    public async Task UnlistedInheritableHandle_IsNotInheritedByTheChild()
    {
        // Deux tubes héritables ; un seul est dans la liste. L'enfant ne doit tenir que celui-là.
        var profile = NewProfile();
        var exe = ProbeExe(profile);
        var job = Track(new ModuleJob());
        var listed = Track(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable));
        var unlisted = Track(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable));
        var process = Track(SandboxedProcess.Start(new SandboxStart(exe, "sleep 1500", profile, None, [listed.ClientSafePipeHandle], job)));
        listed.DisposeLocalCopyOfClientHandle();
        unlisted.DisposeLocalCopyOfClientHandle();
        var listedRead = listed.ReadAsync(new byte[1]).AsTask();
        var unlistedRead = unlisted.ReadAsync(new byte[1]).AsTask();

        // Le tube hors liste est déjà fini : personne d'autre que nous ne l'avait.
        Assert.Equal(0, await unlistedRead.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(300);
        Assert.False(listedRead.IsCompleted);
        await process.Exited.WaitAsync(Patience);
        Assert.Equal(0, await listedRead.WaitAsync(Patience));
    }

    [WindowsSandboxFact]
    public async Task Start_ListedTwiceOrAlsoAsStandardOutput_IsDeduplicated()
    {
        var profile = NewProfile();
        var exe = ProbeExe(profile);
        var job = Track(new ModuleJob());
        var pipe = Track(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable));
        var client = pipe.ClientSafePipeHandle;
        var process = Track(SandboxedProcess.Start(new SandboxStart(exe, "env SystemRoot", profile, None, [client, client], job, client)));
        pipe.DisposeLocalCopyOfClientHandle();

        var output = await new StreamReader(pipe).ReadToEndAsync().WaitAsync(Patience);
        Assert.StartsWith("OK ", output.Trim());
        Assert.Equal(0, await process.Exited.WaitAsync(Patience));
    }

    [WindowsSandboxFact]
    public async Task KillThenDispose_Repeatedly_NeverRaisesUnhandledExceptions()
    {
        var unhandled = new List<Exception>();
        void OnUnhandled(object? sender, UnhandledExceptionEventArgs e) => unhandled.Add((Exception)e.ExceptionObject);
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unhandled.Add(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var profile = NewProfile();
            var exe = ProbeExe(profile);
            for (var i = 0; i < 50; i++)
            {
                using var job = new ModuleJob();
                var process = SandboxedProcess.Start(new SandboxStart(exe, "sleep 30000", profile, None, [], job));
                process.Kill();
                process.Dispose();

                // Terminé (code de sortie) ou annulé selon la course : jamais en échec, jamais en attente.
                var finished = await Task.WhenAny(process.Exited, Task.Delay(Patience));
                Assert.Same(process.Exited, finished);
                Assert.False(process.Exited.IsFaulted);
                Assert.Throws<ObjectDisposedException>(() => process.Kill());
                Assert.Throws<ObjectDisposedException>(() => process.DuplicateInto(job.Handle));
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(200);
            Assert.Empty(unhandled);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [WindowsSandboxFact]
    public async Task Kill_StopsTheProcess()
    {
        var profile = NewProfile();
        var job = Track(new ModuleJob());
        var process = Track(SandboxedProcess.Start(new SandboxStart(ProbeExe(profile), "sleep 30000", profile, None, [], job)));

        process.Kill();

        await process.Exited.WaitAsync(Patience);
        Assert.NotEqual(0, await process.Exited);
    }

    [WindowsSandboxFact]
    public async Task Start_WhenTheJobRefusesTheProcess_ThrowsAndLeavesNothingRunning()
    {
        var profile = NewProfile();
        var exe = ProbeExe(profile);
        var job = Track(new ModuleJob());
        var first = Track(SandboxedProcess.Start(new SandboxStart(exe, "sleep 2500", profile, None, [], job)));
        var before = Process.GetProcessesByName("Trofeo.SandboxProbe").Length;

        // Le job n'accepte qu'un processus : le second est refusé, donc arrêté avant d'avoir rien exécuté.
        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() =>
            SandboxedProcess.Start(new SandboxStart(exe, "sleep 2500", profile, None, [], job)));

        await Task.Delay(500);
        Assert.Equal(before, Process.GetProcessesByName("Trofeo.SandboxProbe").Length);
        first.Kill();
        await first.Exited.WaitAsync(Patience);
    }

    // ---- profil --------------------------------------------------------------------------------------------------------

    [WindowsSandboxFact]
    public void Ensure_Twice_ReturnsTheSameContainer()
    {
        var id = NewId();
        var first = AppContainerProfile.Ensure(id);
        var second = AppContainerProfile.Ensure(id);

        Assert.Equal(first.Sid, second.Sid);
        Assert.Equal("ashboard-vision.mod." + id, first.Name);
        Assert.Equal(first.DataDirectory, second.DataDirectory);
    }

    [WindowsSandboxFact]
    public void Delete_RemovesProfileAndDataDirectory()
    {
        var id = NewId();
        var profile = AppContainerProfile.Ensure(id);
        var dataDirectory = profile.DataDirectory;
        Assert.True(Directory.Exists(dataDirectory));

        AppContainerProfile.Delete(id);

        Assert.False(Directory.Exists(dataDirectory));
        AppContainerProfile.Delete(id); // déjà supprimé : sans erreur
        Assert.Equal(profile.Sid, AppContainerProfile.Ensure(id).Sid); // le SID ne dépend que du nom
    }

    [WindowsSandboxFact]
    public void Ensure_RefusesANameTheSystemWouldTruncate()
    {
        Assert.Throws<ArgumentException>(() => AppContainerProfile.Ensure(new string('a', 50) + ".b"));
        Assert.Throws<ArgumentException>(() => AppContainerProfile.Ensure("a b.c"));
        Assert.Throws<ArgumentException>(() => AppContainerProfile.Ensure(""));
    }

    // ---- aides -------------------------------------------------------------------------------------------------------------

    private static string HostnameExe => Path.Combine(Environment.SystemDirectory, "hostname.exe");

    private string NewId()
    {
        var id = "test.sandbox-" + Guid.NewGuid().ToString("N")[..12];
        _profiles.Add(id);
        return id;
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Insert(0, disposable); // libérés dans l'ordre inverse de la création
        return disposable;
    }

    private AppContainerProfile NewProfile() => AppContainerProfile.Ensure(NewId());

    private string NewFolder(string parent)
    {
        var folder = Path.Combine(parent, "sandbox-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        return folder;
    }

    /// <summary>Copie la sonde dans un dossier temporaire à elle (pour ne pas semer des droits dans le dossier de sortie).</summary>
    private string CopyProbe()
    {
        var target = NewFolder(Path.GetTempPath());
        var source = Path.Combine(AppContext.BaseDirectory, "probe");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    private string _probeFolder = "";

    /// <summary>Le programme de test, copié, et lisible par le conteneur (le runtime de .NET l'est déjà : « tous les packages d'application »).</summary>
    private string ProbeExe(AppContainerProfile profile)
    {
        if (_probeFolder.Length == 0)
        {
            _probeFolder = CopyProbe();
        }

        profile.GrantRead(_probeFolder);
        return Path.Combine(_probeFolder, "Trofeo.SandboxProbe.exe");
    }

    private sealed record ProbeRun(string Output, int ExitCode, JobUsage Usage);

    private async Task<ProbeRun> RunProbeAsync(string[] args, ModulePermission[] granted, string[]? grant = null, AppContainerProfile? profile = null)
    {
        profile ??= NewProfile();
        var exe = ProbeExe(profile);
        foreach (var folder in grant ?? [])
        {
            profile.GrantRead(folder);
        }

        var job = Track(new ModuleJob());
        var pipe = Track(new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable));
        var process = Track(SandboxedProcess.Start(new SandboxStart(exe, string.Join(' ', args.Select(Quote)), profile, granted, [], job, pipe.ClientSafePipeHandle)));
        pipe.DisposeLocalCopyOfClientHandle();

        var output = new StreamReader(pipe).ReadToEndAsync();
        var exitCode = await process.Exited.WaitAsync(Patience);
        return new ProbeRun((await output.WaitAsync(Patience)).Trim(), exitCode, job.Read());
    }

    private async Task<ProbeRun> RunUnsandboxedAsync(string[] args)
    {
        var folder = CopyProbe();
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(folder, "Trofeo.SandboxProbe.exe"), args.Select(Quote))
        {
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProbeRun(output.Trim().Split('\n')[^1].Trim(), process.ExitCode, default);
    }

    private static string Quote(string argument) => argument.Contains(' ') ? "\"" + argument + "\"" : argument;
}
