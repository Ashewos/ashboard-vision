using System.IO.Pipes;
using System.Text.Json;

namespace Trofeo.Control.Tests;

// Les vues dans le canal de contrôle (protocole 4) : changer de vue depuis la fenêtre, apprendre les changements de vue, et
// demander l'aperçu d'une vue précise.
public partial class ControlChannelTests
{
    private static readonly Guid ViewA = new("11110000-0000-0000-0000-000000000001");
    private static readonly Guid ViewB = new("22220000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Status_CarriesTheDisplayedView()
    {
        var name = NewPipeName();
        using var server = new ControlServer(name, new FakeHandler());
        server.Start();
        await using var client = await ConnectAsync(name);

        var status = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal(CurrentViewId, status.CurrentViewId);
    }

    [Fact]
    public async Task ShowView_UnknownId_ReturnsFalse()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { OnShowView = id => id == ViewB };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var unknown = Guid.NewGuid();
        Assert.False(await client.ShowViewAsync(unknown, CancellationToken.None));
        Assert.True(await client.ShowViewAsync(ViewB, CancellationToken.None));

        Assert.Equal([unknown, ViewB], handler.ShownViews);
    }

    [Fact]
    public async Task ShowView_WithoutViewId_IsAnErrorAndTheLinkStaysUp()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();

        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync(5000);
        await SendRawAsync(raw, new { id = 1, type = "showView" });
        await SendRawAsync(raw, new { id = 2, type = "status" });

        using var cts = new CancellationTokenSource(Wait);
        var first = await Framing.ReadAsync(raw, cts.Token);
        using var error = JsonDocument.Parse(first!.Value.Payload);
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Contains("viewId", error.RootElement.GetProperty("message").GetString());
        var second = await Framing.ReadAsync(raw, cts.Token);
        using var status = JsonDocument.Parse(second!.Value.Payload);
        Assert.Equal("status", status.RootElement.GetProperty("type").GetString()); // la connexion est restée utilisable
        Assert.Empty(handler.ShownViews);
    }

    [Fact]
    public async Task ViewShown_IsPushedOnKeyboardSwitch()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var first = await ConnectAsync(name);
        await using var second = await ConnectAsync(name);
        var seenByFirst = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenBySecond = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ViewShown += id => seenByFirst.TrySetResult(id);
        second.ViewShown += id => seenBySecond.TrySetResult(id);

        handler.RaiseViewChanged(ViewB); // le raccourci clavier a changé de vue : aucune requête de ces clients n'est en cours

        Assert.Equal(ViewB, await seenByFirst.Task.WaitAsync(Wait));
        Assert.Equal(ViewB, await seenBySecond.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task ViewShown_ManyChanges_EndOnTheLastOne()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);
        var seen = new List<Guid>();
        var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var final = Guid.NewGuid();
        client.ViewShown += id =>
        {
            lock (seen) seen.Add(id);
            if (id == final) last.TrySetResult();
        };

        for (var i = 0; i < 50; i++) handler.RaiseViewChanged(Guid.NewGuid());
        handler.RaiseViewChanged(final);

        await last.Task.WaitAsync(Wait); // un client lent peut manquer des étapes, jamais la dernière
        lock (seen) Assert.Equal(final, seen[^1]);
    }

    [Fact]
    public async Task ViewShown_DoesNotDisturbARequestInFlight()
    {
        var name = NewPipeName();
        var release = new TaskCompletionSource<Reloaded>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler { OnReload = _ => release.Task };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);
        var pushed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ViewShown += id => pushed.TrySetResult(id);

        var reload = client.ReloadAsync(CancellationToken.None);
        handler.RaiseViewChanged(ViewA);
        Assert.Equal(ViewA, await pushed.Task.WaitAsync(Wait));
        Assert.False(reload.IsCompleted); // l'annonce n'a pas pris la place de la réponse attendue

        release.SetResult(new Reloaded(true, null, []));
        Assert.True((await reload.WaitAsync(Wait)).Applied);
    }

    [Fact]
    public void ServerDispose_StopsListeningToViewChanges()
    {
        var handler = new FakeHandler();
        var server = new ControlServer(NewPipeName(), handler);
        server.Start();
        Assert.True(handler.HasViewSubscribers);

        server.Dispose();

        Assert.False(handler.HasViewSubscribers);
    }

    [Fact]
    public async Task Preview_OfAChosenView_PassesTheViewToTheService()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        await client.SubscribePreviewAsync(8, ViewB, CancellationToken.None);

        await handler.Subscribed.Task.WaitAsync(Wait);
        Assert.Equal(8, handler.SubscribedMaxFps);
        Assert.Equal(ViewB, handler.SubscribedViewId);
    }

    [Fact]
    public async Task Preview_WithoutAView_FollowsTheDisplayedOne()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        await client.SubscribePreviewAsync(8, CancellationToken.None);

        await handler.Subscribed.Task.WaitAsync(Wait);
        Assert.Null(handler.SubscribedViewId);
    }

    [Fact]
    public async Task Preview_OfAMissingView_IsAnErrorTheClientCanRead()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { SubscribeError = new InvalidOperationException("Cette vue n'existe pas.") };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var ex = await Assert.ThrowsAsync<ControlException>(() => client.SubscribePreviewAsync(8, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("Cette vue n'existe pas.", ex.Message);
    }
}
