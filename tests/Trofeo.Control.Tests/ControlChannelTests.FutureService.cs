using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Trofeo.Control.Tests;

// Un service plus récent que la fenêtre : sa réponse contient des choses que cette fenêtre ne sait pas lire (un genre de champ de
// réglage inconnu, par exemple). L'utilisateur doit lire un message clair, pas voir une liaison qui « se coupe ».
public partial class ControlChannelTests
{
    /// <summary>Un faux service qui répond à chaque requête par le JSON voulu (le <c>replyTo</c> est celui de la requête).</summary>
    private static async Task<NamedPipeServerStream> ServeRawAsync(string name, Func<string, int, string?> reply, CancellationToken ct)
    {
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _ = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
                while (await Framing.ReadAsync(server, ct) is { } frame)
                {
                    using var request = JsonDocument.Parse(frame.Payload);
                    var id = request.RootElement.GetProperty("id").GetInt32();
                    var type = request.RootElement.GetProperty("type").GetString()!;
                    if (reply(type, id) is { } json)
                        await Framing.WriteJsonAsync(server, Encoding.UTF8.GetBytes(json), ct);
                }
            }
            catch (Exception)
            {
                // le test a fini
            }
        }, ct);
        return server;
    }

    private static string Reply(int id, string type, string body) => "{\"replyTo\":" + id + ",\"type\":\"" + type + "\"," + body + "}";

    private const string UnknownKindType =
        "{\"typeId\":\"alex.demo\",\"displayName\":\"Démo\",\"minSize\":{\"columns\":2,\"rows\":1},\"presets\":{},\"acceptsCommands\":false,"
        + "\"schema\":{\"fields\":[{\"kind\":\"couleur\",\"key\":\"c\",\"label\":\"Couleur\"}]}}";

    [Fact]
    public async Task Hello_NewerServiceWithAnUnknownFieldKind_IsMismatch_NotALostConnection()
    {
        var name = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var newer = ControlPipe.ProtocolVersion + 1;
        await using var server = await ServeRawAsync(name, (type, id) => type != "hello" ? null : Reply(id, "hello",
            "\"welcome\":{\"serviceVersion\":\"9.0.0\",\"protocolVersion\":" + newer + ",\"types\":[" + UnknownKindType + "]}"), cts.Token);

        var ex = await Assert.ThrowsAsync<ProtocolMismatchException>(() => ConnectAsync(name));

        Assert.Contains($"version {newer}", ex.Message);
        Assert.Contains($"version {ControlPipe.ProtocolVersion}", ex.Message);
    }

    [Fact]
    public async Task Request_ResponseThatCannotBeRead_IsAClearErrorAndTheLinkStaysUp()
    {
        var name = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = await ServeRawAsync(name, (type, id) => type switch
        {
            "hello" => Reply(id, "hello", "\"welcome\":{\"serviceVersion\":\"1.0.0\",\"protocolVersion\":" + ControlPipe.ProtocolVersion + ",\"types\":[]}"),
            "types" => Reply(id, "types", "\"types\":[" + UnknownKindType + "]"),
            _ => Reply(id, "consumption", "\"consumption\":[]"),
        }, cts.Token);
        await using var client = await ConnectAsync(name);

        var ex = await Assert.ThrowsAsync<ControlException>(() => client.GetTypesAsync(cts.Token));

        Assert.Contains("mets les deux à jour", ex.Message);
        Assert.Empty(await client.GetConsumptionAsync(cts.Token)); // la liaison n'a pas été coupée pour autant
    }
}
