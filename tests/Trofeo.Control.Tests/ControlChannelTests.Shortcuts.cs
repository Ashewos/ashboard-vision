using System.IO.Pipes;
using System.Text.Json;

namespace Trofeo.Control.Tests;

// Les raccourcis dans le canal de contrôle (protocole 4) : essayer une touche, et lire les touches que Windows a refusées.
public partial class ControlChannelTests
{
    [Fact]
    public async Task Status_CarriesTheRefusedShortcutKeys()
    {
        var name = NewPipeName();
        using var server = new ControlServer(name, new FakeHandler());
        server.Start();
        await using var client = await ConnectAsync(name);

        var status = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal(["Ctrl+Alt+Right"], status.RefusedShortcutKeys);
    }

    [Fact]
    public async Task TestShortcutKeys_RoundTripsEveryState()
    {
        var name = NewPipeName();
        var handler = new FakeHandler
        {
            OnTestKeys = keys => keys switch
            {
                "Ctrl+Alt+Shift+R" => ShortcutKeyState.TakenByAnotherProgram,
                "???" => ShortcutKeyState.Unreadable,
                _ => ShortcutKeyState.Free,
            },
        };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        Assert.Equal(ShortcutKeyState.TakenByAnotherProgram, await client.TestShortcutKeysAsync("Ctrl+Alt+Shift+R", CancellationToken.None));
        Assert.Equal(ShortcutKeyState.Unreadable, await client.TestShortcutKeysAsync("???", CancellationToken.None));
        Assert.Equal(ShortcutKeyState.Free, await client.TestShortcutKeysAsync("Ctrl+Alt+Shift+T", CancellationToken.None));

        Assert.Equal(["Ctrl+Alt+Shift+R", "???", "Ctrl+Alt+Shift+T"], handler.TestedKeys);
    }

    [Fact]
    public async Task TestShortcutKeys_HandlerFails_IsAControlException()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { OnTestKeys = _ => throw new InvalidOperationException("Les raccourcis ne sont pas disponibles.") };
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var client = await ConnectAsync(name);

        var error = await Assert.ThrowsAsync<ControlException>(() => client.TestShortcutKeysAsync("Ctrl+Alt+Shift+R", CancellationToken.None));

        Assert.Contains("raccourcis", error.Message);
    }

    [Fact]
    public async Task TestShortcutKeys_WithoutKeys_IsAnError_AndTheLinkStaysUp()
    {
        var name = NewPipeName();
        var handler = new FakeHandler();
        using var server = new ControlServer(name, handler);
        server.Start();

        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync(5000);
        await SendRawAsync(raw, new { id = 1, type = "testShortcutKeys" });
        await SendRawAsync(raw, new { id = 2, type = "status" });

        using var cts = new CancellationTokenSource(Wait);
        var first = await Framing.ReadAsync(raw, cts.Token);
        using var error = JsonDocument.Parse(first!.Value.Payload);
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Contains("keys", error.RootElement.GetProperty("message").GetString());
        var second = await Framing.ReadAsync(raw, cts.Token);
        using var status = JsonDocument.Parse(second!.Value.Payload);
        Assert.Equal("status", status.RootElement.GetProperty("type").GetString());
        Assert.Empty(handler.TestedKeys);
    }
}
