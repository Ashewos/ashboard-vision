using System.Text.Json;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.ViewModels.Fields;
using static Trofeo.Settings.Tests.ModulesFixture;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Modules sans fenêtre : formulaires tirés des schémas réels, secrets, recherches, actions.</summary>
public sealed class ModulesTabTests
{
    private const string UniFi = """{"source":"unifi","unifi":{"host":"192.168.1.1"}}""";

    private static ModuleState State(Guid id, string typeId, ModuleHealth health, string? message = null, params string[] notices) =>
        new(id, typeId, health, message, notices);

    private static LookupResult City(string label, string name) =>
        new(label, Json($$"""{"name":"{{name}}","latitude":45.76,"longitude":4.83}"""));

    // ---- Les champs et leurs écritures ----

    [Fact]
    public async Task Choice_Change_WritesSettingsAndSaves()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(ClockId);
        var format = f.Tab.Form!.Field<ChoiceFieldViewModel>("format");
        Assert.Equal("24h", format.SelectedValue);                       // la valeur par défaut du schéma

        await format.SelectAsync(format.Options.Single(o => o.Value == "12h"));

        Assert.Equal(1, f.Link.ReloadCount);
        Assert.Equal("12h", f.SettingsOf(ClockId).GetProperty("format").GetString());
        Assert.Equal("12h", f.SettingsOnDisk(ClockId).GetProperty("format").GetString());
        Assert.Equal("12h", format.SelectedValue);
        Assert.True(format.Options.Single(o => o.Value == "12h").IsSelected);
        Assert.False(format.Options.Single(o => o.Value == "24h").IsSelected);
        Assert.True(f.Session.CanUndo);
    }

    [Fact]
    public async Task Choice_SameValue_WritesNothing()
    {
        using var f = new ModulesFixture(WithSettings(ClockId, """{"format":"12h"}"""));
        f.Tab.Select(ClockId);
        var format = f.Tab.Form!.Field<ChoiceFieldViewModel>("format");

        await format.SelectAsync(format.Options.Single(o => o.Value == "12h"));

        Assert.Equal(0, f.Link.ReloadCount);
        Assert.False(f.Session.CanUndo);
    }

    [Fact]
    public async Task Visibility_FollowsSource()
    {
        using var f = new ModulesFixture();                                // Réseau en « pc »
        f.Tab.Select(NetworkId);
        var form = f.Tab.Form!;
        Assert.Equal(["source"], form.VisibleFields.Select(v => v.Key));

        var source = form.Field<ChoiceFieldViewModel>("source");
        await source.SelectAsync(source.Options.Single(o => o.Value == "unifi"));

        // L'hôte, la clé et l'action apparaissent dans l'ordre du schéma, sans refaire le formulaire.
        Assert.Same(form, f.Tab.Form);
        Assert.Equal(["source", "unifi.host", "unifi.apiKey", "forget-certificate"], form.VisibleFields.Select(v => v.Key));

        await source.SelectAsync(source.Options.Single(o => o.Value == "pc"));
        Assert.Equal(["source"], form.VisibleFields.Select(v => v.Key));
    }

    [Fact]
    public async Task Visibility_FollowsAnUndo()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(NetworkId);
        var source = f.Tab.Form!.Field<ChoiceFieldViewModel>("source");
        await source.SelectAsync(source.Options.Single(o => o.Value == "unifi"));
        Assert.Equal(4, f.Tab.Form!.VisibleFields.Count);

        await f.Session.UndoAsync();

        Assert.Equal(["source"], f.Tab.Form!.VisibleFields.Select(v => v.Key));
        Assert.Equal("pc", source.SelectedValue);
    }

    [Fact]
    public async Task Toggle_Change_WritesSettingsAndSaves()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(ClaudeId);                                           // autoFollow est vrai dans ses réglages
        var fade = f.Tab.Form!.Field<ToggleFieldViewModel>("feedFade");
        Assert.False(fade.IsOn);                                          // défaut du schéma

        await fade.SetAsync(true);

        Assert.True(f.SettingsOf(ClaudeId).GetProperty("feedFade").GetBoolean());
        Assert.True(fade.IsOn);
        Assert.Equal(1, f.Link.ReloadCount);
        Assert.True(f.SettingsOf(ClaudeId).GetProperty("autoFollow").GetBoolean());   // les autres réglages sont gardés
    }

    [Fact]
    public async Task Text_CommitsOnce_AsOneUndoStep()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(MonitoringId);
        var title = f.Tab.Form!.Field<TextFieldViewModel>("title");

        title.Text = "Sal";
        title.Text = "Salon";
        Assert.Equal(0, f.Link.ReloadCount);                              // la frappe n'enregistre rien
        Assert.True(title.IsDirty);

        await title.CommitAsync();

        Assert.Equal(1, f.Link.ReloadCount);
        Assert.Equal("Salon", f.SettingsOf(MonitoringId).GetProperty("title").GetString());
        Assert.False(title.IsDirty);

        await f.Session.UndoAsync();                                      // un seul pas défait tout
        Assert.False(f.SettingsOf(MonitoringId).TryGetProperty("title", out _));
        Assert.Equal("", title.Text);                                     // la zone suit l'annulation, puisqu'elle n'a pas de brouillon
    }

    [Fact]
    public async Task Text_Empty_RemovesTheKey()
    {
        using var f = new ModulesFixture(WithSettings(MonitoringId, """{"title":"Salon"}"""));
        f.Tab.Select(MonitoringId);
        var title = f.Tab.Form!.Field<TextFieldViewModel>("title");
        Assert.Equal("Salon", title.Text);

        title.Text = "  ";
        await title.CommitAsync();

        Assert.False(f.SettingsOf(MonitoringId).TryGetProperty("title", out _));
    }

    [Fact]
    public async Task Text_DraftSurvivesOtherChanges()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(MonitoringId);
        var form = f.Tab.Form!;
        var title = form.Field<TextFieldViewModel>("title");
        title.Text = "Brouillon";

        var rows = form.Field<ListFieldViewModel>("rows");
        await rows.ToggleAsync(rows.Entries.Single(e => e.Value == "disk"), true);   // un autre champ s'enregistre

        Assert.Equal("Brouillon", title.Text);                            // le brouillon n'est pas écrasé par la relecture
        Assert.True(title.IsDirty);
    }

    [Fact]
    public async Task Select_AnotherModule_CommitsThePendingText()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(MonitoringId);
        f.Tab.Form!.Field<TextFieldViewModel>("title").Text = "Salon";

        f.Tab.Select(ClockId);
        await f.Session.ApplyAsync(_ => null);                            // attend que la modification en cours soit finie

        Assert.Equal("Salon", f.SettingsOf(MonitoringId).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Settings_OfAShelvedModule_AreEdited()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(CodexId);
        Assert.False(f.Tab.Selected!.IsPlaced);
        var view = f.Tab.Form!.Field<ChoiceFieldViewModel>("view");

        await view.SelectAsync(view.Options.Single(o => o.Value == "overview"));

        Assert.Equal("overview", f.SettingsOf(CodexId).GetProperty("view").GetString());
        Assert.Single(f.Document.Current.Shelf!);
        Assert.Equal(5, f.Document.Current.Views[0].Placements.Count);
    }

    // ---- Les secrets ----

    [Fact]
    public async Task Secret_Set_GoesToVaultNotLayout()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");

        key.Edit();
        key.Input = "s3cr3t-valeur";
        await key.SaveAsync();

        Assert.Equal([("network", "unifi.apiKey", "s3cr3t-valeur")], f.Secrets.Sets);
        Assert.DoesNotContain("s3cr3t-valeur", f.FileText);               // ni dans le fichier,
        Assert.DoesNotContain("s3cr3t-valeur", f.SettingsOf(NetworkId).GetRawText());   // ni dans la disposition en mémoire
        Assert.Equal(0, f.Link.ReloadCount);                              // les réglages n'ont pas changé : rien à enregistrer
        Assert.False(key.IsEditing);
        Assert.Equal("", key.Input);                                      // la saisie est effacée dès qu'elle est partie dans le coffre
    }

    [Fact]
    public async Task Secret_Set_RestartsThePlacedInstancesOfTheType()
    {
        using var f = new ModulesFixture(layout =>
        {
            layout = WithSettings(NetworkId, UniFi)(layout);
            var shelvedNetwork = new ModulePlacement(Guid.NewGuid(), "network", 0, 0, 4, 2, TextSize.Normal, Json("{}"));
            return layout with { Shelf = [.. layout.Shelf!, shelvedNetwork] };
        });
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");

        key.Edit();
        key.Input = "abc";
        await key.SaveAsync();

        // Un module gardé au rechargement ne relit jamais le coffre : on recrée l'instance placée, pas celle qui est retirée.
        Assert.Equal([NetworkId], f.Link.Restarts);
        Assert.Null(f.Session.Message);
    }

    [Fact]
    public async Task Secret_RestartFails_IsShownOnTheWindowLine()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Link.OnRestart = _ => throw new ControlException("La liaison avec le service est coupée.");
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");

        key.Edit();
        key.Input = "abc";
        await key.SaveAsync();

        Assert.True(f.Secrets.Has("network", "unifi.apiKey"));            // le secret est enregistré quand même
        Assert.Contains("La liaison avec le service est coupée.", f.Session.Message);
    }

    [Fact]
    public async Task Secret_RestartFindsNoInstance_IsShownOnTheWindowLine()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Link.OnRestart = _ => Task.FromResult(false);
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");

        key.Edit();
        key.Input = "abc";
        await key.SaveAsync();

        Assert.False(string.IsNullOrEmpty(f.Session.Message));
    }

    [Fact]
    public async Task Secret_Clear_RemovesItAndRestarts()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Secrets.Set("network", "unifi.apiKey", "abc");
        f.Secrets.Sets.Clear();
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");
        Assert.True(key.IsPresent);

        await key.ClearAsync();

        Assert.Equal([("network", "unifi.apiKey", (string?)null)], f.Secrets.Sets);
        Assert.False(key.IsPresent);
        Assert.Equal([NetworkId], f.Link.Restarts);
    }

    [Fact]
    public async Task Secret_ShowsOnlyPresence()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");
        Assert.Equal("Absente", key.StatusText);

        key.Edit();
        key.Input = "top-secret";
        await key.SaveAsync();

        Assert.Equal("Enregistrée", key.StatusText);
        Assert.True(key.IsPresent);
        // Rien de ce que le champ expose ne contient la valeur.
        var exposed = typeof(SecretFieldViewModel).GetProperties()
            .Where(p => p.GetIndexParameters().Length == 0 && p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(key));
        Assert.DoesNotContain(exposed, text => text?.Contains("top-secret") == true);
    }

    [Fact]
    public async Task Secret_VaultFails_ShowsTheErrorAndKeepsTheInput()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Secrets.Failure = new IOException("Le coffre est verrouillé.");
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");

        key.Edit();
        key.Input = "abc";
        await key.SaveAsync();

        Assert.Equal("Impossible d'enregistrer la clé : Le coffre est verrouillé.", key.Error);   // le message reprend le verbe du bouton
        Assert.True(key.IsEditing);                                       // on peut réessayer
        Assert.Empty(f.Link.Restarts);
    }

    [Fact]
    public async Task Secret_ClearFails_ShowsTheClearMessage()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Secrets.Set("network", "unifi.apiKey", "abc");
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");
        f.Secrets.Failure = new IOException("Le coffre est verrouillé.");

        await key.ClearAsync();

        Assert.Equal("Impossible d'effacer la clé : Le coffre est verrouillé.", key.Error);
        Assert.True(key.IsPresent);
        Assert.Empty(f.Link.Restarts);
    }

    [Fact]
    public void Secret_VaultCannotBeRead_ShowsAbsentWithAnError_WithoutThrowing()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Secrets.HasFailure = new TimeoutException("Le coffre à secrets est verrouillé par un autre processus.");

        f.Tab.Select(NetworkId);                                          // construire le formulaire lit le coffre : rien ne doit remonter
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");

        Assert.False(key.IsPresent);
        Assert.Equal("Absente", key.StatusText);
        Assert.Equal("Coffre à secrets indisponible : Le coffre à secrets est verrouillé par un autre processus.", key.Error);
        f.Session.Report(null);                                           // un changement de la session refait la lecture sans lever non plus
        Assert.NotNull(f.Tab.Form);
    }

    [Fact]
    public async Task Secret_ReadFailsAfterAWrite_StillRestartsTheModule()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Secrets.HasFailsAfterSet = true;
        f.Tab.Select(NetworkId);
        var key = f.Tab.Form!.Field<SecretFieldViewModel>("unifi.apiKey");

        key.Edit();
        key.Input = "abc";
        await key.SaveAsync();

        Assert.Equal([("network", "unifi.apiKey", "abc")], f.Secrets.Sets);
        Assert.Equal([NetworkId], f.Link.Restarts);                       // le secret est écrit : le module le relit quand même
        Assert.True(key.IsPresent);
        Assert.False(key.IsEditing);
    }

    // ---- Les recherches ----

    [Fact]
    public async Task Lookup_Debounced_400ms()
    {
        using var f = new ModulesFixture();
        f.Link.OnLookup = (_, _, _) => Task.FromResult<IReadOnlyList<LookupResult>>([City("Lyon, Rhône, France", "Lyon")]);
        f.Tab.Select(ClockId);
        var city = f.Tab.Form!.Field<LookupFieldViewModel>("location");

        city.Query = "L";
        city.Query = "Ly";
        city.Query = "Lyo";
        f.Time.Advance(TimeSpan.FromMilliseconds(399));
        Assert.Empty(f.Link.Lookups);                                     // 400 ms après la dernière frappe, pas avant

        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        await city.SearchTask;

        Assert.Equal([("clock", "location", "Lyo")], f.Link.Lookups);     // trois frappes, une seule recherche
        Assert.Contains(city.Rows, row => row.Label == "Lyon, Rhône, France");
    }

    [Fact]
    public async Task Lookup_Pick_StoresValueAndShowsLabel()
    {
        using var f = new ModulesFixture();
        f.Link.OnLookup = (_, _, _) => Task.FromResult<IReadOnlyList<LookupResult>>(
            [City("Lyon, Rhône, France", "Lyon"), City("Lyon, Ontario, Canada", "Lyon")]);
        f.Tab.Select(ClockId);
        var city = f.Tab.Form!.Field<LookupFieldViewModel>("location");
        Assert.Equal("Pas de météo", city.CurrentText);                   // pas de valeur : le libellé du vide

        city.Query = "Lyon";
        f.Time.Advance(TimeSpan.FromMilliseconds(400));
        await city.SearchTask;
        var row = city.Rows.Single(r => r.Label == "Lyon, Ontario, Canada");   // le service a dit comment la nommer
        await city.PickAsync(row);

        var stored = f.SettingsOf(ClockId).GetProperty("location");       // la valeur du service est gardée telle quelle
        Assert.Equal("Lyon", stored.GetProperty("name").GetString());
        Assert.Equal(45.76, stored.GetProperty("latitude").GetDouble());
        Assert.Equal("Lyon", city.CurrentText);                           // affichée par LabelPath
        Assert.True(city.HasValue);
        Assert.Equal("", city.Query);
        Assert.False(city.IsOpen);                                        // la liste se referme
        Assert.Equal(1, f.Link.ReloadCount);
    }

    [Fact]
    public async Task Lookup_EmptyLabel_ClearsTheKey()
    {
        using var f = new ModulesFixture(WithSettings(ClockId, """{"format":"12h","location":{"name":"Lyon","latitude":45.76,"longitude":4.83}}"""));
        f.Tab.Select(ClockId);
        var city = f.Tab.Form!.Field<LookupFieldViewModel>("location");
        Assert.Equal("Lyon", city.CurrentText);

        city.Open();
        var empty = city.Rows.Single(r => r.IsEmptyChoice);
        Assert.Equal("Pas de météo", empty.Label);
        await city.PickAsync(empty);

        var settings = f.SettingsOf(ClockId);
        Assert.False(settings.TryGetProperty("location", out _));         // la clé est retirée, pas mise à null
        Assert.Equal("12h", settings.GetProperty("format").GetString());
        Assert.Equal("Pas de météo", city.CurrentText);
        Assert.False(city.HasValue);
    }

    [Fact]
    public async Task Lookup_NonSearchable_LoadsOnceWhenShown_AndPicksTheGpu()
    {
        using var f = new ModulesFixture(WithSettings(MonitoringId, """{"gpu":"NVIDIA GeForce RTX 5070"}"""));
        f.Link.OnLookup = (_, _, _) => Task.FromResult<IReadOnlyList<LookupResult>>(
        [
            new LookupResult("Intel(R) Graphics", Json("\"Intel(R) Graphics\"")),
            new LookupResult("NVIDIA GeForce RTX 5070", Json("\"NVIDIA GeForce RTX 5070\"")),
        ]);

        f.Tab.Select(MonitoringId);                                       // le champ s'affiche : la liste se charge d'elle-même
        var gpu = f.Tab.Form!.Field<LookupFieldViewModel>("gpu");
        await gpu.EnsureLoadedAsync();
        await gpu.EnsureLoadedAsync();

        Assert.Equal([("monitoring", "gpu", "")], f.Link.Lookups);        // une seule fois
        Assert.True(gpu.IsOpen);
        Assert.Equal(
            ["Automatique (celle qui a le plus de mémoire)", "Intel(R) Graphics", "NVIDIA GeForce RTX 5070"],
            gpu.Rows.Select(r => r.Label));
        Assert.True(gpu.Rows.Single(r => r.Label == "NVIDIA GeForce RTX 5070").IsSelected);
        Assert.Equal("NVIDIA GeForce RTX 5070", gpu.CurrentText);

        await gpu.PickAsync(gpu.Rows.Single(r => r.Label == "Intel(R) Graphics"));
        Assert.Equal("Intel(R) Graphics", f.SettingsOf(MonitoringId).GetProperty("gpu").GetString());
        Assert.Single(f.Link.Lookups);                                  // choisir ne relance pas la recherche
    }

    [Fact]
    public async Task Lookup_NonSearchable_EmptyList_OffersRefresh()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(MonitoringId);
        var gpu = f.Tab.Form!.Field<LookupFieldViewModel>("gpu");
        await gpu.EnsureLoadedAsync();
        Assert.True(gpu.CanRefresh);                                      // le relevé n'a encore rien donné

        f.Link.OnLookup = (_, _, _) => Task.FromResult<IReadOnlyList<LookupResult>>(
            [new LookupResult("NVIDIA GeForce RTX 5070", Json("\"NVIDIA GeForce RTX 5070\""))]);
        await gpu.ReloadAsync();

        Assert.Equal(2, f.Link.Lookups.Count);
        Assert.False(gpu.CanRefresh);
        Assert.Contains(gpu.Rows, r => r.Label == "NVIDIA GeForce RTX 5070");
    }

    [Fact]
    public async Task Lookup_Failure_ShowsTheError()
    {
        using var f = new ModulesFixture();
        f.Link.OnLookup = (_, _, _) => throw new ControlException("La liaison avec le service est coupée.");
        f.Tab.Select(ClockId);
        var city = f.Tab.Form!.Field<LookupFieldViewModel>("location");

        city.Query = "Lyon";
        f.Time.Advance(TimeSpan.FromMilliseconds(400));
        await city.SearchTask;

        Assert.Contains("La liaison avec le service est coupée.", city.Error);
        Assert.False(city.IsLoading);
    }

    [Fact]
    public async Task Lookup_PickDuringASearch_StopsTheLoadingIndicator()
    {
        using var f = new ModulesFixture();
        var gate = new TaskCompletionSource<IReadOnlyList<LookupResult>>();
        f.Link.OnLookup = (_, _, _) => gate.Task;
        f.Tab.Select(ClockId);
        var city = f.Tab.Form!.Field<LookupFieldViewModel>("location");
        city.Open();

        city.Query = "Lyon";
        f.Time.Advance(TimeSpan.FromMilliseconds(400));                   // la recherche est partie, sans réponse
        Assert.True(city.IsLoading);

        await city.PickAsync(city.Rows.Single(r => r.IsEmptyChoice));
        Assert.False(city.IsLoading);

        gate.SetResult([City("Lyon, Rhône, France", "Lyon")]);            // la réponse tardive ne rouvre rien
        await city.SearchTask;
        Assert.False(city.IsLoading);
        Assert.False(city.IsOpen);
        Assert.Empty(city.Rows);
    }

    // ---- La liste ----

    [Fact]
    public async Task List_Reorder_AndNoDuplicates()
    {
        using var f = new ModulesFixture(WithSettings(MonitoringId, """{"rows":["cpu","cpu","gpu","bogus","ram"]}"""));
        f.Tab.Select(MonitoringId);
        var rows = f.Tab.Form!.Field<ListFieldViewModel>("rows");

        Assert.Equal(["cpu", "gpu", "ram"], rows.Selected);               // doublon et valeur inconnue écartés
        Assert.Equal(8, rows.Entries.Count);                              // toutes les options, une fois chacune
        Assert.Equal(["cpu", "gpu", "ram"], rows.Entries.Take(3).Select(e => e.Value));   // les cochées d'abord, dans l'ordre

        await rows.MoveAsync(rows.Entries.Single(e => e.Value == "ram"), -1);
        Assert.Equal(["cpu", "ram", "gpu"], rows.Selected);
        Assert.Equal(1, f.Link.ReloadCount);                              // un clic, un enregistrement
        Assert.Equal(["cpu", "ram", "gpu"], f.SettingsOf(MonitoringId).GetProperty("rows").EnumerateArray().Select(e => e.GetString()));

        await rows.ToggleAsync(rows.Entries.Single(e => e.Value == "disk"), true);
        await rows.ToggleAsync(rows.Entries.Single(e => e.Value == "disk"), true);   // cocher deux fois ne double rien
        Assert.Equal(["cpu", "ram", "gpu", "disk"], rows.Selected);
        Assert.Equal(2, f.Link.ReloadCount);

        await rows.ToggleAsync(rows.Entries.Single(e => e.Value == "cpu"), false);
        Assert.Equal(["ram", "gpu", "disk"], rows.Selected);
        Assert.Equal(["ram", "gpu", "disk"], rows.Entries.Take(3).Select(e => e.Value));

        var saved = f.SettingsOf(MonitoringId).GetProperty("rows").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(saved.Distinct().Count(), saved.Count);
    }

    [Fact]
    public async Task List_Move_StopsAtTheEnds_AndKeepsOneRow()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(MonitoringId);
        var rows = f.Tab.Form!.Field<ListFieldViewModel>("rows");
        var first = rows.Entries.Single(e => e.Value == "cpu");
        var last = rows.Entries.Single(e => e.Value == "ram");

        Assert.False(first.CanMoveUp);
        Assert.True(first.CanMoveDown);
        Assert.False(last.CanMoveDown);
        Assert.False(rows.Entries.Single(e => e.Value == "disk").CanMoveUp);   // une case décochée ne bouge pas

        await rows.MoveAsync(first, -1);                                  // déjà en haut : rien à écrire
        Assert.Equal(0, f.Link.ReloadCount);

        await rows.ToggleAsync(rows.Entries.Single(e => e.Value == "gpu"), false);
        await rows.ToggleAsync(rows.Entries.Single(e => e.Value == "ram"), false);
        Assert.Equal(["cpu"], rows.Selected);
        Assert.False(rows.Entries.Single(e => e.Value == "cpu").CanUncheck);   // il en reste toujours une, le module sinon reprend ses lignes par défaut
        await rows.ToggleAsync(rows.Entries.Single(e => e.Value == "cpu"), false);
        Assert.Equal(["cpu"], rows.Selected);
    }

    // ---- Les actions ----

    [Fact]
    public async Task Action_WithConfirm_RunsAfterYes()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Link.OnAction = (_, _) => Task.FromResult(new ActionResult(true, "Certificat oublié : la prochaine connexion mémorisera le nouveau."));
        f.Tab.Select(NetworkId);
        var forget = f.Tab.Form!.Field<ActionFieldViewModel>("forget-certificate");
        Assert.Equal("Oublier le certificat de la console", forget.ButtonText);

        await forget.RequestAsync();
        Assert.True(forget.IsConfirming);                                 // la question d'abord
        Assert.StartsWith("La prochaine connexion fera confiance", forget.ConfirmText);
        Assert.Empty(f.Link.Actions);

        forget.CancelConfirm();                                           // « Annuler » ne lance rien
        Assert.False(forget.IsConfirming);
        Assert.Empty(f.Link.Actions);

        await forget.RequestAsync();
        await forget.ConfirmAsync();

        Assert.Equal([(NetworkId, "forget-certificate")], f.Link.Actions);
        Assert.False(forget.IsConfirming);
        Assert.False(forget.IsRunning);
        Assert.Equal("Certificat oublié : la prochaine connexion mémorisera le nouveau.", forget.Message);
        Assert.True(forget.IsOk);
        Assert.False(forget.IsFailed);
        Assert.Empty(f.Link.Restarts);                                    // le service recrée déjà l'instance : la fenêtre n'ajoute rien
        Assert.Equal(0, f.Link.ReloadCount);
    }

    [Fact]
    public async Task Action_WhileRunning_ShowsInProgressAndIsDisabled()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        var gate = new TaskCompletionSource<ActionResult>();
        f.Link.OnAction = (_, _) => gate.Task;
        f.Tab.Select(NetworkId);
        var forget = f.Tab.Form!.Field<ActionFieldViewModel>("forget-certificate");

        await forget.RequestAsync();
        var running = forget.ConfirmAsync();

        Assert.True(forget.IsRunning);
        Assert.Equal("En cours…", forget.ButtonText);
        Assert.False(forget.CanRun);
        Assert.False(forget.HasMessage);

        gate.SetResult(new ActionResult(false, "Indique d'abord l'adresse de la console."));
        await running;

        Assert.False(forget.IsRunning);
        Assert.Equal("Oublier le certificat de la console", forget.ButtonText);
        Assert.True(forget.CanRun);
        Assert.Equal("Indique d'abord l'adresse de la console.", forget.Message);
        Assert.True(forget.IsFailed);
        Assert.False(forget.IsOk);
    }

    [Fact]
    public async Task Action_LinkLost_ShowsTheError()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, UniFi));
        f.Link.OnAction = (_, _) => throw new ControlException("La liaison avec le service est coupée.");
        f.Tab.Select(NetworkId);
        var forget = f.Tab.Form!.Field<ActionFieldViewModel>("forget-certificate");

        await forget.RequestAsync();
        await forget.ConfirmAsync();

        Assert.False(forget.IsRunning);
        Assert.Equal("La liaison avec le service est coupée.", forget.Message);
        Assert.True(forget.IsFailed);
    }

    [Fact]
    public async Task Action_RunsOnTheSettingsJustTyped()
    {
        using var f = new ModulesFixture(WithSettings(NetworkId, """{"source":"unifi"}"""));
        string? hostSeenByTheAction = null;
        f.Link.OnAction = (_, _) =>
        {
            hostSeenByTheAction = f.SettingsOf(NetworkId).GetProperty("unifi").GetProperty("host").GetString();
            return Task.FromResult(new ActionResult(true, "Fait."));
        };
        f.Tab.Select(NetworkId);
        var host = f.Tab.Form!.Field<TextFieldViewModel>("unifi.host");
        var forget = f.Tab.Form!.Field<ActionFieldViewModel>("forget-certificate");

        host.Text = "10.0.0.1";                                           // saisi, pas encore validé : le clic sur le bouton arrive avant la perte du focus
        await forget.RequestAsync();
        await forget.ConfirmAsync();

        Assert.Equal("10.0.0.1", hostSeenByTheAction);                    // le service a reçu l'adresse avant l'action
        Assert.Equal(1, f.Link.ReloadCount);
    }

    [Fact]
    public async Task Action_OnAShelvedModule_IsUnavailable()
    {
        using var f = new ModulesFixture(layout =>
        {
            var shelved = new ModulePlacement(Guid.NewGuid(), "claudecode", 0, 0, 8, 4, TextSize.Normal, Json("{}"));
            return layout with { Shelf = [.. layout.Shelf!, shelved] };
        });
        f.Tab.Select(f.Document.Current.Shelf![1].InstanceId);
        var install = f.Tab.Form!.Field<ActionFieldViewModel>("install-quotas");

        Assert.False(install.CanRun);                                     // le service ne fait pas tourner un module retiré
        await install.RequestAsync();
        Assert.False(install.IsConfirming);
        Assert.Empty(f.Link.Actions);
    }

    // ---- Le panneau de droite ----

    [Fact]
    public void NoSchema_ShowsNothingToSet()
    {
        using var f = new ModulesFixture();

        f.Tab.Select(NowPlayingId);

        Assert.True(f.Tab.HasNoSchema);
        Assert.False(f.Tab.HasForm);
        Assert.Equal("Ce module n'a rien à régler.", Texts.ModulesNothingToSet);
        Assert.Equal("En écoute", f.Tab.SelectedName);
    }

    [Fact]
    public void Notices_And_ActionMessage_Shown()
    {
        using var f = new ModulesFixture(null,
            State(NetworkId, "network", ModuleHealth.Offline, "Appuie sur ► sur ta Freebox…"),
            State(MonitoringId, "monitoring", ModuleHealth.Online, null, "3 lignes sur 5 tiennent à cette taille."));

        f.Tab.Select(NetworkId);
        Assert.True(f.Tab.HasActionMessage);
        Assert.Equal("Appuie sur ► sur ta Freebox…", f.Tab.ActionMessage);
        Assert.False(f.Tab.HasNotices);

        f.Tab.Select(MonitoringId);
        Assert.False(f.Tab.HasActionMessage);
        Assert.True(f.Tab.HasNotices);
        Assert.Equal(["3 lignes sur 5 tiennent à cette taille."], f.Tab.Notices);

        // Le service renvoie son état chaque seconde : les remarques suivent.
        f.Link.Set(LinkState.Connected, f.Link.Welcome, SampleData.Status() with
        {
            Modules = [State(MonitoringId, "monitoring", ModuleHealth.Online, "Capteurs en cours de lecture.")],
        });
        Assert.Equal("Capteurs en cours de lecture.", f.Tab.ActionMessage);
        Assert.False(f.Tab.HasNotices);
    }

    [Fact]
    public void Health_UsesTheThreeWords_AndNothingForShelvedModules()
    {
        using var f = new ModulesFixture(null,
            State(NetworkId, "network", ModuleHealth.Online),
            State(ClockId, "clock", ModuleHealth.Offline),
            State(ClaudeId, "claudecode", ModuleHealth.NotConfigured));

        var byName = f.Tab.Placed.ToDictionary(p => p.Id);
        Assert.Equal("En ligne", byName[NetworkId].HealthText);
        Assert.True(byName[NetworkId].IsOnline);
        Assert.Equal("Hors ligne", byName[ClockId].HealthText);
        Assert.True(byName[ClockId].IsRefuse);
        Assert.Equal("Non configuré", byName[ClaudeId].HealthText);
        Assert.Equal("Non configuré", byName[MonitoringId].HealthText);   // absent de l'état : non configuré
        Assert.True(byName[MonitoringId].IsIdle);

        f.Tab.Select(NetworkId);
        Assert.Equal("En ligne", f.Tab.SelectedHealthText);
        Assert.True(f.Tab.HasHealth);

        var shelved = Assert.Single(f.Tab.Shelved);
        Assert.Equal("Codex", shelved.Name);
        Assert.Equal("", shelved.HealthText);
        Assert.False(shelved.IsPlaced);
        f.Tab.Select(CodexId);
        Assert.Equal("", f.Tab.SelectedHealthText);
        Assert.False(f.Tab.HasHealth);
    }

    [Fact]
    public void Health_FollowsTheServiceStatus()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(NetworkId);
        Assert.Equal("Non configuré", f.Tab.SelectedHealthText);

        f.Link.Set(LinkState.Connected, f.Link.Welcome, SampleData.Status() with
        {
            Modules = [State(NetworkId, "network", ModuleHealth.Online)],
        });

        Assert.Equal("En ligne", f.Tab.SelectedHealthText);
        Assert.True(f.Tab.Placed.Single(p => p.Id == NetworkId).IsOnline);
    }

    [Fact]
    public void Lists_PlacedThenShelved_AndTheFirstPlacedModuleIsChosen()
    {
        using var f = new ModulesFixture();

        Assert.Equal(["Claude Code", "Monitoring PC", "En écoute", "Heure, date et météo", "Réseau"], f.Tab.Placed.Select(p => p.Name));
        Assert.Equal(["Codex"], f.Tab.Shelved.Select(p => p.Name));
        Assert.Equal(ClaudeId, f.Tab.SelectedId);
        Assert.NotNull(f.Tab.Form);
    }

    [Fact]
    public async Task Selection_FollowsTheLayout_WhenTheChosenModuleIsRemoved()
    {
        using var f = new ModulesFixture();
        f.Tab.Select(ClockId);
        Assert.Contains(f.Tab.Placed, p => p.Id == ClockId);

        await f.Session.ApplyAsync(layout => layout.WithPlacements(
            layout.Views[0].Id, [.. layout.Views[0].Placements.Where(p => p.InstanceId != ClockId)]) with
        {
            Shelf = [.. layout.Shelf!, layout.Views[0].Placements.Single(p => p.InstanceId == ClockId)],
        });

        Assert.DoesNotContain(f.Tab.Placed, p => p.Id == ClockId);
        Assert.Contains(f.Tab.Shelved, p => p.Id == ClockId);
        Assert.Equal(ClockId, f.Tab.SelectedId);                          // retiré, il se règle encore depuis « Retirés »
        Assert.False(f.Tab.Selected!.IsPlaced);
    }

    [Fact]
    public void ServiceStopped_HasNoForm()
    {
        using var f = new ModulesFixture();

        f.Link.Set(LinkState.ServiceStopped);

        Assert.Null(f.Tab.Form);
        Assert.False(f.Tab.HasNoSchema);
    }
}
