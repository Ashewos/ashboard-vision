using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>
/// Reports de la tâche 12, repris à la tâche 22 : un double-clic sur un <c>.avmod</c> pendant que la fenêtre est déjà ouverte passe le chemin
/// au premier lancement, et un <c>.avmod</c> qui ne peut pas s'ouvrir dit pourquoi au lieu de laisser la fenêtre muette.
/// </summary>
public sealed class PackageOpeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-package-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public PackageOpeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string TempAvmod(string name = "m.avmod")
    {
        var file = Path.Combine(_dir, name);
        File.WriteAllText(file, "x");
        return file;
    }

    // ---- Pourquoi un .avmod ne s'ouvre pas ----

    [Theory]
    [InlineData(@"\\hote\partage\x.avmod")]
    [InlineData(@"//hote/partage/x.avmod")]
    [InlineData(@"\\?\UNC\hote\partage\x.avmod")]
    public void Resolve_NetworkShare_SaysToCopyTheFile_WithoutTouchingTheDisk(string arg)
    {
        var touched = new List<string>();

        var resolution = PackageArgument.Resolve([arg], path => { touched.Add(path); return true; }, path => { touched.Add(path); return DriveType.Fixed; });

        Assert.Null(resolution.Path);
        Assert.Equal("Copie ce fichier sur ce PC pour l'installer.", resolution.Problem);
        Assert.Empty(touched);
    }

    [Fact]
    public void Resolve_MappedNetworkDrive_SaysToCopyTheFile_BeforeLookingForIt()
    {
        var touched = new List<string>();

        var resolution = PackageArgument.Resolve([@"Z:\modules\x.avmod"], path => { touched.Add(path); return true; }, _ => DriveType.Network);

        Assert.Null(resolution.Path);
        Assert.Equal("Copie ce fichier sur ce PC pour l'installer.", resolution.Problem);
        Assert.Empty(touched);
    }

    [Theory]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    public void Resolve_MissingFile_SaysItIsNotThere(DriveType drive)
    {
        var resolution = PackageArgument.Resolve([@"D:\modules\x.avmod"], _ => false, _ => drive);

        Assert.Null(resolution.Path);
        Assert.Equal("Ce fichier est introuvable : vérifie qu'il est toujours là.", resolution.Problem);
    }

    [Fact]
    public void Resolve_InvalidPath_SaysItIsNotThere()
    {
        var resolution = PackageArgument.Resolve(["bad\0name.avmod"]);

        Assert.Null(resolution.Path);
        Assert.Equal("Ce fichier est introuvable : vérifie qu'il est toujours là.", resolution.Problem);
    }

    [Fact]
    public void Resolve_ExistingLocalFile_GivesThePath_AndNoProblem()
    {
        var file = TempAvmod();

        var resolution = PackageArgument.Resolve(["--x", file]);

        Assert.Equal(file, resolution.Path);
        Assert.Null(resolution.Problem);
    }

    [Theory]
    [InlineData("Trofeo.Settings.exe")]
    [InlineData("module.zip")]
    public void Resolve_NoAvmodArgument_SaysNothing(string arg)
    {
        Assert.Equal(default, PackageArgument.Resolve([arg]));
        Assert.Equal(default, PackageArgument.Resolve(null));
    }

    // ---- La fenêtre : ouvre la boîte, ou dit pourquoi ----

    private (MainViewModel Model, CommunityFixture Fixture) OpenModel()
    {
        var fixture = new CommunityFixture();
        _disposables.Add(fixture);
        var model = new MainViewModel(fixture.Link, fixture.Document, new EditorPreferences(FirstRunDone: true), fixture.Picker, ["Arial"]);
        _disposables.Add(model);
        return (model, fixture);
    }

    [Fact]
    public void OpenPackageArguments_ExistingFile_OpensTheInstallDialog_NothingInstalls()
    {
        var (model, fixture) = OpenModel();
        var file = TempAvmod();

        model.OpenPackageArguments(["--x", file]);

        Assert.Same(model.SettingsTab, model.SelectedTab);
        Assert.Equal(["inspect " + file], fixture.Link.ModuleCalls.Where(call => call.StartsWith("inspect")));
        Assert.NotNull(model.SettingsTab.Community.Dialog);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, call => call.StartsWith("install"));
    }

    [Theory]
    [InlineData(@"\\hote\partage\x.avmod", "Copie ce fichier sur ce PC pour l'installer.")]
    [InlineData(@"C:\dossier-qui-n-existe-pas-trofeo\x.avmod", "Ce fichier est introuvable : vérifie qu'il est toujours là.")]
    public void OpenPackageArguments_FileThatCannotOpen_SaysWhy_AndTouchesNothing(string arg, string expected)
    {
        var (model, fixture) = OpenModel();
        var before = fixture.Link.ModuleCalls.Count;

        model.OpenPackageArguments([arg]);

        Assert.Same(model.SettingsTab, model.SelectedTab);
        Assert.Equal(expected, model.SettingsTab.Community.Message);
        Assert.True(model.SettingsTab.Community.HasMessage);
        Assert.Null(model.SettingsTab.Community.Dialog);
        Assert.DoesNotContain(fixture.Link.ModuleCalls.Skip(before), call => call.StartsWith("inspect") || call.StartsWith("install"));
    }

    [Fact]
    public void OpenPackageArguments_WithoutAnAvmod_ChangesNothing()
    {
        var (model, fixture) = OpenModel();
        var tab = model.SelectedTab;
        var before = fixture.Link.ModuleCalls.Count;

        model.OpenPackageArguments(["--x", "autre.txt"]);
        model.OpenPackageArguments(null);

        Assert.Same(tab, model.SelectedTab);
        Assert.Null(model.SettingsTab.Community.Message);
        Assert.Equal(before, fixture.Link.ModuleCalls.Count);
    }

    // ---- Le second lancement passe le chemin au premier ----

    private static string Unique() => "trofeo-test-" + Guid.NewGuid().ToString("N");

    private SingleInstance First(string name)
    {
        var first = SingleInstance.Acquire(@"Local\" + name, @"Local\" + name + ".show", name + ".open");
        _disposables.Add(first);
        return first;
    }

    private SingleInstance Second(string name, params string[] arguments)
    {
        var second = SingleInstance.Acquire(@"Local\" + name, @"Local\" + name + ".show", name + ".open", arguments);
        _disposables.Add(second);
        return second;
    }

    [Fact]
    public async Task SecondLaunch_PassesTheAvmodPathToTheFirst()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Tube nommé : Windows seulement.");
        var name = Unique();
        var file = TempAvmod();
        var first = First(name);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => received.TrySetResult(args);

        var second = Second(name, "--x", file, "autre.txt");

        Assert.False(second.IsFirst);
        Assert.Equal([file], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecondLaunch_TurnsARelativePathIntoAFullPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Tube nommé : Windows seulement.");
        var name = Unique();
        var first = First(name);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => received.TrySetResult(args);

        Second(name, "relatif.avmod");

        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal([Path.GetFullPath("relatif.avmod")], args);
    }

    [Fact]
    public async Task SecondLaunch_BeforeTheFirstSubscribed_IsNotLost()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Tube nommé : Windows seulement.");
        var name = Unique();
        var file = TempAvmod();
        var first = First(name);

        Second(name, file);
        await Task.Delay(500, TestContext.Current.CancellationToken); // le temps que le premier ait lu le message, sans abonné

        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => received.TrySetResult(args);

        Assert.Equal([file], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecondLaunch_WithoutAnAvmod_SendsNothing_ButStillAsksToComeForward()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Tube nommé : Windows seulement.");
        var name = Unique();
        var first = First(name);
        var received = new List<string[]>();
        first.ArgumentsReceived += args => { lock (received) received.Add(args); };
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ShowRequested += () => shown.TrySetResult();

        Second(name, "--x", "module.zip");

        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        lock (received)
            Assert.Empty(received);
    }

    [Fact]
    public async Task Pipe_OversizedOrGarbageMessage_IsIgnored_AndTheNextOneStillArrives()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Tube nommé : Windows seulement.");
        var name = Unique();
        var file = TempAvmod();
        var first = First(name);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => received.TrySetResult(args);

        // 70 Ko : plus que ce qu'un second lancement envoie jamais.
        using (var client = new NamedPipeClientStream(".", name + ".open", PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            client.Connect(5000);
            try { client.Write(new byte[70 * 1024]); }
            catch (IOException) { /* le serveur a pu fermer avant la fin : c'est ce qu'on veut */ }
        }

        // Des octets qui ne sont pas de l'UTF-8.
        using (var client = new NamedPipeClientStream(".", name + ".open", PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            client.Connect(5000);
            client.Write([0xFF, 0xFE, 0xFD]);
        }

        Second(name, file);

        Assert.Equal([file], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pipe_IsReservedToTheCurrentUser()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Tube nommé : Windows seulement.");
        var name = Unique();
        First(name);

        using var client = new NamedPipeClientStream(".", name + ".open", PipeAccessRights.Write | PipeAccessRights.ReadPermissions,
            PipeOptions.CurrentUserOnly, TokenImpersonationLevel.None, HandleInheritability.None);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);

        var me = WindowsIdentity.GetCurrent().User!;
        var rules = client.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<AuthorizationRule>().ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(me, rule.IdentityReference));
    }
}
