using System.IO.Pipes;
using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Control.Tests;

public partial class ControlChannelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static string NewPipeName() => "trofeo-test-" + Guid.NewGuid().ToString("N");

    private static async Task<ControlClient> ConnectAsync(string pipeName) =>
        await ControlClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(10), CancellationToken.None);

    private static readonly Guid CurrentViewId = new("11110000-0000-0000-0000-000000000001");

    private static ServiceStatus SampleStatus() => new(
        new ScreenInfo(LinkScreenState.Recovering, "Câble débranché"),
        Emulated: true,
        [new ModuleState(Guid.Parse("11111111-1111-1111-1111-111111111111"), "clock", ModuleHealth.Online, "Rien à signaler", ["Remarque"])],
        new ProcessInfo(1_000_000, 2_000_000, 3.5),
        new FrameInfo(42, 1.25, 0.5),
        new SensorInfo(true, false, true),
        CurrentViewId,
        ["Ctrl+Alt+Right"]);

    private static Welcome SampleWelcome() => new("1.2.3", ControlPipe.ProtocolVersion,
    [
        new ModuleTypeInfo("clock", "Heure", new GridSize(2, 1),
            new Dictionary<SizePreset, GridSize> { [SizePreset.Compact] = new(2, 1), [SizePreset.Ideal] = new(3, 2) },
            AcceptsCommands: false,
            new SettingsSchema(
            [
                new ChoiceField("format", "Format", [new SettingOption("24", "24 h"), new SettingOption("12", "12 h")], "24") { Help = "Choisis le format." },
                new ToggleField("seconds", "Secondes", true) { VisibleWhen = new FieldCondition("format", "24") },
                new TextField("city", "Ville", 40) { Placeholder = "Paris" },
                new ListField("parts", "Parties", [new SettingOption("a", "A")], ["a"]),
                new SecretField("token", "Jeton"),
                new LookupField("device", "Appareil") { LabelPath = "title", EmptyLabel = "Aucun" },
                new ActionField("login", "Se connecter") { Confirm = "Sûr ?" },
            ])),
    ]);

    private static UpdateInfo SampleUpdate() =>
        new(LinkUpdateState.Available, "1.2.0", IsDowngrade: true, Percent: null, "La version 1.2.0 est disponible.",
            LinkUpdateChannel.Beta, WaitForNextStable: true);

    private sealed partial class FakeHandler : IControlHandler
    {
        public Welcome WelcomeToReturn { get; set; } = SampleWelcome();
        public ServiceStatus StatusToReturn { get; set; } = SampleStatus();
        public Func<CancellationToken, Task<Reloaded>> OnReload { get; set; } = _ => Task.FromResult(new Reloaded(true, null, []));
        public Func<Guid, CancellationToken, Task<bool>> OnRestart { get; set; } = (_, _) => Task.FromResult(true);
        public Exception? SubscribeError { get; set; }

        public UpdateInfo UpdateToReturn { get; set; } = SampleUpdate();
        public int Checks;
        public List<(LinkUpdateChannel Channel, bool Wait)> PreferencesSet { get; } = [];
        public List<bool> Installs { get; } = [];
        public ActionResult InstallResult { get; set; } = new(true, "Windows va demander l'autorisation d'installer la mise à jour.");

        public readonly TaskCompletionSource<Action<PreviewFrame>> Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Unsubscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SubscribedMaxFps;
        public Guid? SubscribedViewId;
        public Func<Guid, bool> OnShowView { get; set; } = _ => true;
        public List<Guid> ShownViews { get; } = [];

        public event Action<Guid>? ViewChanged;

        public bool HasViewSubscribers => ViewChanged is not null;

        /// <summary>Le service change de vue (clavier, par exemple) : tous les clients connectés l'apprennent.</summary>
        public void RaiseViewChanged(Guid viewId) => ViewChanged?.Invoke(viewId);

        public Task<bool> ShowViewAsync(Guid viewId, CancellationToken ct)
        {
            lock (ShownViews) ShownViews.Add(viewId);
            return Task.FromResult(OnShowView(viewId));
        }

        public Func<string, ShortcutKeyState> OnTestKeys { get; set; } = _ => ShortcutKeyState.Free;
        public List<string> TestedKeys { get; } = [];

        public Task<ShortcutKeyState> TestShortcutKeysAsync(string keys, CancellationToken ct)
        {
            lock (TestedKeys) TestedKeys.Add(keys);
            return Task.FromResult(OnTestKeys(keys));
        }

        public Welcome Hello() => WelcomeToReturn;
        public ServiceStatus GetStatus() => StatusToReturn;
        public Task<Reloaded> ReloadAsync(CancellationToken ct) => OnReload(ct);
        public Task<bool> RestartModuleAsync(Guid instanceId, CancellationToken ct) => OnRestart(instanceId, ct);

        public Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LookupResult>>(
                [new LookupResult($"{typeId}/{fieldKey}/{query}", JsonSerializer.SerializeToElement(new { id = 7 }))]);

        public Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey, CancellationToken ct) =>
            Task.FromResult(new ActionResult(true, $"{instanceId}:{actionKey}"));

        public UpdateInfo GetUpdate() => UpdateToReturn;

        public Task CheckUpdatesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Checks);
            return Task.CompletedTask;
        }

        public Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable, CancellationToken ct)
        {
            PreferencesSet.Add((channel, waitForNextStable));
            return Task.CompletedTask;
        }

        public Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade, CancellationToken ct)
        {
            Installs.Add(confirmDowngrade);
            return Task.FromResult(InstallResult);
        }

        public IDisposable SubscribePreview(int maxFps, Guid? viewId, Action<PreviewFrame> onFrame)
        {
            if (SubscribeError is not null)
                throw SubscribeError;
            SubscribedMaxFps = maxFps;
            SubscribedViewId = viewId;
            Subscribed.TrySetResult(onFrame);
            return new Subscription(Unsubscribed);
        }

        private sealed class Subscription(TaskCompletionSource unsubscribed) : IDisposable
        {
            public void Dispose() => unsubscribed.TrySetResult();
        }
    }

    [Fact]
    public async Task Hello_ReturnsTypesWithSchemas()
    {
        var name = NewPipeName();
        using var server = new ControlServer(name, new FakeHandler());
        server.Start();
        await using var client = await ConnectAsync(name);

        var expected = SampleWelcome();
        Assert.Equal("1.2.3", client.Welcome.ServiceVersion);
        Assert.Equal(ControlPipe.ProtocolVersion, client.Welcome.ProtocolVersion);
        var type = Assert.Single(client.Welcome.Types);
        Assert.Equal("clock", type.TypeId);
        Assert.Equal("Heure", type.DisplayName);
        Assert.Equal(new GridSize(2, 1), type.MinSize);
        Assert.Equal(new GridSize(3, 2), type.Presets[SizePreset.Ideal]);
        Assert.False(type.AcceptsCommands);
        Assert.Equal(expected.Types[0].Schema.Fields.Count, type.Schema.Fields.Count);

        var choice = Assert.IsType<ChoiceField>(type.Schema.Fields[0]);
        Assert.Equal("Choisis le format.", choice.Help);
        Assert.Equal("12", choice.Options[1].Value);
        var toggle = Assert.IsType<ToggleField>(type.Schema.Fields[1]);
        Assert.Equal(new FieldCondition("format", "24"), toggle.VisibleWhen);
        Assert.Equal("Paris", Assert.IsType<TextField>(type.Schema.Fields[2]).Placeholder);
        Assert.Equal(["a"], Assert.IsType<ListField>(type.Schema.Fields[3]).Default);
        Assert.IsType<SecretField>(type.Schema.Fields[4]);
        Assert.Equal("title", Assert.IsType<LookupField>(type.Schema.Fields[5]).LabelPath);
        Assert.Equal("Sûr ?", Assert.IsType<ActionField>(type.Schema.Fields[6]).Confirm);
    }

    [Fact]
    public async Task Hello_DifferentProtocolVersion_ThrowsProtocolMismatchException()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { WelcomeToReturn = SampleWelcome() with { ProtocolVersion = ControlPipe.ProtocolVersion + 1 } };
        using var server = new ControlServer(name, handler);
        server.Start();

        await Assert.ThrowsAsync<ProtocolMismatchException>(() => ConnectAsync(name)); // une ControlException, mais reconnaissable
    }

    [Fact]
    public void TimeoutFor_ActionIsLongerThanReloadAndRestart()
    {
        Assert.Equal(TimeSpan.FromSeconds(150), ControlClient.TimeoutFor(MessageTypes.Action));
        Assert.Equal(TimeSpan.FromSeconds(20), ControlClient.TimeoutFor(MessageTypes.Reload));
        Assert.Equal(TimeSpan.FromSeconds(20), ControlClient.TimeoutFor(MessageTypes.Restart));
        Assert.Equal(TimeSpan.FromSeconds(10), ControlClient.TimeoutFor(MessageTypes.Status));
    }

    [Fact]
    public async Task Connect_NoServer_ThrowsTimeout()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ControlClient.ConnectAsync(NewPipeName(), TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task Status_RoundTrips()
    {
        var name = NewPipeName();
        using var server = new ControlServer(name, new FakeHandler());
        server.Start();
        await using var client = await ConnectAsync(name);

        var status = await client.GetStatusAsync(CancellationToken.None);

        var expected = SampleStatus();
        Assert.Equal(LinkScreenState.Recovering, status.Screen.State);
        Assert.Equal("Câble débranché", status.Screen.Reason);
        Assert.True(status.Emulated);
        var module = Assert.Single(status.Modules);
        Assert.Equal(expected.Modules[0].InstanceId, module.InstanceId);
        Assert.Equal(ModuleHealth.Online, module.Health);
        Assert.Equal("Rien à signaler", module.ActionMessage);
        Assert.Equal(["Remarque"], module.Notices);
        Assert.Equal(new ProcessInfo(1_000_000, 2_000_000, 3.5), status.Process);
        Assert.Equal(new FrameInfo(42, 1.25, 0.5), status.Frames);
        Assert.Equal(new SensorInfo(true, false, true), status.Sensors);
    }

    [Fact]
    public async Task Reload_ReturnsHandlerResult()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { OnReload = _ => Task.FromResult(new Reloaded(false, "Fichier illisible", ["Attention"])) };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var result = await client.ReloadAsync(CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("Fichier illisible", result.Error);
        Assert.Equal(["Attention"], result.Warnings);
    }

    [Fact]
    public async Task Restart_ReturnsHandlerResult()
    {
        var name = NewPipeName();
        var known = Guid.NewGuid();
        var seen = new List<Guid>();
        var handler = new FakeHandler
        {
            OnRestart = (id, _) =>
            {
                seen.Add(id);
                return Task.FromResult(id == known);
            },
        };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        Assert.True(await client.RestartModuleAsync(known, CancellationToken.None));
        var unknown = Guid.NewGuid();
        Assert.False(await client.RestartModuleAsync(unknown, CancellationToken.None));
        Assert.Equal([known, unknown], seen);
    }

    [Fact]
    public async Task LookupAndAction_RoundTrip()
    {
        var name = NewPipeName();
        using var server = new ControlServer(name, new FakeHandler());
        server.Start();
        await using var client = await ConnectAsync(name);

        var results = await client.LookupAsync("clock", "device", "abc", CancellationToken.None);
        var found = Assert.Single(results);
        Assert.Equal("clock/device/abc", found.Label);
        Assert.Equal(7, found.Value.GetProperty("id").GetInt32());

        var id = Guid.NewGuid();
        var action = await client.RunActionAsync(id, "login", CancellationToken.None);
        Assert.True(action.Ok);
        Assert.Equal($"{id}:login", action.Message);
    }

    [Fact]
    public async Task Update_GetCheckSetPreferencesAndInstall_RoundTrip()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        Assert.Equal(SampleUpdate(), await client.GetUpdateAsync(CancellationToken.None));

        await client.CheckUpdatesAsync(CancellationToken.None);
        Assert.Equal(1, handler.Checks);

        await client.SetUpdatePreferencesAsync(LinkUpdateChannel.Stable, waitForNextStable: true, CancellationToken.None);
        await client.SetUpdatePreferencesAsync(LinkUpdateChannel.Beta, waitForNextStable: false, CancellationToken.None);
        Assert.Equal([(LinkUpdateChannel.Stable, true), (LinkUpdateChannel.Beta, false)], handler.PreferencesSet);

        var installed = await client.InstallUpdateAsync(confirmDowngrade: true, CancellationToken.None);
        Assert.True(installed.Ok);
        Assert.Equal(handler.InstallResult.Message, installed.Message);
        handler.InstallResult = new ActionResult(false, "Revenir à une version plus ancienne demande ta confirmation.");
        var refused = await client.InstallUpdateAsync(confirmDowngrade: false, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal([true, false], handler.Installs);
    }

    [Fact]
    public async Task Update_NullFieldsAndDownloadProgress_RoundTrip()
    {
        var name = NewPipeName();
        var handler = new FakeHandler
        {
            UpdateToReturn = new UpdateInfo(LinkUpdateState.Downloading, "1.0.1", false, 42, null, LinkUpdateChannel.Stable, false),
        };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var update = await client.GetUpdateAsync(CancellationToken.None);

        Assert.Equal(handler.UpdateToReturn, update);
        Assert.Equal(42, update.Percent);
        Assert.Null(update.Message);
    }

    [Fact]
    public void ProtocolVersion_IsFour() => Assert.Equal(4, ControlPipe.ProtocolVersion);

    [Fact]
    public void TimeoutFor_UpdateRequests()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ControlClient.TimeoutFor(MessageTypes.GetUpdate));
        Assert.Equal(TimeSpan.FromSeconds(45), ControlClient.TimeoutFor(MessageTypes.CheckUpdates));
        Assert.Equal(TimeSpan.FromSeconds(45), ControlClient.TimeoutFor(MessageTypes.SetUpdatePreferences));
        Assert.Equal(TimeSpan.FromMinutes(10), ControlClient.TimeoutFor(MessageTypes.InstallUpdate));
    }

    [Fact]
    public async Task HandlerThrows_ClientGetsControlException()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { OnReload = _ => throw new InvalidOperationException("Disposition illisible") };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var ex = await Assert.ThrowsAsync<ControlException>(() => client.ReloadAsync(CancellationToken.None));
        Assert.Equal("Disposition illisible", ex.Message);

        // La connexion reste utilisable.
        var status = await client.GetStatusAsync(CancellationToken.None);
        Assert.True(status.Emulated);
    }

    [Fact]
    public async Task SlowHandler_DoesNotBlockOtherRequests()
    {
        var name = NewPipeName();
        var release = new TaskCompletionSource<Reloaded>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler { OnReload = _ => release.Task };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var reload = client.ReloadAsync(CancellationToken.None);
        var status = await client.GetStatusAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.True(status.Emulated);
        Assert.False(reload.IsCompleted);

        release.SetResult(new Reloaded(true, null, []));
        Assert.True((await reload.WaitAsync(Wait)).Applied);
    }

    [Fact]
    public async Task Preview_FramesArriveWithRotation()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);
        var received = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += frame => received.TrySetResult(frame);

        await client.SubscribePreviewAsync(12, CancellationToken.None);
        var publish = await handler.Subscribed.Task.WaitAsync(Wait);
        Assert.Equal(12, handler.SubscribedMaxFps);
        publish(new PreviewFrame([0xFF, 0xD8, 9, 8, 7, 0xFF, 0xD9], 180));

        var frame = await received.Task.WaitAsync(Wait);
        Assert.Equal(180, frame.Rotation);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 9, 8, 7, 0xFF, 0xD9 }, frame.Jpeg);
    }

    [Fact]
    public async Task Preview_HandlerThrows_ClientGetsControlException()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { SubscribeError = new InvalidOperationException("Aperçu indisponible") };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var ex = await Assert.ThrowsAsync<ControlException>(() => client.SubscribePreviewAsync(12, CancellationToken.None));
        Assert.Equal("Aperçu indisponible", ex.Message);

        // La connexion reste utilisable, et un nouvel abonnement peut réussir.
        Assert.True((await client.GetStatusAsync(CancellationToken.None)).Emulated);
        handler.SubscribeError = null;
        await client.SubscribePreviewAsync(12, CancellationToken.None);
        await handler.Subscribed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Preview_SlowClient_DropsFramesNotService()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();

        // Client brut : il s'abonne puis ne lit plus rien, le temps que le service publie.
        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync(5000);
        await SendRawAsync(raw, new { id = 1, type = "preview", maxFps = 12 });
        var publish = await handler.Subscribed.Task.WaitAsync(Wait);

        var jpeg = new byte[250_000];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            publish(new PreviewFrame(jpeg, i));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 50, $"Publier 100 images a pris {watch.ElapsedMilliseconds} ms.");

        // Une fois le client de nouveau à l'écoute, la dernière image publiée finit par arriver ; les autres ont pu être perdues.
        var count = 0;
        var lastRotation = -1;
        using var cts = new CancellationTokenSource(Wait);
        while (lastRotation != 99)
        {
            var frame = await Framing.ReadAsync(raw, cts.Token);
            Assert.NotNull(frame);
            if (frame.Value.Kind != FrameKind.Image)
                continue;
            count++;
            lastRotation = Framing.ParseImage(frame.Value.Payload).Rotation;
        }
        Assert.True(count < 100, $"Aucune image perdue ({count} reçues) : le service a attendu le client.");
    }

    private static async Task SendRawAsync(Stream stream, object request) =>
        await Framing.WriteJsonAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request), CancellationToken.None);

    [Fact]
    public async Task ClientDisconnect_UnsubscribesPreview()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        var client = await ConnectAsync(name);
        await client.SubscribePreviewAsync(12, CancellationToken.None);
        await handler.Subscribed.Task.WaitAsync(Wait);
        Assert.False(handler.Unsubscribed.Task.IsCompleted);

        await client.DisposeAsync();

        await handler.Unsubscribed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task SubscribePreview_ZeroStopsSubscription()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        await client.SubscribePreviewAsync(12, CancellationToken.None);
        await handler.Subscribed.Task.WaitAsync(Wait);
        await client.SubscribePreviewAsync(0, CancellationToken.None);

        await handler.Unsubscribed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task ServerStops_ClientRaisesDisconnected()
    {
        var name = NewPipeName();
        var server = new ControlServer(name, new FakeHandler());
        server.Start();
        await using var client = await ConnectAsync(name);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();

        server.Dispose();

        await disconnected.Task.WaitAsync(Wait);
        await Assert.ThrowsAsync<ControlException>(() => client.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FiveClients_FifthWaits()
    {
        var name = NewPipeName();
        using var server = new ControlServer(name, new FakeHandler());
        server.Start();

        var clients = new List<ControlClient>();
        try
        {
            for (var i = 0; i < 4; i++)
                clients.Add(await ConnectAsync(name));

            var fifth = ConnectAsync(name);
            var early = await Task.WhenAny(fifth, Task.Delay(500));
            Assert.NotSame(fifth, early);

            await clients[0].DisposeAsync();
            clients.RemoveAt(0);

            var accepted = await fifth.WaitAsync(Wait);
            clients.Add(accepted);
            Assert.True((await accepted.GetStatusAsync(CancellationToken.None)).Emulated);
        }
        finally
        {
            foreach (var c in clients)
                await c.DisposeAsync();
        }
    }
}
