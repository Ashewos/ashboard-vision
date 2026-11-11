using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.ModuleKit;

namespace Trofeo.Control.Tests;

// Les modules complémentaires dans le canal de contrôle (protocole 3).
public partial class ControlChannelTests
{
    private static readonly ModulePermission[] Granted = [ModulePermission.LocalNetwork, ModulePermission.ThisComputer];

    private static CommunityModuleInfo SampleModule() => new(
        "alex.demo", "Démo", "Alex", "Un module de démonstration", "1.2.0", LinkCommunityState.NeedsApproval, "À valider dans la fenêtre de réglage",
        Declared: [ModulePermission.Internet, ModulePermission.Sensors], Granted: [ModulePermission.Internet],
        FromDevFolder: true, UpdateVersion: "1.3.0", UpdateNotes: "Nouveau réglage.", DevFolderPath: @"C:\dev\demo");

    private sealed partial class FakeHandler
    {
        private readonly object _callsGate = new();
        private readonly List<string> _calls = [];

        /// <summary>Ce que le service a reçu, dans l'ordre : « nom argument… ».</summary>
        public string[] Calls
        {
            get
            {
                lock (_callsGate)
                    return [.. _calls];
            }
        }

        private void Record(string call)
        {
            lock (_callsGate)
                _calls.Add(call);
        }

        public IReadOnlyList<ModuleTypeInfo> TypesToReturn { get; set; } =
        [
            new("alex.demo", "Démo", new GridSize(2, 1), new Dictionary<SizePreset, GridSize>(), AcceptsCommands: false, SettingsSchema.Empty,
                new CommunityTypeInfo("Alex", "Un module de démonstration", "1.2.0", [ModulePermission.Internet, ModulePermission.ThisComputer], [ModulePermission.Internet])),
        ];

        public CommunityOverview CommunityToReturn { get; set; } = new(
            [SampleModule()], @"C:\Modules", DeveloperMode: true, DevFolders: [@"C:\dev\demo"], StoreTampered: true);

        public PackageInfo PackageToReturn { get; set; } = new(
            "alex.demo", "Démo", "Alex", "1.3.0", "Un module de démonstration", [ModulePermission.Internet], ["Une erreur."],
            "Un autre module porte déjà cet identifiant", "1.2.0");

        public ActionResult ActionToReturn { get; set; } = new(true, "Fait.");

        public IReadOnlyList<ModuleConsumption> ConsumptionToReturn { get; set; } =
            [new("alex.demo", "Démo", IsCommunity: true, CpuPercent: 1.5, MemoryBytes: 123_456_789, High: true)];

        private static string Join(IReadOnlyList<ModulePermission> granted) => string.Join(",", granted);

        public IReadOnlyList<ModuleTypeInfo> GetTypes()
        {
            Record("types");
            return TypesToReturn;
        }

        public CommunityOverview GetCommunity()
        {
            Record("community");
            return CommunityToReturn;
        }

        public PackageInfo InspectPackage(string path)
        {
            Record($"inspectPackage {path}");
            return PackageToReturn;
        }

        public Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted, CancellationToken ct)
        {
            Record($"installPackage {path} {Join(granted)}");
            return Task.FromResult(ActionToReturn);
        }

        public ActionResult ApproveModule(string id, IReadOnlyList<ModulePermission> granted)
        {
            Record($"approveModule {id} {Join(granted)}");
            return ActionToReturn;
        }

        public Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct)
        {
            Record($"setModulePermissions {id} {Join(granted)}");
            return Task.FromResult(ActionToReturn);
        }

        public Task<ActionResult> UninstallModuleAsync(string id, CancellationToken ct)
        {
            Record($"uninstallModule {id}");
            return Task.FromResult(ActionToReturn);
        }

        public Task<ActionResult> RelaunchModuleAsync(string id, CancellationToken ct)
        {
            Record($"relaunchModule {id}");
            return Task.FromResult(ActionToReturn);
        }

        public ActionResult SetModulesRoot(string path)
        {
            Record($"setModulesRoot {path}");
            return ActionToReturn;
        }

        public ActionResult SetDeveloperMode(bool on)
        {
            Record($"setDeveloperMode {on}");
            return ActionToReturn;
        }

        public ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted)
        {
            Record($"addDevFolder {path} {Join(granted)}");
            return ActionToReturn;
        }

        public PackageInfo InspectDevFolder(string path)
        {
            Record($"inspectDevFolder {path}");
            return PackageToReturn;
        }

        public ActionResult RemoveDevFolder(string path)
        {
            Record($"removeDevFolder {path}");
            return ActionToReturn;
        }

        public Task<ActionResult> ReloadDevModuleAsync(string id, CancellationToken ct)
        {
            Record($"reloadDevModule {id}");
            return Task.FromResult(ActionToReturn);
        }

        public Task CheckModuleUpdatesAsync(CancellationToken ct)
        {
            Record("checkModuleUpdates");
            return Task.CompletedTask;
        }

        public Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct)
        {
            Record($"installModuleUpdate {id} {Join(granted)}");
            return Task.FromResult(ActionToReturn);
        }

        public ModuleUpdatePreparation? PreparationToReturn { get; set; } = new(
            true, null, "1.3.0", "Nouveau réglage.", [ModulePermission.Internet, ModulePermission.NowPlaying], [ModulePermission.NowPlaying]);

        public Task<ModuleUpdatePreparation> PrepareModuleUpdateAsync(string id, CancellationToken ct)
        {
            Record($"prepareModuleUpdate {id}");
            return Task.FromResult(PreparationToReturn!);
        }

        public void CancelModuleUpdate(string id) => Record($"cancelModuleUpdate {id}");

        public IReadOnlyList<ModuleConsumption> GetConsumption()
        {
            Record("consumption");
            return ConsumptionToReturn;
        }
    }

    private static async Task<(ControlServer Server, ControlClient Client)> StartAsync(FakeHandler handler)
    {
        var name = NewPipeName();
        var server = new ControlServer(name, handler);
        server.Start();
        return (server, await ConnectAsync(name));
    }

    [Fact]
    public async Task Hello_ServiceOfProtocol3_IsMismatch()
    {
        var name = NewPipeName();
        var handler = new FakeHandler { WelcomeToReturn = SampleWelcome() with { ProtocolVersion = 3 } };
        using var server = new ControlServer(name, handler);
        server.Start();

        var ex = await Assert.ThrowsAsync<ProtocolMismatchException>(() => ConnectAsync(name));
        Assert.Contains("version 4", ex.Message);
        Assert.Contains("version 3", ex.Message);
    }

    [Fact]
    public async Task Hello_CommunityInfoOfAType_RoundTrips()
    {
        var handler = new FakeHandler { WelcomeToReturn = SampleWelcome() with { Types = [.. SampleWelcome().Types, .. new FakeHandler().TypesToReturn] } };
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        Assert.Null(client.Welcome.Types[0].Community); // un module d'origine n'a pas de fiche
        var community = client.Welcome.Types[1].Community;
        Assert.NotNull(community);
        Assert.Equal("Alex", community.Author);
    }

    [Fact]
    public async Task Types_RoundTripWithCommunityInfo()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var types = await client.GetTypesAsync(CancellationToken.None);

        var type = Assert.Single(types);
        Assert.Equal("alex.demo", type.TypeId);
        Assert.Equal(new GridSize(2, 1), type.MinSize);
        var community = Assert.IsType<CommunityTypeInfo>(type.Community);
        Assert.Equal(("Alex", "Un module de démonstration", "1.2.0"), (community.Author, community.Description, community.Version));
        Assert.Equal([ModulePermission.Internet, ModulePermission.ThisComputer], community.Declared);
        Assert.Equal([ModulePermission.Internet], community.Granted);
        Assert.Equal(["types"], handler.Calls);
    }

    [Fact]
    public async Task Community_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var overview = await client.GetCommunityAsync(CancellationToken.None);

        Assert.Equal(@"C:\Modules", overview.ModulesRoot);
        Assert.True(overview.DeveloperMode);
        Assert.True(overview.StoreTampered);
        Assert.Equal([@"C:\dev\demo"], overview.DevFolders);
        var module = Assert.Single(overview.Modules);
        var expected = SampleModule();
        Assert.Equal(
            (expected.Id, expected.Name, expected.Author, expected.Description, expected.Version, expected.State, expected.Message, expected.FromDevFolder,
                expected.UpdateVersion, expected.UpdateNotes, expected.DevFolderPath),
            (module.Id, module.Name, module.Author, module.Description, module.Version, module.State, module.Message, module.FromDevFolder,
                module.UpdateVersion, module.UpdateNotes, module.DevFolderPath));
        Assert.Equal(expected.Declared, module.Declared);
        Assert.Equal(expected.Granted, module.Granted);
    }

    [Fact]
    public async Task Community_NullFieldsAndEveryState_RoundTrip()
    {
        var modules = Enum.GetValues<LinkCommunityState>()
            .Select(state => new CommunityModuleInfo(
                "alex." + state.ToString().ToLowerInvariant(), "N", "A", "D", Version: null, state, Message: null, [], [], FromDevFolder: false,
                UpdateVersion: null, UpdateNotes: null))
            .ToList();
        var handler = new FakeHandler { CommunityToReturn = new CommunityOverview(modules, @"C:\M", false, [], false) };
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var overview = await client.GetCommunityAsync(CancellationToken.None);

        Assert.Equal(modules.Select(m => m.State), overview.Modules.Select(m => m.State));
        Assert.All(overview.Modules, m => Assert.Null(m.Version));
        Assert.All(overview.Modules, m => Assert.Null(m.Message));
        Assert.All(overview.Modules, m => Assert.Null(m.DevFolderPath));
    }

    [Fact]
    public async Task InspectDevFolder_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var package = await client.InspectDevFolderAsync(@"C:\dev\demo", CancellationToken.None);

        Assert.Equal(("alex.demo", "Démo", "Alex", "1.3.0"), (package.Id, package.Name, package.Author, package.Version));
        Assert.Equal([ModulePermission.Internet], package.Declared);
        Assert.Equal(["Une erreur."], package.Errors);
        Assert.Equal([@"inspectDevFolder C:\dev\demo"], handler.Calls);
    }

    [Fact]
    public async Task InspectPackage_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var package = await client.InspectPackageAsync(@"C:\Téléchargements\alex.demo-1.3.0.avmod", CancellationToken.None);

        Assert.Equal(("alex.demo", "Démo", "Alex", "1.3.0", "Un module de démonstration"),
            (package.Id, package.Name, package.Author, package.Version, package.Description));
        Assert.Equal([ModulePermission.Internet], package.Declared);
        Assert.Equal(["Une erreur."], package.Errors);
        Assert.Equal("Un autre module porte déjà cet identifiant", package.Conflict);
        Assert.Equal("1.2.0", package.InstalledVersion);
        Assert.Equal([@"inspectPackage C:\Téléchargements\alex.demo-1.3.0.avmod"], handler.Calls);
    }

    [Fact]
    public async Task InspectPackage_RefusedPackage_HasNullIdentity()
    {
        var handler = new FakeHandler { PackageToReturn = new PackageInfo(null, null, null, null, null, [], ["Archive illisible."], null, null) };
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var package = await client.InspectPackageAsync("x.avmod", CancellationToken.None);

        Assert.Null(package.Id);
        Assert.Null(package.Conflict);
        Assert.Null(package.InstalledVersion);
        Assert.Equal(["Archive illisible."], package.Errors);
    }

    [Fact]
    public async Task InstallPackage_RoundTrip()
    {
        var handler = new FakeHandler { ActionToReturn = new ActionResult(false, "Refusé.") };
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var result = await client.InstallPackageAsync("a.avmod", Granted, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Refusé.", result.Message);
        Assert.Equal(["installPackage a.avmod LocalNetwork,ThisComputer"], handler.Calls);
    }

    [Fact]
    public async Task ApproveModule_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var result = await client.ApproveModuleAsync("alex.demo", [], CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Fait.", result.Message);
        Assert.Equal(["approveModule alex.demo "], handler.Calls); // aucune permission accordée : liste vide, pas absente
    }

    [Fact]
    public async Task SetModulePermissions_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        Assert.True((await client.SetModulePermissionsAsync("alex.demo", Granted, CancellationToken.None)).Ok);

        Assert.Equal(["setModulePermissions alex.demo LocalNetwork,ThisComputer"], handler.Calls);
    }

    [Fact]
    public async Task UninstallAndRelaunchAndReloadDev_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        Assert.True((await client.UninstallModuleAsync("alex.demo", CancellationToken.None)).Ok);
        Assert.True((await client.RelaunchModuleAsync("alex.demo", CancellationToken.None)).Ok);
        Assert.True((await client.ReloadDevModuleAsync("alex.demo", CancellationToken.None)).Ok);

        Assert.Equal(["uninstallModule alex.demo", "relaunchModule alex.demo", "reloadDevModule alex.demo"], handler.Calls);
    }

    [Fact]
    public async Task SetModulesRootAndDeveloperMode_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        Assert.True((await client.SetModulesRootAsync(@"D:\Modules", CancellationToken.None)).Ok);
        Assert.True((await client.SetDeveloperModeAsync(true, CancellationToken.None)).Ok);
        Assert.True((await client.SetDeveloperModeAsync(false, CancellationToken.None)).Ok);

        Assert.Equal([@"setModulesRoot D:\Modules", "setDeveloperMode True", "setDeveloperMode False"], handler.Calls);
    }

    [Fact]
    public async Task AddAndRemoveDevFolder_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        Assert.True((await client.AddDevFolderAsync(@"C:\dev\demo", [ModulePermission.Internet], CancellationToken.None)).Ok);
        Assert.True((await client.RemoveDevFolderAsync(@"C:\dev\demo", CancellationToken.None)).Ok);

        Assert.Equal([@"addDevFolder C:\dev\demo Internet", @"removeDevFolder C:\dev\demo"], handler.Calls);
    }

    [Fact]
    public async Task CheckModuleUpdatesAndInstallModuleUpdate_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        await client.CheckModuleUpdatesAsync(CancellationToken.None);
        var result = await client.InstallModuleUpdateAsync("alex.demo", [ModulePermission.NowPlaying], CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(["checkModuleUpdates", "installModuleUpdate alex.demo NowPlaying"], handler.Calls);
    }

    [Fact]
    public async Task PrepareModuleUpdate_RoundTrip_CarriesTheVersionTheNotesAndBothPermissionLists()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var preparation = await client.PrepareModuleUpdateAsync("alex.demo", CancellationToken.None);

        Assert.True(preparation.Ok);
        Assert.Equal(("1.3.0", "Nouveau réglage."), (preparation.Version, preparation.Notes));
        Assert.Equal([ModulePermission.Internet, ModulePermission.NowPlaying], preparation.Declared);
        Assert.Equal([ModulePermission.NowPlaying], preparation.NewPermissions);
        Assert.Equal(["prepareModuleUpdate alex.demo"], handler.Calls);
    }

    [Fact]
    public async Task PrepareModuleUpdate_Refused_RoundTrips_WithItsMessageAndNoVersion()
    {
        var handler = new FakeHandler { PreparationToReturn = new ModuleUpdatePreparation(false, "Aucune mise à jour proposée pour ce module.", null, null, [], []) };
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var preparation = await client.PrepareModuleUpdateAsync("alex.demo", CancellationToken.None);

        Assert.False(preparation.Ok);
        Assert.Equal("Aucune mise à jour proposée pour ce module.", preparation.Message);
        Assert.Null(preparation.Version);
        Assert.Empty(preparation.Declared);
        Assert.Empty(preparation.NewPermissions);
    }

    [Fact]
    public async Task PrepareModuleUpdate_EmptyAnswer_IsReportedAsIncomplete_AndTheConnectionStays()
    {
        var handler = new FakeHandler { PreparationToReturn = null };
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        await Assert.ThrowsAsync<ControlException>(() => client.PrepareModuleUpdateAsync("alex.demo", CancellationToken.None));
        Assert.True((await client.GetStatusAsync(CancellationToken.None)).Emulated);
    }

    [Fact]
    public async Task CancelModuleUpdate_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        await client.CancelModuleUpdateAsync("alex.demo", CancellationToken.None);

        Assert.Equal(["cancelModuleUpdate alex.demo"], handler.Calls);
    }

    [Fact]
    public async Task Consumption_RoundTrip()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;

        var consumption = await client.GetConsumptionAsync(CancellationToken.None);

        var line = Assert.Single(consumption);
        Assert.Equal(new ModuleConsumption("alex.demo", "Démo", true, 1.5, 123_456_789, true), line);
    }

    [Fact]
    public async Task EmptyAnswer_IsReportedAsIncomplete()
    {
        var handler = new FakeHandler();
        var (server, client) = await StartAsync(handler);
        using var _ = server;
        await using var __ = client;
        handler.ConsumptionToReturn = null!; // réponse vide : le client la déclare incomplète, la connexion reste utilisable

        await Assert.ThrowsAsync<ControlException>(() => client.GetConsumptionAsync(CancellationToken.None));
        Assert.True((await client.GetStatusAsync(CancellationToken.None)).Emulated);
    }

    [Fact]
    public void TimeoutFor_ModuleRequests()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), ControlClient.TimeoutFor(MessageTypes.CheckModuleUpdates));
        Assert.Equal(TimeSpan.FromMinutes(11), ControlClient.TimeoutFor(MessageTypes.PrepareModuleUpdate)); // au-dessus des 10 min du service
        foreach (var slow in new[]
                 {
                     MessageTypes.InstallPackage, MessageTypes.SetModulePermissions, MessageTypes.UninstallModule, MessageTypes.RelaunchModule,
                     MessageTypes.ReloadDevModule, MessageTypes.InstallModuleUpdate,
                 })
        {
            Assert.Equal(TimeSpan.FromMinutes(2), ControlClient.TimeoutFor(slow));
        }

        foreach (var quick in new[]
                 {
                     MessageTypes.Types, MessageTypes.Community, MessageTypes.InspectPackage, MessageTypes.ApproveModule, MessageTypes.SetModulesRoot,
                     MessageTypes.SetDeveloperMode, MessageTypes.AddDevFolder, MessageTypes.InspectDevFolder, MessageTypes.RemoveDevFolder, MessageTypes.Consumption, MessageTypes.CancelModuleUpdate,
                 })
        {
            Assert.Equal(TimeSpan.FromSeconds(10), ControlClient.TimeoutFor(quick));
        }
    }

    // ---- Sur le fil ----

    [Fact]
    public void Wire_PermissionsAndStatesAreText_LikeOtherEnums()
    {
        var request = JsonSerializer.Serialize(
            new Request(1, MessageTypes.InstallPackage) { Path = "a.avmod", Granted = [ModulePermission.LocalNetwork, ModulePermission.NowPlaying] },
            ControlJson.Options);
        var response = JsonSerializer.Serialize(
            new Response(1, MessageTypes.Community) { Community = new CommunityOverview([SampleModule()], @"C:\M", false, [], false) },
            ControlJson.Options);

        Assert.Contains("\"granted\":[\"LocalNetwork\",\"NowPlaying\"]", request);
        Assert.Contains("\"state\":\"NeedsApproval\"", response);
        Assert.Contains("\"declared\":[\"Internet\",\"Sensors\"]", response);
    }

    private static async Task<NamedPipeClientStream> RawConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    private static Task SendRawAsync(Stream pipe, string json) => Framing.WriteJsonAsync(pipe, Encoding.UTF8.GetBytes(json), CancellationToken.None);

    [Fact]
    public async Task Wire_UnknownPermissionName_ClosesTheConnection_AsAnyMalformedMessage()
    {
        var handler = new FakeHandler();
        var name = NewPipeName();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var pipe = await RawConnectAsync(name);

        await SendRawAsync(pipe, """{"id":1,"type":"installPackage","path":"a.avmod","granted":["Telepathie"]}""");

        Assert.Null(await Framing.ReadAsync(pipe, CancellationToken.None).WaitAsync(Wait)); // fin du flux : la session s'est refermée
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Wire_UndefinedPermissionNumber_IsRefusedWithAnErrorAndTheConnectionStays()
    {
        var handler = new FakeHandler();
        var name = NewPipeName();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var pipe = await RawConnectAsync(name);

        await SendRawAsync(pipe, """{"id":1,"type":"approveModule","moduleId":"alex.demo","granted":[99]}""");
        var frame = await Framing.ReadAsync(pipe, CancellationToken.None).WaitAsync(Wait);

        Assert.NotNull(frame);
        var reply = JsonSerializer.Deserialize<Response>(frame.Value.Payload, ControlJson.Options)!;
        Assert.Equal(MessageTypes.Error, reply.Type);
        Assert.Equal("Permission inconnue : 99.", reply.Message);
        Assert.Empty(handler.Calls);

        await SendRawAsync(pipe, """{"id":2,"type":"consumption"}""");
        var next = await Framing.ReadAsync(pipe, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(MessageTypes.Consumption, JsonSerializer.Deserialize<Response>(next!.Value.Payload, ControlJson.Options)!.Type);
    }

    [Theory]
    [InlineData("""{"id":1,"type":"installPackage","granted":[]}""", "Paramètre manquant : path.")]
    [InlineData("""{"id":1,"type":"installPackage","path":"a.avmod"}""", "Paramètre manquant : granted.")]
    [InlineData("""{"id":1,"type":"approveModule","granted":[]}""", "Paramètre manquant : moduleId.")]
    [InlineData("""{"id":1,"type":"setDeveloperMode"}""", "Paramètre manquant : on.")]
    [InlineData("""{"id":1,"type":"addDevFolder","granted":[]}""", "Paramètre manquant : path.")]
    [InlineData("""{"id":1,"type":"prepareModuleUpdate"}""", "Paramètre manquant : moduleId.")]
    [InlineData("""{"id":1,"type":"cancelModuleUpdate"}""", "Paramètre manquant : moduleId.")]
    public async Task Wire_MissingParameter_IsAnErrorResponse(string request, string message)
    {
        var handler = new FakeHandler();
        var name = NewPipeName();
        using var server = new ControlServer(name, handler);
        server.Start();
        await using var pipe = await RawConnectAsync(name);

        await SendRawAsync(pipe, request);
        var frame = await Framing.ReadAsync(pipe, CancellationToken.None).WaitAsync(Wait);

        var reply = JsonSerializer.Deserialize<Response>(frame!.Value.Payload, ControlJson.Options)!;
        Assert.Equal(MessageTypes.Error, reply.Type);
        Assert.Equal(message, reply.Message);
        Assert.Empty(handler.Calls);
    }
}
