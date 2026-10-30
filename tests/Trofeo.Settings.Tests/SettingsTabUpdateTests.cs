using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>La section « Mises à jour » de l'onglet Paramètres : le canal, la recherche, l'installation et la boîte du retour en arrière.</summary>
public sealed class SettingsTabUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-updates-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;

    public SettingsTabUpdateTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SettingsTabViewModel Open(UpdateInfo? updates)
    {
        _store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        _link.Updates = updates;
        return new SettingsTabViewModel(_link, new EditSession(document), new PreferencesStore(new EditorPreferences(), Path.Combine(_dir, "settings-app.json")),
            new SystemServices(new FakeStartupRegistration(), new FakeElevatedLauncher(), () => @"C:\Trofeo\Trofeo.Display.exe", () => @"C:\Trofeo\Trofeo.SensorHelper.exe", () => 100_000_000));
    }

    private static UpdateInfo Info(
        LinkUpdateState state, string? version = null, bool downgrade = false, string? message = null, int? percent = null,
        LinkUpdateChannel channel = LinkUpdateChannel.Stable, bool wait = false) =>
        new(state, version, downgrade, percent, message, channel, wait);

    /// <summary>Le service est sur la bêta ; passer en Stable lui fait proposer la 1.0.0, plus ancienne : un retour en arrière.</summary>
    private SettingsTabViewModel OpenBetaWithStableDowngrade()
    {
        var tab = Open(Info(LinkUpdateState.UpToDate, message: "Tu as la dernière version.", channel: LinkUpdateChannel.Beta));
        _link.OnSetUpdatePreferences = (channel, wait) =>
        {
            _link.SetUpdates(Info(LinkUpdateState.Available, "1.0.0", downgrade: true, message: "La version 1.0.0 est disponible.", channel: channel, wait: wait));
            return Task.CompletedTask;
        };
        return tab;
    }

    // ---- Le canal ----

    [Fact]
    public async Task ChannelBeta_CallsSetPreferencesBeta()
    {
        var tab = Open(Info(LinkUpdateState.UpToDate, message: "Tu as la dernière version."));
        Assert.True(tab.IsStableChannel);

        await tab.SetChannelAsync(LinkUpdateChannel.Beta);

        Assert.Equal([(LinkUpdateChannel.Beta, false)], _link.UpdatePreferences);
        Assert.True(tab.IsBetaChannel);
        Assert.False(tab.IsStableChannel);
        Assert.Null(tab.Downgrade);
        Assert.Empty(_link.UpdateInstalls);
    }

    [Fact]
    public async Task ChannelAlreadyChosen_AsksNothing()
    {
        var tab = Open(Info(LinkUpdateState.UpToDate, channel: LinkUpdateChannel.Beta));

        await tab.SetChannelAsync(LinkUpdateChannel.Beta);

        Assert.Empty(_link.UpdatePreferences);
    }

    [Fact]
    public async Task ChannelStable_FromBeta_DowngradeOffered_OpensDialog()
    {
        var tab = OpenBetaWithStableDowngrade();

        await tab.SetChannelAsync(LinkUpdateChannel.Stable);

        Assert.Equal([(LinkUpdateChannel.Stable, false)], _link.UpdatePreferences);
        var dialog = Assert.IsType<DowngradeDialogViewModel>(tab.Downgrade);
        Assert.True(tab.HasDowngrade);
        Assert.Equal(
            "Tu repasses à la version stable 1.0.0, plus ancienne que ta bêta. Les réglages enregistrés par la bêta pourraient mal se relire : une copie en est faite avant.",
            dialog.Text);
        Assert.Equal("Attendre la prochaine version stable", Texts.UpdatesWaitForStable);
        Assert.False(dialog.WaitForNextStable);
        Assert.Equal("Installer la version 1.0.0", dialog.ConfirmLabel);
        Assert.Equal("Annuler", Texts.ModulesCancel);
        Assert.Empty(_link.UpdateInstalls); // la boîte demande avant d'installer
    }

    [Fact]
    public async Task ChannelStable_FromBeta_NothingOlderOffered_NoDialog()
    {
        var tab = Open(Info(LinkUpdateState.UpToDate, channel: LinkUpdateChannel.Beta));
        _link.OnSetUpdatePreferences = (channel, wait) =>
        {
            _link.SetUpdates(Info(LinkUpdateState.NoStableYet, message: "Aucune version stable pour l'instant.", channel: channel, wait: wait));
            return Task.CompletedTask;
        };

        await tab.SetChannelAsync(LinkUpdateChannel.Stable);

        Assert.Null(tab.Downgrade);
        Assert.Equal("Aucune version stable pour l'instant.", tab.UpdateStatusText);
    }

    [Fact]
    public async Task DowngradeDialog_WaitChecked_SetsPreferencesWaitTrue_DoesNotInstall()
    {
        var tab = OpenBetaWithStableDowngrade();
        await tab.SetChannelAsync(LinkUpdateChannel.Stable);
        var dialog = tab.Downgrade!;

        dialog.WaitForNextStable = true;
        Assert.Equal("Garder ma bêta", dialog.ConfirmLabel); // le bouton dit ce qu'il fait : rien ne s'installe
        await dialog.ConfirmAsync();

        Assert.Equal([(LinkUpdateChannel.Stable, false), (LinkUpdateChannel.Stable, true)], _link.UpdatePreferences);
        Assert.Empty(_link.UpdateInstalls);
        Assert.Null(tab.Downgrade);
    }

    [Fact]
    public async Task DowngradeDialog_Confirmed_InstallsWithConfirmDowngrade()
    {
        var tab = OpenBetaWithStableDowngrade();
        await tab.SetChannelAsync(LinkUpdateChannel.Stable);

        await tab.Downgrade!.ConfirmAsync();

        Assert.Equal([true], _link.UpdateInstalls);
        Assert.Equal([(LinkUpdateChannel.Stable, false)], _link.UpdatePreferences); // seule la demande de canal du départ
        Assert.Null(tab.Downgrade);
    }

    [Fact]
    public async Task DowngradeDialog_Cancelled_InstallsNothing_AndKeepsStable()
    {
        var tab = OpenBetaWithStableDowngrade();
        await tab.SetChannelAsync(LinkUpdateChannel.Stable);

        tab.Downgrade!.Cancel();

        Assert.Null(tab.Downgrade);
        Assert.Empty(_link.UpdateInstalls);
        Assert.Single(_link.UpdatePreferences);
        Assert.True(tab.IsStableChannel);
    }

    [Fact]
    public async Task InstallButton_OnADowngradeOffer_AsksFirst()
    {
        var tab = Open(Info(LinkUpdateState.Available, "1.0.0", downgrade: true, message: "La version 1.0.0 est disponible."));

        await tab.InstallUpdateAsync();

        Assert.Empty(_link.UpdateInstalls);
        Assert.NotNull(tab.Downgrade);
    }

    [Fact]
    public async Task DowngradeDialog_ClosesWhenTheOfferGoesAway()
    {
        var tab = OpenBetaWithStableDowngrade();
        await tab.SetChannelAsync(LinkUpdateChannel.Stable);

        _link.SetUpdates(Info(LinkUpdateState.UpToDate, message: "Tu as la dernière version."));

        Assert.Null(tab.Downgrade);
    }

    // ---- Rechercher et installer ----

    [Fact]
    public async Task CheckNow_AsksTheService()
    {
        var tab = Open(Info(LinkUpdateState.UpToDate, message: "Tu as la dernière version."));

        await tab.CheckUpdatesAsync();

        Assert.Equal(1, _link.CheckUpdatesCount);
    }

    [Fact]
    public async Task Install_NormalOffer_InstallsWithoutConfirmDowngrade()
    {
        var tab = Open(Info(LinkUpdateState.Available, "0.9.0", message: "La version 0.9.0 est disponible."));

        await tab.InstallUpdateAsync();

        Assert.Equal([false], _link.UpdateInstalls);
        Assert.Null(tab.Downgrade);
    }

    [Theory]
    [InlineData(LinkUpdateState.Unknown, null, false)]
    [InlineData(LinkUpdateState.Checking, null, false)]
    [InlineData(LinkUpdateState.UpToDate, null, false)]
    [InlineData(LinkUpdateState.NoStableYet, null, false)]
    [InlineData(LinkUpdateState.Available, "0.9.0", true)]
    [InlineData(LinkUpdateState.Downloading, "0.9.0", false)]
    [InlineData(LinkUpdateState.Installing, "0.9.0", false)]
    [InlineData(LinkUpdateState.Failed, null, false)]
    [InlineData(LinkUpdateState.Failed, "0.9.0", true)] // l'offre est gardée après un échec : on peut réessayer
    public void InstallButton_HiddenUnlessAvailable(LinkUpdateState state, string? version, bool visible)
    {
        var tab = Open(Info(state, version, message: "Un message."));

        Assert.Equal(visible, tab.CanInstallUpdate);
        if (visible)
            Assert.Equal("Installer la version 0.9.0", tab.InstallUpdateText);
    }

    [Fact]
    public void InstallButton_WithoutService_Hidden()
    {
        var tab = Open(null);

        Assert.False(tab.CanInstallUpdate);
        Assert.False(tab.CanCheckUpdates);
        Assert.False(tab.HasUpdateStatus);
    }

    [Theory]
    [InlineData(LinkUpdateState.Unknown, true)]
    [InlineData(LinkUpdateState.UpToDate, true)]
    [InlineData(LinkUpdateState.Available, true)]
    [InlineData(LinkUpdateState.Failed, true)]
    [InlineData(LinkUpdateState.Installing, true)] // l'installateur tourne encore : ça ne verrouille pas la recherche
    [InlineData(LinkUpdateState.Checking, false)]
    [InlineData(LinkUpdateState.Downloading, false)]
    public void CheckButton_DisabledOnlyWhileCheckingOrDownloading(LinkUpdateState state, bool enabled)
    {
        var tab = Open(Info(state, "0.9.0"));

        Assert.Equal(enabled, tab.CanCheckUpdates);
    }

    [Fact]
    public async Task InstallCancelledByWindows_ShowsCancelledMessage()
    {
        const string cancelled = "Mise à jour annulée : l'autorisation n'a pas été donnée.";
        var tab = Open(Info(LinkUpdateState.Available, "0.9.0", message: "La version 0.9.0 est disponible."));
        _link.OnInstallUpdate = _ => Task.FromResult(new ActionResult(false, cancelled));

        await tab.InstallUpdateAsync();

        Assert.Equal(cancelled, tab.UpdateStatusText);
        Assert.True(tab.CanInstallUpdate); // l'offre reste là : on peut réessayer
    }

    [Fact]
    public async Task InstallRefused_MessageGoesAwayWhenTheStateMoves()
    {
        var tab = Open(Info(LinkUpdateState.Available, "0.9.0", message: "La version 0.9.0 est disponible."));
        _link.OnInstallUpdate = _ => Task.FromResult(new ActionResult(false, "L'installation de la mise à jour est déjà lancée."));
        await tab.InstallUpdateAsync();
        Assert.Equal("L'installation de la mise à jour est déjà lancée.", tab.UpdateStatusText);

        _link.SetUpdates(Info(LinkUpdateState.Installing, "0.9.0", message: "Installation de la version 0.9.0…"));

        Assert.Equal("Installation de la version 0.9.0…", tab.UpdateStatusText);
    }

    [Fact]
    public async Task LostLink_ShowsTheLinkMessage_AndDoesNotThrow()
    {
        var tab = Open(Info(LinkUpdateState.UpToDate, message: "Tu as la dernière version."));
        _link.OnCheckUpdates = () => throw new ControlException("Le service d'affichage ne répond pas.");

        await tab.CheckUpdatesAsync();

        Assert.Equal("Le service d'affichage ne répond pas.", tab.UpdateStatusText);
        Assert.True(tab.CanCheckUpdates); // plus occupé
    }

    [Fact]
    public void NoStableYet_ShowsMessage()
    {
        var tab = Open(Info(LinkUpdateState.NoStableYet, message: "Aucune version stable pour l'instant."));

        Assert.True(tab.HasUpdateStatus);
        Assert.Equal("Aucune version stable pour l'instant.", tab.UpdateStatusText);
        Assert.False(tab.CanInstallUpdate);
    }

    [Fact]
    public void UpdateStatus_FollowsTheLink()
    {
        var tab = Open(Info(LinkUpdateState.Checking, message: "Recherche en cours…"));
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _link.SetUpdates(Info(LinkUpdateState.Downloading, "0.9.0", message: "Téléchargement… 42 %", percent: 42));

        Assert.Equal("Téléchargement… 42 %", tab.UpdateStatusText);
        Assert.Contains(nameof(SettingsTabViewModel.UpdateStatusText), changed);
        Assert.Contains(nameof(SettingsTabViewModel.CanCheckUpdates), changed);
    }

    // ---- Dans la vraie fenêtre ----

    [AvaloniaFact]
    public async Task UpdatesSection_ShowsTheChoicesAndTheButtons()
    {
        _store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        _link.Updates = Info(LinkUpdateState.Available, "0.9.0", message: "La version 0.9.0 est disponible.");
        using var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true));
        var window = new MainWindow { DataContext = model, Width = 1440, Height = 900 };
        window.Show();
        model.SelectedTab = model.SettingsTab;
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Mises à jour", texts);
        Assert.Contains("Version", texts);
        Assert.Contains("La version 0.9.0 est disponible.", texts);
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        foreach (var expected in new[] { "Stable", "Bêta", "Rechercher maintenant", "Installer la version 0.9.0" })
            Assert.Contains(expected, buttons);

        // Passer de la bêta à la stable ouvre la boîte, dans la fenêtre.
        _link.Updates = Info(LinkUpdateState.UpToDate, channel: LinkUpdateChannel.Beta);
        _link.OnSetUpdatePreferences = (channel, wait) =>
        {
            _link.SetUpdates(Info(LinkUpdateState.Available, "1.0.0", downgrade: true, message: "La version 1.0.0 est disponible.", channel: channel, wait: wait));
            return Task.CompletedTask;
        };
        _link.SetUpdates(_link.Updates);
        await model.SettingsTab.SetChannelAsync(LinkUpdateChannel.Stable);
        Dispatcher.UIThread.RunJobs();

        var box = window.GetVisualDescendants().OfType<DowngradeDialogView>().Single();
        Assert.True(box.IsEffectivelyVisible);
        var inBox = box.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(
            "Tu repasses à la version stable 1.0.0, plus ancienne que ta bêta. Les réglages enregistrés par la bêta pourraient mal se relire : une copie en est faite avant.",
            inBox);
        Assert.Contains(box.GetVisualDescendants().OfType<CheckBox>(), c => (c.Content as string) == "Attendre la prochaine version stable");
        var boxButtons = box.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.Contains("Installer la version 1.0.0", boxButtons);
        Assert.Contains("Annuler", boxButtons);
    }
}
