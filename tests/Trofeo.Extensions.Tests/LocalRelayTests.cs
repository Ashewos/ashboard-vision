using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AshboardVision.Sdk;
using Microsoft.Win32.SafeHandles;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Le relais « Cet ordinateur » : seuls les ports saisis dans les champs « port » des réglages de la case qui les demande sont
/// relayés, et seulement avec la permission, 16 connexions à la fois au plus. Les tests du relais seul ont un faux « hôte » dans ce
/// processus-ci (les poignées dupliquées lui sont donc directement utilisables) ; ceux du superviseur ajoutent le bon rattachement des
/// réglages (instance, action de la fenêtre) ; un test relie le vrai hôte à un vrai petit serveur HTTP.
/// </summary>
[Collection("Hosts")]
public sealed class LocalRelayTests : IAsyncLifetime
{
    private readonly ModuleSandbox _folders = new();
    private readonly List<EchoServer> _servers = [];
    private readonly List<IAsyncDisposable> _rigs = [];
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var server in _servers)
        {
            server.Dispose();
        }

        _folders.Dispose();
    }

    private EchoServer NewServer()
    {
        var server = new EchoServer();
        _servers.Add(server);
        return server;
    }

    private (LocalRelay Relay, List<HostMessage> Sent) NewRelay(bool permitted = true, params PortField[] fields)
    {
        var sent = new List<HostMessage>();
        var relay = new LocalRelay(fields.Length > 0 ? fields : [new PortField("port", "Port", 25565)], permitted,
            handle => Win32.DuplicateInto(handle).ToInt64(), message =>
            {
                lock (sent)
                {
                    sent.Add(message);
                }
            });
        _disposables.Add(relay);
        return (relay, sent);
    }

    private static async Task<T> WaitForAsync<T>(List<HostMessage> sent, Func<T, bool>? filter = null) where T : HostMessage
    {
        T? found = null;
        await TestWait.UntilAsync(() =>
        {
            lock (sent)
            {
                found = sent.OfType<T>().FirstOrDefault(message => filter?.Invoke(message) ?? true);
            }

            return found is not null;
        }, null, () => "Reçus : " + string.Join(" | ", sent.Select(m => m.ToString())));
        return found!;
    }

    private static (Stream Read, Stream Write) Streams(LocalConnected connected) =>
        (new AnonymousPipeClientStream(PipeDirection.In, new SafePipeHandle(new IntPtr(connected.ReadHandle), true)),
            new AnonymousPipeClientStream(PipeDirection.Out, new SafePipeHandle(new IntPtr(connected.WriteHandle), true)));

    private static async Task<string> RoundTripAsync(Stream read, Stream write, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await write.WriteAsync(bytes);
        await write.FlushAsync();
        var buffer = new byte[bytes.Length];
        await Task.Run(() => read.ReadExactly(buffer)).WaitAsync(TestWait.Patience);
        return Encoding.UTF8.GetString(buffer);
    }

    [WindowsHostFact]
    public async Task Relay_EchoServer_RoundTrip()
    {
        var server = NewServer();
        var (relay, sent) = NewRelay();
        var settings = ModuleSandbox.Json($$"""{"port":{{server.Port}}}""");

        relay.Connect(new LocalConnect(7, Guid.NewGuid(), server.Port), settings);

        var connected = await WaitForAsync<LocalConnected>(sent);
        Assert.Equal(7, connected.Id);
        var (read, write) = Streams(connected);
        using (read)
        {
            Assert.Equal("bonjour le serveur", await RoundTripAsync(read, write, "bonjour le serveur"));
            Assert.Equal("encore", await RoundTripAsync(read, write, "encore"));
            Assert.Equal(1, relay.OpenConnections);

            // Quand le module ferme son écriture, le serveur le voit, ferme à son tour, et le module lit la fin du flux.
            write.Dispose();
            Assert.Equal(-1, await Task.Run(() => read.ReadByte()).WaitAsync(TestWait.Patience));
        }

        await TestWait.UntilAsync(() => relay.OpenConnections == 0);
    }

    [WindowsHostFact]
    public async Task Relay_PortNotInInstanceSettings_IsRefused()
    {
        var typed = NewServer();
        var other = NewServer();
        var (relay, sent) = NewRelay();
        var settings = ModuleSandbox.Json($$"""{"port":{{typed.Port}}}""");

        relay.Connect(new LocalConnect(1, Guid.NewGuid(), other.Port), settings); // un port que l'utilisateur n'a pas saisi
        relay.Connect(new LocalConnect(2, Guid.NewGuid(), 0), settings);
        relay.Connect(new LocalConnect(3, Guid.NewGuid(), 70000), settings);

        foreach (var id in new[] { 1, 2, 3 })
        {
            Assert.Equal("Port non autorisé", (await WaitForAsync<LocalRefused>(sent, refused => refused.Id == id)).Reason);
        }

        Assert.Equal(0, other.Accepted); // et rien n'a même essayé de s'y connecter
        Assert.Equal(0, relay.OpenConnections);
    }

    [WindowsHostFact]
    public void IsAllowedPort_ReadsEveryPortFieldOfTheInstanceSettings()
    {
        PortField[] fields = [new PortField("port", "Port", 25565), new PortField("web.port", "Web", 8123)];

        Assert.True(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("""{"port":1234}"""), 1234));
        Assert.True(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("""{"web":{"port":9000}}"""), 9000));
        Assert.True(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("{}"), 25565)); // réglage jamais écrit : la valeur proposée
        Assert.True(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("{}"), 8123));
        Assert.False(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("""{"port":1234}"""), 25565)); // elle a été changée
        Assert.False(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("""{"port":"1234"}"""), 1234)); // pas un nombre
        Assert.False(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("""{"port":1234.5}"""), 1234));
        Assert.False(LocalRelay.IsAllowedPort(fields, ModuleSandbox.Json("""{"autre":1234}"""), 1234));
        Assert.False(LocalRelay.IsAllowedPort(fields, default, 1234));
        Assert.False(LocalRelay.IsAllowedPort([], ModuleSandbox.Json("""{"port":1234}"""), 1234));
    }

    [WindowsHostFact]
    public async Task Relay_PermissionNotGranted_IsRefused()
    {
        var server = NewServer();
        var (relay, sent) = NewRelay(permitted: false);

        relay.Connect(new LocalConnect(1, Guid.NewGuid(), server.Port), ModuleSandbox.Json($$"""{"port":{{server.Port}}}"""));

        Assert.Equal("Port non autorisé", (await WaitForAsync<LocalRefused>(sent)).Reason);
        Assert.Equal(0, server.Accepted);
    }

    [WindowsHostFact]
    public async Task Relay_NothingListening_IsRefusedAndFreesItsSlot()
    {
        int free;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            free = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var (relay, sent) = NewRelay();
        relay.Connect(new LocalConnect(1, Guid.NewGuid(), free), ModuleSandbox.Json($$"""{"port":{{free}}}"""));

        Assert.Equal("Connexion impossible", (await WaitForAsync<LocalRefused>(sent)).Reason);
        await TestWait.UntilAsync(() => relay.OpenConnections == 0);
    }

    [WindowsHostFact]
    public async Task Relay_SeventeenthConnection_IsRefused()
    {
        var server = NewServer();
        var (relay, sent) = NewRelay();
        var settings = ModuleSandbox.Json($$"""{"port":{{server.Port}}}""");
        var open = new List<(Stream Read, Stream Write)>();

        for (var i = 1; i <= LocalRelay.MaxConnections; i++)
        {
            relay.Connect(new LocalConnect(i, Guid.NewGuid(), server.Port), settings);
            open.Add(Streams(await WaitForAsync<LocalConnected>(sent, connected => connected.Id == i)));
        }

        relay.Connect(new LocalConnect(17, Guid.NewGuid(), server.Port), settings);

        Assert.Equal("Trop de connexions ouvertes", (await WaitForAsync<LocalRefused>(sent, refused => refused.Id == 17)).Reason);
        Assert.Equal(16, relay.OpenConnections);

        // Une connexion fermée libère sa place.
        open[0].Write.Dispose();
        open[0].Read.Dispose();
        await TestWait.UntilAsync(() => relay.OpenConnections == 15);
        relay.Connect(new LocalConnect(18, Guid.NewGuid(), server.Port), settings);
        await WaitForAsync<LocalConnected>(sent, connected => connected.Id == 18);

        foreach (var (read, write) in open.Skip(1))
        {
            read.Dispose();
            write.Dispose();
        }
    }

    [WindowsHostFact]
    public async Task Relay_Dispose_ClosesEveryConnection()
    {
        var server = NewServer();
        var (relay, sent) = NewRelay();
        relay.Connect(new LocalConnect(1, Guid.NewGuid(), server.Port), ModuleSandbox.Json($$"""{"port":{{server.Port}}}"""));
        var (read, write) = Streams(await WaitForAsync<LocalConnected>(sent));
        Assert.Equal("a", await RoundTripAsync(read, write, "a"));

        relay.Dispose();

        Assert.Equal(-1, await Task.Run(() => read.ReadByte()).WaitAsync(TestWait.Patience));
        write.Dispose();
        read.Dispose();
    }

    // --- Avec le superviseur : à qui appartiennent les réglages qui font foi -----------------------------------------------------

    private (SupervisorRig Rig, FakeHostFactory Factory) FakeSupervisor(bool permitted = true)
    {
        var manifest = ModuleSandbox.Manifest(entry: "module.dll", permissions: [ModulePermission.ThisComputer], fields: [new PortField("port", "Port", 25565)]);
        var entry = ModuleSandbox.Ready(_folders.SmallModule(), manifest, permitted ? [ModulePermission.ThisComputer] : []);
        var factory = new FakeHostFactory();
        var rig = new SupervisorRig(entry, factory);
        _rigs.Add(rig);
        return (rig, factory);
    }

    private static async Task<Open> OpenInstanceAsync(SupervisorRig rig, FakeHostProcess? host, FakeHostFactory factory, int port)
    {
        var module = rig.Open(settings: $$"""{"port":{{port}}}""");
        using (rig.Draw(module))
        {
        }

        host ??= await factory.WaitForStartAsync();
        return await host.WaitAsync<Open>(open => open.Settings.GetProperty("port").GetInt32() == port);
    }

    [WindowsHostFact]
    public async Task Relay_PortChangedInSettings_NewPortAllowedOldRefused()
    {
        var oldServer = NewServer();
        var newServer = NewServer();
        var (rig, factory) = FakeSupervisor();
        var first = rig.Open(settings: $$"""{"port":{{oldServer.Port}}}""");
        using (rig.Draw(first))
        {
        }

        var host = await factory.WaitForStartAsync();
        var oldOpen = await host.WaitAsync<Open>();

        await host.SendAsync(new LocalConnect(1, oldOpen.InstanceId, oldServer.Port));
        await host.SendAsync(new LocalConnect(2, oldOpen.InstanceId, newServer.Port));
        await host.WaitAsync<LocalConnected>(connected => connected.Id == 1);
        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 2)).Reason);

        // L'utilisateur change le port : la disposition recrée la case avec les nouveaux réglages (l'hôte reste le même).
        var newOpen = await OpenInstanceAsync(rig, host, factory, newServer.Port);
        first.Dispose();
        await host.SendAsync(new LocalConnect(3, newOpen.InstanceId, newServer.Port));
        await host.SendAsync(new LocalConnect(4, newOpen.InstanceId, oldServer.Port));
        await host.WaitAsync<LocalConnected>(connected => connected.Id == 3);
        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 4)).Reason);

        // Une instance ne prend pas le port d'une autre : l'ancienne instance n'existe plus.
        await host.SendAsync(new LocalConnect(5, oldOpen.InstanceId, oldServer.Port));
        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 5)).Reason);
        Assert.Equal(1, factory.Count);
    }

    [WindowsHostFact]
    public async Task Relay_EachInstanceHasItsOwnPorts()
    {
        var serverA = NewServer();
        var serverB = NewServer();
        var (rig, factory) = FakeSupervisor();
        var openA = await OpenInstanceAsync(rig, null, factory, serverA.Port);
        var host = factory.Last;
        var openB = await OpenInstanceAsync(rig, host, factory, serverB.Port);

        await host.SendAsync(new LocalConnect(1, openA.InstanceId, serverB.Port)); // le port de l'autre case
        await host.SendAsync(new LocalConnect(2, openB.InstanceId, serverB.Port));

        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 1)).Reason);
        await host.WaitAsync<LocalConnected>(connected => connected.Id == 2);
    }

    [WindowsHostFact]
    public async Task Relay_FromAnEmptyOrUnknownInstance_IsRefused()
    {
        var server = NewServer();
        var (rig, factory) = FakeSupervisor();
        await OpenInstanceAsync(rig, null, factory, server.Port);
        var host = factory.Last;

        await host.SendAsync(new LocalConnect(1, Guid.Empty, server.Port)); // la recherche d'un champ n'a pas d'instance
        await host.SendAsync(new LocalConnect(2, Guid.NewGuid(), server.Port));

        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 1)).Reason);
        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 2)).Reason);
        Assert.Equal(0, server.Accepted);
    }

    [WindowsHostFact]
    public async Task Relay_WithoutThePermissionGranted_IsRefusedByTheSupervisor()
    {
        var server = NewServer();
        var (rig, factory) = FakeSupervisor(permitted: false);
        var open = await OpenInstanceAsync(rig, null, factory, server.Port);
        var host = factory.Last;

        await host.SendAsync(new LocalConnect(1, open.InstanceId, server.Port));

        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>()).Reason);
        Assert.Equal(0, server.Accepted);
    }

    [WindowsHostFact]
    public async Task Relay_DuringARunAction_UsesTheSettingsSentWithThatAction()
    {
        var typed = NewServer();
        var other = NewServer();
        var (rig, factory) = FakeSupervisor();
        var module = rig.Open();
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Hello>();
        var supervisorFactory = new RemoteModuleFactory(ModuleSandbox.Ready(_folders.SmallModule(), ModuleSandbox.Manifest(entry: "module.dll")), rig.Supervisor);

        var action = supervisorFactory.RunActionAsync("tester", ModuleSandbox.Json($$"""{"port":{{typed.Port}}}"""), rig.Secrets, CancellationToken.None);
        var run = await host.WaitAsync<RunAction>();

        // Pendant l'action, le port saisi dans la fenêtre (pas encore enregistré) est celui qui compte.
        await host.SendAsync(new LocalConnect(1, run.InstanceId, typed.Port));
        await host.SendAsync(new LocalConnect(2, run.InstanceId, other.Port));
        await host.WaitAsync<LocalConnected>(connected => connected.Id == 1);
        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 2)).Reason);

        await host.SendAsync(new ActionReply(run.Id, new ActionResult(true, "Fait.")));
        Assert.True((await action).Ok);

        // L'action finie, son identifiant ne vaut plus rien.
        await host.SendAsync(new LocalConnect(3, run.InstanceId, typed.Port));
        Assert.Equal("Port non autorisé", (await host.WaitAsync<LocalRefused>(refused => refused.Id == 3)).Reason);
        module.Dispose();
    }

    [WindowsHostFact]
    public async Task Relay_WithTheRealHost_ReachesALocalHttpServerThroughTheRelay()
    {
        using var web = new HttpLikeServer("hello-from-the-pc");
        var manifest = ModuleSandbox.Manifest(permissions: [ModulePermission.ThisComputer], fields: [new PortField("port", "Port", 25565)]);
        var entry = ModuleSandbox.Ready(_folders.SampleCopy(), manifest);
        var factory = new PlainHostProcessFactory();
        var rig = new SupervisorRig(entry, factory);
        _rigs.Add(rig);
        var module = rig.Open(settings: $$"""{"mode":"http","port":{{web.Port}},"url":"http://127.0.0.1:{{web.Port}}/"}""");
        using (rig.Draw(module))
        {
        }

        await TestWait.UntilAsync(() => rig.Log.Entries.Any(line => line.Contains("http:hello-from-the-pc", StringComparison.Ordinal)), null,
            () => string.Join(" | ", rig.Log.Entries));
    }

    // --- Des serveurs pour les tests -------------------------------------------------------------------------------------------------

    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _accepted;

        public EchoServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                await stream.CopyToAsync(stream, _stop.Token);
                            }
                            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                            {
                            }
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Un serveur qui répond à n'importe quelle requête par une réponse HTTP minimale puis ferme.</summary>
    private sealed class HttpLikeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _body;

        public HttpLikeServer(string body)
        {
            _body = body;
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose() => _listener.Stop();

        private async Task AcceptLoop()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                var buffer = new byte[4096];
                                _ = await stream.ReadAsync(buffer);
                                var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n{_body}";
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                                await stream.FlushAsync();
                            }
                            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                            {
                            }
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
