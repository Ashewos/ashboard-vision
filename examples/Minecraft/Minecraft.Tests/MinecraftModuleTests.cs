using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AshboardVision.Sdk;
using AshboardVision.Sdk.Testing;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace Minecraft.Tests;

/// <summary>
/// Les tests du module « Serveur Minecraft ». Ils ne joignent jamais un vrai serveur ni Internet : le module parle à un faux serveur TCP qui écoute sur
/// <c>127.0.0.1</c>. Un serveur « de ce PC » passe, comme dans le logiciel, par <c>Services.ConnectLocalAsync</c> (le faux de <c>ModuleTestHost</c> applique
/// la règle des ports du vrai relais) ; un autre serveur passe par <see cref="TestableMinecraftModule"/>, qui redirige la connexion vers le faux serveur.
/// Le module actualise ses données en tâche de fond : les tests attendent la fin de l'actualisation (<see cref="Running.Refreshed"/>) au lieu de sonder.
/// </summary>
public sealed class MinecraftModuleTests
{
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifest.json");

    private static readonly ModulePermission[] Everything = [ModulePermission.ThisComputer, ModulePermission.LocalNetwork, ModulePermission.Internet];

    private static bool HasMagenta(SKBitmap image) => image.Pixels.Any(pixel => pixel.Alpha == 255 && pixel.Red > 200 && pixel.Green < 60 && pixel.Blue > 200);

    private static string Settings(string address, int port) => $$"""{ "adresse": {{JsonSerializer.Serialize(address)}}, "port": {{port}} }""";

    private static string StatusJson(string? favicon = null) => $$"""
        {
          "version": { "name": "Paper 1.21.4", "protocol": 769 },
          "players": { "max": 20, "online": 4, "sample": [ { "name": "Alex", "id": "1" }, { "name": "Steve", "id": "2" }, { "name": "Notch", "id": "3" }, { "name": "Herobrine", "id": "4" } ] },
          "description": { "text": "§6Bienvenue ", "extra": [ { "text": "sur Ashboard", "color": "green" } ] }
          {{(favicon is null ? "" : $", \"favicon\": \"{favicon}\"")}}
        }
        """;

    /// <summary>Un module qui tourne, avec son hôte de test et ce que le test lui a prêté.</summary>
    private sealed class Running(ModuleTestHost<TestableMinecraftModule> host, MinecraftProbe probe) : IAsyncDisposable
    {
        public ModuleTestHost<TestableMinecraftModule> Host => host;

        public MinecraftProbe Probe => probe;

        public MinecraftModule Module => probe.Module ?? throw new InvalidOperationException("Le module n'est pas créé.");

        /// <summary>L'actualisation en cours (ou la dernière) : attends-la avant de lire l'état.</summary>
        public Task Refreshed => Module.LastRefresh;

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    private static async Task<Running> StartAsync(
        string settings, IReadOnlyList<ModulePermission>? granted = null, Action<MinecraftProbe>? prepare = null)
    {
        var probe = new MinecraftProbe();
        prepare?.Invoke(probe);
        TestableMinecraftModule.Current.Value = probe;
        var host = new ModuleTestHost<TestableMinecraftModule>(ManifestPath, JsonDocument.Parse(settings).RootElement.Clone(), granted ?? Everything);
        await host.StartAsync();
        return new Running(host, probe);
    }

    // ---- Ce que la case montre ----

    [Theory]
    [InlineData("classique")]
    [InlineData("liquid-glass")]
    [InlineData("pixel")]
    public async Task Module_ShowsPlayersAndIcon(string theme)
    {
        await using var server = FakeMinecraftServer.Answering(StatusJson(ServerListPingTests.Favicon(ServerListPingTests.Png(SKColors.Magenta))));
        await using var running = await StartAsync(Settings("localhost", server.Port));

        await running.Refreshed;

        // Ce que le module a retenu du serveur, puis les lignes qu'il en tire : le nom sans code de couleur, « 4 / 20 joueurs », trois pseudos au plus.
        var status = running.Module.Status!;
        Assert.Equal("Bienvenue sur Ashboard", status.Motd);
        Assert.Equal(["Alex", "Steve", "Notch", "Herobrine"], status.SamplePlayers);
        Assert.Equal(ModuleHealth.Online, running.Host.Health);
        var lines = MinecraftModule.Lines("localhost", status, Themes.Find(theme)!.Theme).Select(line => line.Text).ToArray();
        Assert.Equal(["Bienvenue sur Ashboard", "4 / 20 joueurs", "Alex, Steve, Notch"], lines[..3]);
        Assert.StartsWith("Paper 1.21.4 · ", lines[3], StringComparison.Ordinal);
        Assert.EndsWith(" ms", lines[3], StringComparison.Ordinal);
        Assert.Equal(4, lines.Length);

        // L'icône du serveur (ici un carré magenta, que les thèmes n'utilisent nulle part) est dans l'image.
        using var image = running.Host.Render(new GridSize(4, 1), theme);
        Assert.True(HasMagenta(image), "L'icône du serveur n'est pas dessinée.");
    }

    [Fact]
    public async Task Module_WithoutIcon_DrawsTextOnly()
    {
        await using var server = FakeMinecraftServer.Answering(StatusJson());
        await using var running = await StartAsync(Settings("localhost", server.Port));
        await running.Refreshed;

        using var image = running.Host.Render(new GridSize(4, 1), "classique");

        Assert.True(image.Pixels.Any(pixel => pixel.Alpha > 0), "Rien n'est dessiné.");
        Assert.False(HasMagenta(image));
    }

    [Fact]
    public async Task Module_NoPlayerNames_HasNoNamesLine()
    {
        await using var server = FakeMinecraftServer.Answering("""{ "players": { "online": 0, "max": 20 }, "description": "Vide" }""");
        await using var running = await StartAsync(Settings("localhost", server.Port));
        await running.Refreshed;

        var lines = MinecraftModule.Lines("localhost", running.Module.Status!, Themes.Find("classique")!.Theme).Select(line => line.Text).ToArray();

        Assert.Equal(["Vide", "0 / 20 joueurs"], lines[..2]);
        Assert.Equal(3, lines.Length); // pas de ligne de pseudos : la dernière est la latence
    }

    [Fact]
    public void Lines_WithoutMotd_ShowTheAddress()
    {
        var status = new ServerStatus("", "", 1, 5, [], TimeSpan.FromMilliseconds(12.4), null);

        var lines = MinecraftModule.Lines("mc.example.org", status, Themes.Find("classique")!.Theme).Select(line => line.Text);

        Assert.Equal(["mc.example.org", "1 / 5 joueurs", "12 ms"], lines);
    }

    // ---- Un serveur de ce PC, ou un autre ----

    /// <summary>Toutes les écritures de « ce PC » que le module reconnaît : l'adresse écrite dans le champ, et celle que la poignée de main annonce (sans crochets).</summary>
    public static TheoryData<string, string> ThisComputerForms => new()
    {
        { "localhost", "localhost" },
        { "LocalHost", "LocalHost" },
        { "LOCALHOST", "LOCALHOST" },
        { "localhost.", "localhost." },
        { "127.0.0.1", "127.0.0.1" },
        { "127.0.0.2", "127.0.0.2" },
        { "127.1", "127.1" },
        { "0.0.0.0", "0.0.0.0" },
        { "::1", "::1" },
        { "[::1]", "::1" },
        { "::ffff:127.0.0.1", "::ffff:127.0.0.1" },
        { "0:0:0:0:0:0:0:1", "0:0:0:0:0:0:0:1" },
        { "::", "::" },
    };

    [Theory]
    [MemberData(nameof(ThisComputerForms))]
    public async Task Module_ThisComputerAddresses_GoThroughTheRelay(string address, string announced)
    {
        await using var server = FakeMinecraftServer.Answering(StatusJson());
        await using var running = await StartAsync(Settings(address, server.Port));

        await running.Refreshed;

        // Le serveur a reçu la connexion du relais (du faux de ModuleTestHost) ; le module n'a jamais ouvert de connexion lui-même.
        Assert.Equal([new Handshake(-1, announced, server.Port, 1)], server.Handshakes);
        Assert.Empty(running.Probe.RemoteCalls);
        Assert.NotNull(running.Module.Status);
    }

    [Theory]
    [MemberData(nameof(ThisComputerForms))]
    public async Task Module_ThisComputerAddresses_WithoutThisComputer_SaysPermissionMissing(string address, string announced)
    {
        // Chaque écriture de ce PC demande la permission « Cet ordinateur » : sans elle, le module le dit (il ne tente pas une connexion directe,
        // que l'enfermement bloquerait, ce qui donnerait un « injoignable » trompeur).
        Assert.NotEmpty(announced);
        await using var running = await StartAsync(Settings(address, 25565), granted: [ModulePermission.LocalNetwork, ModulePermission.Internet]);

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Contains("Cet ordinateur", running.Host.StatusMessage);
        running.Host.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(running.Probe.RemoteCalls);
    }

    [Theory]
    [InlineData("mc.example.org")]
    [InlineData("192.168.1.20")]
    [InlineData("8.8.8.8")]
    [InlineData("127.example.org")]
    [InlineData("my-localhost")]
    public async Task Module_AddressesThatAreNotThisComputer_UseTheDirectConnection(string address)
    {
        await using var server = FakeMinecraftServer.Answering(StatusJson());
        await using var running = await StartAsync(Settings(address, 25570), prepare: probe => probe.RedirectTo = server.Port);

        await running.Refreshed;

        Assert.Equal([(address, 25570)], running.Probe.RemoteCalls);
    }

    [Theory]
    [InlineData(ModulePermission.Internet)]
    [InlineData(ModulePermission.LocalNetwork)]
    public async Task Module_OtherServer_UsesADirectConnection(ModulePermission network)
    {
        await using var server = FakeMinecraftServer.Answering(StatusJson());
        await using var running = await StartAsync(
            Settings("mc.example.org", 25570), granted: [network], prepare: probe => probe.RedirectTo = server.Port);

        await running.Refreshed;

        // Le module a demandé une connexion ordinaire vers l'adresse et le port des réglages, et s'est présenté au serveur sous ce nom.
        Assert.Equal([("mc.example.org", 25570)], running.Probe.RemoteCalls);
        Assert.Equal([new Handshake(-1, "mc.example.org", 25570, 1)], server.Handshakes);
        Assert.Equal("Bienvenue sur Ashboard", running.Module.Status!.Motd);
    }

    [Fact]
    public async Task Module_LocalhostWithoutThisComputer_SaysPermissionMissing()
    {
        // Le réseau local et Internet ne servent à rien pour joindre ce PC : seule la permission « Cet ordinateur » ouvre le relais.
        await using var server = FakeMinecraftServer.Answering(StatusJson());
        await using var running = await StartAsync(
            Settings("localhost", server.Port), granted: [ModulePermission.LocalNetwork, ModulePermission.Internet]);

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Contains("Cet ordinateur", running.Host.StatusMessage);
        running.Host.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(running.Module.Status);
        Assert.Empty(server.Handshakes);
        Assert.Empty(running.Probe.RemoteCalls);
    }

    [Fact]
    public async Task Module_OtherServerWithoutNetworkPermission_SaysPermissionMissing()
    {
        await using var running = await StartAsync(Settings("mc.example.org", 25565), granted: [ModulePermission.ThisComputer]);

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Contains("Réseau local", running.Host.StatusMessage);
        Assert.Contains("Internet", running.Host.StatusMessage);
        running.Host.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(running.Probe.RemoteCalls);
    }

    // ---- Les pannes, chacune avec sa phrase ----

    [Fact]
    public async Task Module_Unreachable_IsOffline()
    {
        // Un port libre qu'on vient de relâcher : rien n'y écoute. Le relais répond « refusé » : le serveur est injoignable (la permission est accordée).
        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await using var running = await StartAsync(Settings("localhost", closed));
        await running.Refreshed;

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Equal("Serveur injoignable", running.Host.StatusMessage);
    }

    [Fact]
    public async Task Module_RelayUnavailable_SaysTheSoftwareDoesNotAnswer()
    {
        // Le service d'Ashboard Vision n'a pas répondu : ce n'est pas le serveur qui est en cause, et le message le dit (autrement que « injoignable »).
        await using var running = await StartAsync(
            Settings("localhost", 25565), prepare: probe => probe.LocalResult = new DataResult<Stream>(DataStatus.Unavailable, null));

        await running.Refreshed;

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Contains("Ashboard Vision ne répond pas", running.Host.StatusMessage);
        Assert.DoesNotContain("injoignable", running.Host.StatusMessage);
    }

    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public async Task Module_OtherServerUnreachable_IsOffline(Exception failure)
    {
        await using var running = await StartAsync(Settings("mc.example.org", 25565), prepare: probe => probe.RemoteFailure = failure);
        await running.Refreshed;

        // Pour un serveur qui n'est pas sur ce PC, le message ajoute l'aide qui sort le plus souvent d'embarras.
        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.StartsWith("Serveur injoignable", running.Host.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Si le serveur tourne sur ce PC, écris localhost", running.Host.StatusMessage);
    }

    public static TheoryData<Exception> ConnectionFailures => new()
    {
        new SocketException((int)SocketError.HostNotFound),
        new SocketException((int)SocketError.ConnectionRefused),
        new TimeoutException(),
    };

    [Fact]
    public async Task Module_ServerThatNeverAnswers_SaysItDoesNotAnswer()
    {
        await using var server = FakeMinecraftServer.Silent();
        await using var running = await StartAsync(Settings("localhost", server.Port), prepare: probe => probe.QueryTimeout = TimeSpan.FromMilliseconds(300));

        await running.Refreshed;

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Contains("ne répond pas", running.Host.StatusMessage);
    }

    [Fact]
    public async Task Module_ServerThatHangsUp_SaysSo()
    {
        await using var server = FakeMinecraftServer.HangingUp();
        await using var running = await StartAsync(Settings("localhost", server.Port));

        await running.Refreshed;

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Contains("fermé la connexion", running.Host.StatusMessage);
    }

    [Theory]
    [MemberData(nameof(UnreadableAnswers))]
    public async Task Module_AnswerThatIsNotMinecraft_SaysTheAnswerIsUnreadable(byte[] answer)
    {
        await using var server = FakeMinecraftServer.Replying(answer);
        await using var running = await StartAsync(Settings("localhost", server.Port));

        await running.Refreshed;

        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Contains("illisible", running.Host.StatusMessage);
    }

    private static byte[] InvalidUtf8Answer()
    {
        byte[] json = [.. "{\"description\":\""u8, 0xFF, .. "\"}"u8];
        return Packets.Frame([0x00, .. Packets.VarInt(json.Length), .. json]);
    }

    public static TheoryData<byte[]> UnreadableAnswers => new()
    {
        Packets.Frame(Packets.StatusResponse("pas du JSON")),
        Packets.VarInt(int.MaxValue), // un paquet de 2 Gio annoncé
        new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }, // un VarInt de 7 octets
        InvalidUtf8Answer(), // un JSON dont une chaîne contient l'octet 0xFF
    };

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "adresse": "", "port": 25565 }""")]
    [InlineData("""{ "adresse": "   ", "port": 25565 }""")]
    public async Task Module_NoAddress_NotConfigured(string settings)
    {
        // « localhost » n'est que le texte grisé du champ : sans adresse, le module le dit au lieu d'essayer un serveur au hasard.
        await using var running = await StartAsync(settings);

        Assert.Equal(ModuleHealth.NotConfigured, running.Host.Health);
        Assert.Contains("Indique l'adresse", running.Host.StatusMessage);
        Assert.Null(running.Module.Status);
        Assert.Empty(running.Probe.RemoteCalls);
    }

    [Theory]
    [InlineData("pas une adresse", "adresse")]
    [InlineData("a b", "adresse")]
    [InlineData("x/y", "adresse")]
    public async Task Module_InvalidAddress_NotConfigured(string address, string expected)
    {
        await using var running = await StartAsync(Settings(address, 25565));

        Assert.Equal(ModuleHealth.NotConfigured, running.Host.Health);
        Assert.Contains(expected, running.Host.StatusMessage);
        Assert.Empty(running.Probe.RemoteCalls);
    }

    [Theory]
    [InlineData("mc.example.org:25565")]
    [InlineData("localhost:25565")]
    [InlineData("127.0.0.1:25565")]
    [InlineData("[::1]:25565")]
    public async Task Module_AddressWithAPort_SaysToUseThePortField(string address)
    {
        await using var running = await StartAsync(Settings(address, 25565));

        Assert.Equal(ModuleHealth.NotConfigured, running.Host.Health);
        Assert.Contains("champ « Port »", running.Host.StatusMessage);
        Assert.Empty(running.Probe.RemoteCalls);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("25565.5")]
    public async Task Module_InvalidPort_NotConfigured(string port)
    {
        await using var running = await StartAsync($$"""{ "adresse": "localhost", "port": {{port}} }""");

        Assert.Equal(ModuleHealth.NotConfigured, running.Host.Health);
        Assert.Contains("port", running.Host.StatusMessage);
    }

    // ---- Dans le temps ----

    [Fact]
    public async Task Module_RefreshesEveryThirtySeconds()
    {
        await using var server = FakeMinecraftServer.Answering(StatusJson());
        await using var running = await StartAsync(Settings("localhost", server.Port));
        await running.Refreshed;
        var first = running.Module.LastRefresh;

        running.Host.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(first, running.Module.LastRefresh); // pas d'actualisation avant 30 secondes

        running.Host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotSame(first, running.Module.LastRefresh);
        await running.Refreshed;
        Assert.Equal(2, server.Handshakes.Count);
    }

    [Fact]
    public async Task Module_AfterAnOutage_KeepsTheLastStatusAndComesBackOnline()
    {
        await using var server = FakeMinecraftServer.Answering(StatusJson());
        await using var running = await StartAsync(Settings("localhost", server.Port));
        await running.Refreshed;
        var known = running.Module.Status;

        // Le serveur raccroche : la dernière réponse reste connue (la case la garde), l'état dit ce qui ne va pas.
        server.Behavior = Behaviors.HangingUp;
        running.Host.Time.Advance(TimeSpan.FromSeconds(30));
        await running.Refreshed;
        Assert.Equal(ModuleHealth.Offline, running.Host.Health);
        Assert.Same(known, running.Module.Status);

        // Le serveur revient : à l'actualisation suivante, l'état redevient « en ligne », sans message.
        server.Behavior = Behaviors.Answering(StatusJson());
        running.Host.Time.Advance(TimeSpan.FromSeconds(30));
        await running.Refreshed;
        Assert.Equal(ModuleHealth.Online, running.Host.Health);
        Assert.Null(running.Host.StatusMessage);
        Assert.NotSame(known, running.Module.Status);
    }

    [Fact]
    public async Task Module_TwoCases_AskTheirOwnServer()
    {
        // Deux serveurs, deux cases : chacune a son adresse et son port.
        await using var first = FakeMinecraftServer.Answering("""{ "description": "Premier", "players": { "online": 1, "max": 10 } }""");
        await using var second = FakeMinecraftServer.Answering("""{ "description": "Second", "players": { "online": 2, "max": 10 } }""");
        await using var one = await StartAsync(Settings("localhost", first.Port));
        await using var two = await StartAsync(Settings("localhost", second.Port));

        await one.Refreshed;
        await two.Refreshed;

        Assert.Equal("Premier", one.Module.Status!.Motd);
        Assert.Equal("Second", two.Module.Status!.Motd);
        Assert.Single(first.Handshakes);
        Assert.Single(second.Handshakes);
    }

    [Fact]
    public async Task Module_DisposedDuringARequest_EndsCleanly()
    {
        // La fermeture arrive pendant que le serveur n'a pas répondu : elle interrompt la demande, rend la main, et aucune exception ne reste dans
        // une tâche sans que personne la lise (l'hôte du logiciel s'arrêterait sur une exception non gérée d'un thread du module).
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.Add(e.Exception);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await using var server = FakeMinecraftServer.Silent();
            await using var running = await StartAsync(Settings("localhost", server.Port)); // libéré aussi si une assertion échoue avant la fermeture
            for (var i = 0; i < 500 && server.Handshakes.Count == 0; i++)
            {
                await Task.Delay(10); // la demande est partie (le serveur l'a lue) et ne recevra pas de réponse
            }

            Assert.Single(server.Handshakes);

            var closing = running.DisposeAsync().AsTask();
            Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(5))));
            await closing;

            // Une minuterie qui se déclenche après la fermeture ne relance rien.
            running.Host.Time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(100); // on attend « pour de vrai » qu'une connexion à tort ait eu le temps de partir
            Assert.Single(server.Handshakes);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }
}

/// <summary>Ce qu'un test prête au module (connexion redirigée, panne, délai) et ce qu'il y relève (l'instance, les connexions demandées).</summary>
internal sealed class MinecraftProbe
{
    private readonly object _gate = new();
    private readonly List<(string Host, int Port)> _remoteCalls = [];

    /// <summary>L'instance du module créée pour ce test.</summary>
    public MinecraftModule? Module { get; set; }

    /// <summary>Le port du faux serveur où mener les connexions « ordinaires » (vers un autre serveur que ce PC).</summary>
    public int? RedirectTo { get; set; }

    /// <summary>Si elle est posée, la connexion « ordinaire » échoue avec cette exception.</summary>
    public Exception? RemoteFailure { get; set; }

    /// <summary>Si elle est posée, la réponse du relais vers ce PC (au lieu de celle du faux relais de <c>ModuleTestHost</c>).</summary>
    public DataResult<Stream>? LocalResult { get; set; }

    /// <summary>Le délai d'interrogation que le module doit prendre (le sien sinon).</summary>
    public TimeSpan? QueryTimeout { get; set; }

    /// <summary>Les connexions « ordinaires » que le module a demandées : l'adresse et le port de ses réglages.</summary>
    public IReadOnlyList<(string Host, int Port)> RemoteCalls
    {
        get { lock (_gate) { return [.. _remoteCalls]; } }
    }

    public void Record(string host, int port)
    {
        lock (_gate)
        {
            _remoteCalls.Add((host, port));
        }
    }
}

/// <summary>
/// Le module tel que les tests le font tourner : le même, mais la connexion « ordinaire » (vers un autre serveur que ce PC) va vers le faux serveur,
/// et les tests lisent ce qu'il a demandé. <c>ModuleTestHost</c> crée le module lui-même (constructeur sans paramètre) : ce que le test prête passe donc
/// par un <see cref="AsyncLocal{T}"/>, posé avant <c>StartAsync</c>. La valeur suit ce test seul : deux tests qui tournent en même temps ne se voient pas.
/// </summary>
internal sealed class TestableMinecraftModule : MinecraftModule
{
    public static readonly AsyncLocal<MinecraftProbe?> Current = new();

    private readonly MinecraftProbe _probe = Current.Value ?? throw new InvalidOperationException("Pose TestableMinecraftModule.Current avant StartAsync.");

    public TestableMinecraftModule() => _probe.Module = this;

    internal override Task<DataResult<Stream>> ConnectLocalAsync(int port, CancellationToken ct) =>
        _probe.LocalResult is { } result ? Task.FromResult(result) : base.ConnectLocalAsync(port, ct);

    internal override TimeSpan QueryTimeout => _probe.QueryTimeout ?? base.QueryTimeout;

    internal override async Task<Stream> ConnectRemoteAsync(string host, int port, CancellationToken ct)
    {
        _probe.Record(host, port);
        if (_probe.RemoteFailure is { } failure)
        {
            throw failure;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _probe.RedirectTo ?? throw new InvalidOperationException("Pose RedirectTo.")), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
