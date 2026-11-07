using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using static Trofeo.Settings.Tests.CommunityFixture;

namespace Trofeo.Settings.Tests;

/// <summary>La boîte d'un module complémentaire : installer, valider, régler les permissions, avec les textes exacts de la conception.</summary>
public sealed class InstallModuleDialogTests
{
    private const string Path = @"C:\Téléchargements\alex.demo-1.0.0.avmod";

    [Fact]
    public async Task Dialog_ShowsOnlyDeclaredPermissions_Unchecked()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = Package(declared: [ModulePermission.Internet, ModulePermission.Sensors]);

        await fixture.Section.OpenPackageAsync(Path);

        var dialog = Assert.IsType<InstallModuleDialogViewModel>(fixture.Section.Dialog);
        Assert.Equal(["Internet", "Capteurs"], dialog.Permissions.Select(p => p.Label)); // le réseau local, l'ordinateur, la musique : pas demandés, pas montrés
        Assert.All(dialog.Permissions, p => Assert.False(p.IsChecked));
        Assert.All(dialog.Permissions, p => Assert.False(string.IsNullOrWhiteSpace(p.Explanation)));
        Assert.Equal(["inspect " + Path], fixture.Link.ModuleCalls); // rien n'est installé tant qu'on n'a pas cliqué
    }

    [Fact]
    public async Task Dialog_ShowsTheWarningAndWhatTheModuleSaysAboutItself()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = Package(name: "Démo", author: "Alex", version: "1.0.0", description: "Un module pour essayer.");

        await fixture.Section.OpenPackageAsync(Path);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal(
            "Ce module a été créé par la communauté, pas par Ashboard Vision. Il peut contenir du code malveillant : installe seulement un module dont tu connais et fais confiance à l'auteur.",
            dialog.WarningText);
        Assert.True(dialog.ShowsWarning);
        Assert.Equal("Installer un module complémentaire", dialog.Title);
        Assert.Equal("Installer", dialog.ConfirmLabel);
        Assert.Equal("Démo", dialog.Name);
        Assert.Equal("Alex", dialog.Author);
        Assert.Equal("Version 1.0.0", dialog.VersionText);
        Assert.Equal("Un module pour essayer.", dialog.Description);
        Assert.True(dialog.CanConfirm);
    }

    [Fact]
    public async Task Dialog_InstallSendsCheckedPermissionsOnly()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = Package(declared: [ModulePermission.Internet, ModulePermission.Sensors, ModulePermission.NowPlaying]);
        await fixture.Section.OpenPackageAsync(Path);
        var dialog = fixture.Section.Dialog!;
        dialog.Permissions[2].IsChecked = true; // Musique en cours
        dialog.Permissions[0].IsChecked = true; // Internet

        await dialog.ConfirmAsync();

        Assert.Equal($"install {Path} Internet,NowPlaying", fixture.LastChange);
        Assert.Null(fixture.Section.Dialog); // la boîte se ferme
    }

    [Fact]
    public async Task Dialog_InstallWithNothingChecked_SendsNoPermission()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.OpenPackageAsync(Path);

        await fixture.Section.Dialog!.ConfirmAsync();

        Assert.Equal($"install {Path} ", fixture.LastChange);
    }

    [Fact]
    public async Task Dialog_ErrorsHideInstallButton()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = Package(
            errors: ["Le paquet contient plus de 1 000 fichiers.", "Chemin refusé : ..\\evil.dll."], conflict: "Un autre module porte déjà cet identifiant.");

        await fixture.Section.OpenPackageAsync(Path);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal(
            ["Le paquet contient plus de 1 000 fichiers.", "Chemin refusé : ..\\evil.dll.", "Un autre module porte déjà cet identifiant."],
            dialog.Problems); // tels quels
        Assert.True(dialog.HasProblems);
        Assert.False(dialog.CanConfirm);
        Assert.False(dialog.ShowsPermissionArea);
        await dialog.ConfirmAsync();
        Assert.DoesNotContain(fixture.Link.ModuleCalls, call => call.StartsWith("install"));
    }

    [Fact]
    public async Task Dialog_UnreadablePackage_ShowsTheNameOfTheFile()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = new PackageInfo(null, null, null, null, null, [], ["Archive illisible."], null, null);

        await fixture.Section.OpenPackageAsync(Path);

        Assert.Equal("alex.demo-1.0.0.avmod", fixture.Section.Dialog!.Name);
        Assert.Equal(["Archive illisible."], fixture.Section.Dialog.Problems);
    }

    [Fact]
    public async Task Dialog_Cancel_InstallsNothing()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.OpenPackageAsync(Path);

        await fixture.Section.Dialog!.CancelAsync();

        Assert.Null(fixture.Section.Dialog);
        Assert.Equal(["inspect " + Path], fixture.Link.ModuleCalls);
    }

    [Fact]
    public async Task Dialog_RefusedByTheService_ShowsItsMessageAsIs_AndStaysOpen()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.OpenPackageAsync(Path);
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Un autre module porte déjà cet identifiant.");
        var dialog = fixture.Section.Dialog!;

        await dialog.ConfirmAsync();

        Assert.Same(dialog, fixture.Section.Dialog);
        Assert.Equal("Un autre module porte déjà cet identifiant.", dialog.ResultMessage);
        Assert.True(dialog.HasResultMessage);
        Assert.True(dialog.CanConfirm); // on peut réessayer ou annuler
    }

    [Fact]
    public async Task Dialog_ServiceUnreachableWhileInstalling_ShowsTheErrorAsIs()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.OpenPackageAsync(Path);
        var dialog = fixture.Section.Dialog!;
        fixture.Link.ModuleFailure = new ControlException("Le service d'affichage ne répond pas.");

        await dialog.ConfirmAsync();

        Assert.Equal("Le service d'affichage ne répond pas.", dialog.ResultMessage);
        Assert.Same(dialog, fixture.Section.Dialog);
    }

    [Fact]
    public async Task Dialog_InspectionFailure_ShowsTheMessageInTheSection_AndOpensNoDialog()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.InspectFailure = new ControlException("Le service d'affichage ne répond pas.");

        await fixture.Section.OpenPackageAsync(Path);

        Assert.Null(fixture.Section.Dialog);
        Assert.Equal("Le service d'affichage ne répond pas.", fixture.Section.Message);
    }

    [Fact]
    public async Task Dialog_TextFromTheModule_IsPlainTextWithoutInvisibleTricks()
    {
        using var fixture = new CommunityFixture();
        // Un nom qui change le sens d'écriture, des espaces de largeur nulle, un faux saut de ligne, des caractères de contrôle.
        fixture.Link.Package = Package(
            name: "Mon\u202E module\u200B\u0007\r\n<b>gras</b>", author: "A\u2028lex", version: "1.0.0\n", description: "Ligne un\nLigne deux\u202Egnirts\u0000");

        await fixture.Section.OpenPackageAsync(Path);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal("Mon module <b>gras</b>", dialog.Name); // montré tel quel (jamais interprété), sans les caractères trompeurs
        Assert.Equal("Alex", dialog.Author);
        Assert.Equal("Version 1.0.0", dialog.VersionText);
        Assert.Equal("Ligne un\nLigne deux" + "gnirts", dialog.Description);
    }

    [Fact]
    public async Task Dialog_ExtremelyLongTexts_AreKept_TheViewTrimsThem()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = Package(name: new string('N', 10_000), author: new string('A', 10_000), description: string.Join("\n", Enumerable.Repeat(new string('d', 500), 100)));

        await fixture.Section.OpenPackageAsync(Path);

        Assert.Equal(10_000, fixture.Section.Dialog!.Name.Length);
        Assert.True(fixture.Section.Dialog.CanConfirm);
    }

    [Fact]
    public async Task Dialog_Approve_NeverPreChecksAnything_EvenForAModuleWhoseFilesChanged()
    {
        using var fixture = new CommunityFixture().WithModules(
            Module(state: LinkCommunityState.Changed, declared: [ModulePermission.Internet, ModulePermission.Sensors], granted: [ModulePermission.Internet]));
        await fixture.Section.RefreshAsync();

        fixture.Section.Modules.Single().ApproveCommand.Execute(null);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal("Valider un module complémentaire", dialog.Title);
        Assert.Equal("Valider", dialog.ConfirmLabel);
        Assert.Equal(ModuleDialogMode.Approve, dialog.Mode);
        Assert.Equal(["Internet", "Capteurs"], dialog.Permissions.Select(p => p.Label));
        Assert.All(dialog.Permissions, p => Assert.False(p.IsChecked));
        dialog.Permissions[1].IsChecked = true;

        await dialog.ConfirmAsync();

        Assert.Equal("approve alex.demo Sensors", fixture.LastChange);
        Assert.Null(fixture.Section.Dialog);
    }

    [Fact]
    public async Task Dialog_Permissions_ShowsWhatWasGranted_AndSavesTheNewChoice()
    {
        using var fixture = new CommunityFixture().WithModules(Module(declared: [ModulePermission.Internet, ModulePermission.Sensors], granted: [ModulePermission.Internet]));
        await fixture.Section.RefreshAsync();

        fixture.Section.Modules.Single().PermissionsCommand.Execute(null);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal(ModuleDialogMode.Permissions, dialog.Mode);
        Assert.Equal("Permissions du module", dialog.Title);
        Assert.False(dialog.ShowsWarning);
        Assert.Equal([true, false], dialog.Permissions.Select(p => p.IsChecked));
        dialog.Permissions[0].IsChecked = false;
        dialog.Permissions[1].IsChecked = true;

        await dialog.ConfirmAsync();

        Assert.Equal("permissions alex.demo Sensors", fixture.LastChange);
    }

    [Fact]
    public async Task Dialog_PackageThatReplacesAnInstalledModule_KeepsWhatWasGrantedAndFlagsTheNewOnes()
    {
        using var fixture = new CommunityFixture().WithModules(
            Module(version: "1.0.0", declared: [ModulePermission.Internet], granted: [ModulePermission.Internet]));
        fixture.Link.Package = Package(
            version: "1.1.0", declared: [ModulePermission.Internet, ModulePermission.Sensors], installedVersion: "1.0.0");

        await fixture.Section.OpenPackageAsync(Path);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal("Version 1.1.0 (tu as la 1.0.0)", dialog.VersionText);
        Assert.Equal([true, false], dialog.Permissions.Select(p => p.IsChecked));
        Assert.Equal([false, true], dialog.Permissions.Select(p => p.IsNew));
        Assert.True(dialog.HasNewPermissions);
    }

    [Fact]
    public void Texts_OfThePermissions_AreTheOnesOfTheService()
    {
        // La fenêtre ne référence pas le service : ses textes sont dans Strings.resx, et ce test garde les deux d'accord.
        foreach (var permission in Enum.GetValues<ModulePermission>())
        {
            Assert.Equal(Trofeo.Extensions.PermissionTexts.Label(permission), PermissionLabels.Label(permission));
            Assert.Equal(Trofeo.Extensions.PermissionTexts.Explanation(permission), PermissionLabels.Explanation(permission));
        }
    }

    [Theory]
    [InlineData("Mon\u202E module", "Mon module")]
    [InlineData("a\u200Bb\u200Dc\uFEFFd\u00ADe", "abcde")]
    [InlineData("deux\r\nlignes\tet tab", "deux lignes et tab")]
    [InlineData("  rogné  ", "rogné")]
    [InlineData(null, "")]
    public void UntrustedText_Line_RemovesWhatCanFoolTheEye(string? raw, string expected) =>
        Assert.Equal(expected, UntrustedText.Line(raw));

    [Fact]
    public void UntrustedText_Block_KeepsLineBreaks_ButNotLineSeparators() =>
        Assert.Equal("un\ndeuxtrois", UntrustedText.Block("un\r\ndeux\u2028trois\u2029"));
}
