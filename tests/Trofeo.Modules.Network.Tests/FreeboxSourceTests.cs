using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Modules.Network.Tests;

public class FreeboxSourceTests
{
    private const string AppToken = "APP-TOKEN-0123456789abcdef";
    private const string Challenge = "CHALLENGE-xyz";
    private const string Session = "SESSION-TOKEN-987654";
    private const string ButtonMessage = "Appuie sur ► sur ta Freebox pour autoriser le dashboard";
    private const string DeniedMessage = "Autorisation refusée sur la Freebox";

    private const string Discovery = "GET /api_version";
    private const string Authorize = "POST /api/v8/login/authorize/";
    private const string Track = "GET /api/v8/login/authorize/42";
    private const string Login = "GET /api/v8/login/";
    private const string OpenSession = "POST /api/v8/login/session/";
    private const string Connection = "GET /api/v8/connection/";

    [Fact]
    public void AppId_KeepsLegacyId()
    {
        // La Freebox reconnaît l'application par cet identifiant : le changer redemanderait l'autorisation sur la façade.
        Assert.Equal("trofeo-HB-dashboard", FreeboxSource.AppId);
    }

    private sealed record Call(string Route, string Host, string Body, string? SessionHeader);

    /// <summary>Un « bruit » de transport : la requête ne rend jamais la main avant l'annulation de son jeton.</summary>
    private sealed record Hang;

    /// <summary>
    /// Boîte factice : chaque route (« GET /chemin ») a une file de réponses ; la dernière se répète. Les routes
    /// inconnues échouent comme un réseau coupé.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<object>> _routes = [];

        public List<Call> Calls { get; } = [];
        public TaskCompletionSource Started { get; } = new();

        public void On(string route, string json, HttpStatusCode status = HttpStatusCode.OK) => Add(route, (status, json));
        public void OnFail(string route, Exception error) => Add(route, error);
        public void OnHang(string route) => Add(route, new Hang());

        public int Count(string route) => Calls.Count(c => c.Route == route);

        public void Clear(string route) => _routes.Remove(route);

        private void Add(string route, object entry)
        {
            if (!_routes.TryGetValue(route, out var queue)) _routes[route] = queue = new Queue<object>();
            queue.Enqueue(entry);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("X-Fbx-App-Auth", out var header);
            Calls.Add(new Call(route, request.RequestUri.Host, body, header?.SingleOrDefault()));
            Started.TrySetResult();

            if (!_routes.TryGetValue(route, out var queue) || queue.Count == 0)
            {
                throw new HttpRequestException($"pas de réponse prévue pour {route}");
            }

            var entry = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            switch (entry)
            {
                case Exception error:
                    throw error;
                case Hang:
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("inatteignable");
                default:
                    var (status, json) = ((HttpStatusCode, string))entry;
                    return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }
    }

    private sealed class Rig
    {
        public ScriptedHandler Handler { get; } = new();
        public FakeSecretStore Secrets { get; } = new();
        public FakeModuleLog Log { get; } = new();
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        public FreeboxSource Source { get; }

        public Rig(Uri? baseUri = null)
        {
            Handler.On(Discovery, ApiVersion("8.2"));
            Source = new FreeboxSource(new HttpClient(Handler), Secrets, Log, baseUri, Time);
        }

        public Task<SourceReading> Read(CancellationToken ct = default) => Source.ReadAsync(ct);

        /// <summary>Scénario d'une session qui s'ouvre et d'une connexion à 10 Mb/s descendants.</summary>
        public void LoginAndConnection(string state = "up")
        {
            Handler.On(Login, Ok($$"""{"logged_in":false,"challenge":"{{Challenge}}"}"""));
            Handler.On(OpenSession, Ok($$"""{"session_token":"{{Session}}"}"""));
            Handler.On(Connection, Ok($$"""{"state":"{{state}}","rate_down":1250000,"rate_up":62500,"bandwidth_down":1000000000}"""));
        }

        /// <summary>Autorisation demandée : la box rend le jeton et le numéro de suivi.</summary>
        public void AuthorizeRequest() =>
            Handler.On(Authorize, Ok($$"""{"app_token":"{{AppToken}}","track_id":42}"""));
    }

    private static string ApiVersion(string version) =>
        $$"""{"uid":"abc","device_name":"Freebox Server","api_version":"{{version}}","api_base_url":"/api/","device_type":"FreeboxServer1,1"}""";

    private static string Ok(string result) => $$"""{"success":true,"result":{{result}}}""";

    private static string Err(string code) => $$"""{"success":false,"msg":"erreur","error_code":"{{code}}"}""";

    private static string TrackStatus(string status) => Ok($$"""{"status":"{{status}}","challenge":"{{Challenge}}"}""");

    // --- Authentification ---

    [Fact]
    public void Password_MatchesRfc2202Vector() =>
        Assert.Equal("effcdf6ae5eb2fa2d27416d5f184df9c259a7c79", FreeboxAuth.Password("Jefe", "what do ya want for nothing?"));

    [Fact]
    public void Title_And_DefaultInterval()
    {
        var rig = new Rig();

        Assert.Equal("Freebox", rig.Source.Title);
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Source.Interval);
    }

    // --- Autorisation ---

    [Fact]
    public async Task FirstRun_RequestsAuthorization_AndShowsButtonMessage()
    {
        var rig = new Rig();
        rig.AuthorizeRequest();

        var reading = await rig.Read();

        Assert.Null(reading.Sample);
        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage), reading.Status);
        Assert.Equal([Discovery, Authorize], rig.Handler.Calls.Select(c => c.Route));
        Assert.All(rig.Handler.Calls, c => Assert.Equal("mafreebox.freebox.fr", c.Host));
        var expected = "{\"app_id\":\"trofeo-HB-dashboard\",\"app_name\":\"Ashboard Vision\","
            + $"\"app_version\":\"{FreeboxSource.AppVersion}\",\"device_name\":\"{Environment.MachineName}\"}}";
        Assert.Equal(expected, rig.Handler.Calls[1].Body);
        Assert.False(string.IsNullOrWhiteSpace(FreeboxSource.AppVersion));
        Assert.Null(rig.Secrets.Get("freebox.app_token")); // pas encore accordé
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Source.Interval);
    }

    [Fact]
    public async Task Pending_ShowsButtonMessage_WithoutRequestingAgain()
    {
        var rig = new Rig();
        rig.AuthorizeRequest();
        rig.Handler.On(Track, TrackStatus("pending"));
        await rig.Read();

        var reading = await rig.Read();
        var again = await rig.Read();

        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage), reading.Status);
        Assert.Equal(reading, again);
        Assert.Equal(1, rig.Handler.Count(Authorize));
        Assert.Equal(1, rig.Handler.Count(Discovery));
        Assert.Equal(2, rig.Handler.Count(Track));
    }

    [Fact]
    public async Task Granted_StoresToken_ThenReadsRates()
    {
        var rig = new Rig();
        rig.AuthorizeRequest();
        rig.Handler.On(Track, TrackStatus("pending"));
        rig.Handler.On(Track, TrackStatus("granted"));
        rig.LoginAndConnection();
        await rig.Read();
        await rig.Read(); // en attente

        var reading = await rig.Read(); // accordé

        Assert.Equal(AppToken, rig.Secrets.Get("freebox.app_token"));
        Assert.Equal(new ModuleStatus(ModuleHealth.Online), reading.Status);
        Assert.Equal(new ThroughputSample(1_250_000, 62_500), reading.Sample);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Source.Interval);

        var session = rig.Handler.Calls.Single(c => c.Route == OpenSession);
        using var body = JsonDocument.Parse(session.Body);
        Assert.Equal("trofeo-HB-dashboard", body.RootElement.GetProperty("app_id").GetString());
        Assert.Equal(FreeboxAuth.Password(AppToken, Challenge), body.RootElement.GetProperty("password").GetString());
        Assert.Null(session.SessionHeader);
        Assert.Equal(Session, rig.Handler.Calls.Single(c => c.Route == Connection).SessionHeader);

        // La session ouverte sert aux lectures suivantes : un seul appel chacune.
        var callsBefore = rig.Handler.Calls.Count;
        var next = await rig.Read();
        Assert.Equal(new ThroughputSample(1_250_000, 62_500), next.Sample);
        Assert.Equal(callsBefore + 1, rig.Handler.Calls.Count);
        Assert.Equal(Connection, rig.Handler.Calls[^1].Route);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("unknown")]
    public async Task Timeout_RequestsAgain(string status)
    {
        var rig = new Rig();
        rig.AuthorizeRequest();
        rig.Handler.On(Track, TrackStatus(status));
        await rig.Read();

        var reading = await rig.Read();

        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage), reading.Status);
        Assert.Equal(2, rig.Handler.Count(Authorize));
    }

    [Fact]
    public async Task Denied_ShowsRefusal_AndWaits5Min()
    {
        var rig = new Rig();
        rig.AuthorizeRequest();
        rig.Handler.On(Track, TrackStatus("denied"));
        await rig.Read();

        var denied = await rig.Read();

        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, DeniedMessage), denied.Status);
        Assert.Null(denied.Sample);
        Assert.Null(rig.Secrets.Get("freebox.app_token"));

        // Pendant l'attente, la box n'est pas appelée.
        var calls = rig.Handler.Calls.Count;
        rig.Time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        var waiting = await rig.Read();
        Assert.Equal(denied, waiting);
        Assert.Equal(calls, rig.Handler.Calls.Count);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var retried = await rig.Read();
        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage), retried.Status);
        Assert.Equal(2, rig.Handler.Count(Authorize));
    }

    [Theory]
    [InlineData("invalid_token")]
    [InlineData("pending_token")]
    public async Task Login_InvalidToken_RestartsAuthorization(string code)
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", "OLD-TOKEN");
        rig.Handler.On(Login, Ok($$"""{"logged_in":false,"challenge":"{{Challenge}}"}"""));
        rig.Handler.On(OpenSession, Err(code), HttpStatusCode.Forbidden);
        rig.AuthorizeRequest();

        var reading = await rig.Read();

        Assert.Null(rig.Secrets.Get("freebox.app_token"));
        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage), reading.Status);
        Assert.Equal(1, rig.Handler.Count(Authorize));
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Source.Interval);
    }

    [Fact]
    public async Task StoredToken_SkipsAuthorization()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();

        var reading = await rig.Read();

        Assert.Equal(new ThroughputSample(1_250_000, 62_500), reading.Sample);
        Assert.Equal([Discovery, Login, OpenSession, Connection], rig.Handler.Calls.Select(c => c.Route));
        Assert.Equal(0, rig.Handler.Count(Authorize));
    }

    // --- Débit ---

    [Fact]
    public async Task AuthRequired_RelogsOnce()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        await rig.Read();

        rig.Handler.Clear(OpenSession);
        rig.Handler.On(OpenSession, Ok("""{"session_token":"SESSION-2"}"""));
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);
        rig.Handler.On(Connection, Ok("""{"state":"up","rate_down":300,"rate_up":40}"""));
        var callsBefore = rig.Handler.Calls.Count;

        var reading = await rig.Read();

        Assert.Equal(new ThroughputSample(300, 40), reading.Sample);
        Assert.Equal(ModuleHealth.Online, reading.Status.Health);
        Assert.Equal([Connection, Login, OpenSession, Connection], rig.Handler.Calls.Skip(callsBefore).Select(c => c.Route));
        Assert.Equal([Session, null, null, "SESSION-2"], rig.Handler.Calls.Skip(callsBefore).Select(c => c.SessionHeader));
    }

    [Fact]
    public async Task AuthRequired_StillFailing_IsOfflineWithoutThrowing()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);

        var reading = await rig.Read();

        Assert.Equal(ModuleHealth.Offline, reading.Status.Health);
        Assert.Null(reading.Sample);
        Assert.Equal(2, rig.Handler.Count(Connection)); // une seule nouvelle tentative
        Assert.Equal(2, rig.Handler.Count(OpenSession));
        Assert.Equal(AppToken, rig.Secrets.Get("freebox.app_token")); // le jeton d'application reste valable
    }

    [Fact]
    public async Task AuthRequired_Persistent_WarnsOncePerEpisode()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);

        for (var i = 0; i < 5; i++) await rig.Read();

        Assert.Single(rig.Log.Entries, e => e.StartsWith("Warning") && e.Contains("malgré une nouvelle session"));

        // Une lecture réussie ferme l'épisode : la panne suivante est de nouveau signalée.
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Ok("""{"state":"up","rate_down":1,"rate_up":1}"""));
        Assert.Equal(ModuleHealth.Online, (await rig.Read()).Status.Health);
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);
        await rig.Read();
        await rig.Read();

        Assert.Equal(2, rig.Log.Entries.Count(e => e.StartsWith("Warning") && e.Contains("malgré une nouvelle session")));
    }

    [Fact]
    public async Task ConnectionDown_IsOffline()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection(state: "down");

        var reading = await rig.Read();

        Assert.Equal(new ModuleStatus(ModuleHealth.Offline), reading.Status);
        Assert.Equal(new ThroughputSample(0, 0), reading.Sample);
    }

    [Fact]
    public async Task OldApi_IsOffline()
    {
        var rig = new Rig();
        rig.Handler.Clear(Discovery);
        rig.Handler.On(Discovery, ApiVersion("3.1"));

        var reading = await rig.Read();

        Assert.Equal(new ModuleStatus(ModuleHealth.Offline, "Freebox trop ancienne"), reading.Status);
        Assert.Null(reading.Sample);
        Assert.Equal([Discovery], rig.Handler.Calls.Select(c => c.Route));
    }

    [Fact]
    public async Task Api4_IsAccepted()
    {
        var rig = new Rig();
        rig.Handler.Clear(Discovery);
        rig.Handler.On(Discovery, ApiVersion("4.0"));
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.Handler.On("GET /api/v4/login/", Ok($$"""{"challenge":"{{Challenge}}"}"""));
        rig.Handler.On("POST /api/v4/login/session/", Ok($$"""{"session_token":"{{Session}}"}"""));
        rig.Handler.On("GET /api/v4/connection/", Ok("""{"state":"up","rate_down":1,"rate_up":2}"""));

        var reading = await rig.Read();

        Assert.Equal(new ThroughputSample(1, 2), reading.Sample);
    }

    // --- Pannes ---

    [Fact]
    public async Task NetworkError_Throws_AndNextReadResumesWithoutRediscovery()
    {
        var rig = new Rig();
        rig.Handler.OnFail(Authorize, new HttpRequestException("coupé"));
        rig.AuthorizeRequest(); // la deuxième tentative réussit

        await Assert.ThrowsAsync<FreeboxException>(() => rig.Read());
        var reading = await rig.Read();

        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage), reading.Status);
        Assert.Equal(1, rig.Handler.Count(Discovery)); // le préfixe découvert est conservé
    }

    [Fact]
    public async Task SessionAndToken_SurviveANetworkError()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        await rig.Read();

        rig.Handler.Clear(Connection);
        rig.Handler.OnFail(Connection, new HttpRequestException("coupé"));
        rig.Handler.On(Connection, Ok("""{"state":"up","rate_down":7,"rate_up":8}"""));
        await Assert.ThrowsAsync<FreeboxException>(() => rig.Read());
        var callsBefore = rig.Handler.Calls.Count;

        var reading = await rig.Read();

        Assert.Equal(new ThroughputSample(7, 8), reading.Sample);
        Assert.Equal([Connection], rig.Handler.Calls.Skip(callsBefore).Select(c => c.Route)); // ni découverte ni nouvelle session
    }

    [Fact]
    public async Task Request_HasItsOwn5SecondTimeout()
    {
        var rig = new Rig();
        rig.Handler.Clear(Discovery);
        rig.Handler.OnHang(Discovery);

        var read = rig.Read();
        await rig.Handler.Started.Task;
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(read.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<FreeboxException>(() => read);
        Assert.Contains("5 s", error.Message);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsCancellation()
    {
        var rig = new Rig();
        rig.Handler.Clear(Discovery);
        rig.Handler.OnHang(Discovery);
        using var cts = new CancellationTokenSource();

        var read = rig.Read(cts.Token);
        await rig.Handler.Started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Theory]
    [InlineData("<html>502 Bad Gateway</html>", HttpStatusCode.BadGateway)]
    [InlineData("", HttpStatusCode.OK)]
    [InlineData("[1,2]", HttpStatusCode.OK)]
    public async Task UnparseableBody_Throws(string body, HttpStatusCode status)
    {
        var rig = new Rig();
        rig.Handler.Clear(Discovery);
        rig.Handler.On(Discovery, body, status);

        await Assert.ThrowsAsync<FreeboxException>(() => rig.Read());
    }

    [Fact]
    public async Task ApiBaseUrlPointingElsewhere_Throws()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.Handler.Clear(Discovery);
        rig.Handler.On(Discovery, """{"api_version":"8.2","api_base_url":"//autre.example/api/"}""");

        await Assert.ThrowsAsync<FreeboxException>(() => rig.Read());

        Assert.Equal([Discovery], rig.Handler.Calls.Select(c => c.Route));
    }

    [Fact]
    public async Task UnexpectedErrorCode_Throws()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("internal_error"), HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<FreeboxException>(() => rig.Read());

        Assert.Contains("internal_error", error.Message);
    }

    [Fact]
    public async Task CustomBaseUri_IsUsed()
    {
        var rig = new Rig(new Uri("http://192.168.0.254:8080"));
        rig.AuthorizeRequest();

        await rig.Read();

        Assert.All(rig.Handler.Calls, c => Assert.Equal("192.168.0.254", c.Host));
    }

    // --- Journal ---

    [Fact]
    public async Task Log_NeverContainsToken()
    {
        var rig = new Rig();
        rig.AuthorizeRequest();
        rig.Handler.On(Track, TrackStatus("granted"));
        rig.LoginAndConnection();
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);

        await rig.Read(); // demande d'autorisation
        await rig.Read(); // accordée, puis connexion refusée deux fois : Offline
        rig.Handler.Clear(OpenSession);
        rig.Handler.On(OpenSession, Err("invalid_token"), HttpStatusCode.Forbidden);
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);
        await rig.Read(); // session perdue, jeton rejeté : nouvelle demande

        Assert.NotEmpty(rig.Log.Entries);
        Assert.DoesNotContain(rig.Log.Entries, e => e.Contains(AppToken) || e.Contains(Session) || e.Contains(Challenge));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("unparseable")]
    [InlineData("refused")]
    public async Task Module_ReadThrowingAfterTheTokenExists_LeavesNoTokenInTheLog(string failure)
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        rig.Handler.Clear(Connection);
        switch (failure)
        {
            case "transport": rig.Handler.OnFail(Connection, new HttpRequestException("coupé")); break;
            case "unparseable": rig.Handler.On(Connection, "<html>502</html>", HttpStatusCode.BadGateway); break;
            default: rig.Handler.On(Connection, Err("internal_error"), HttpStatusCode.InternalServerError); break;
        }
        var descriptor = new NetworkFactory((_, _) => rig.Source).Descriptor;
        using var module = new NetworkModule(descriptor, rig.Source, rig.Time, rig.Log);

        await module.StartAsync(CancellationToken.None);

        Assert.Equal(ModuleHealth.Offline, module.Status.Health);
        Assert.Contains(rig.Log.Entries, e => e.StartsWith("Error"));
        Assert.DoesNotContain(rig.Log.Entries, e => e.Contains(AppToken) || e.Contains(Session) || e.Contains(Challenge));
    }

    // --- Durcissement de la tâche 12 ---

    /// <summary>Coffre qui compte les lectures de chaque clé.</summary>
    private sealed class CountingSecretStore : ISecretStore
    {
        private readonly FakeSecretStore _inner = new();
        public Dictionary<string, int> Gets { get; } = [];

        public string? Get(string key)
        {
            Gets[key] = Gets.GetValueOrDefault(key) + 1;
            return _inner.Get(key);
        }

        public void Set(string key, string? value) => _inner.Set(key, value);
    }

    [Fact]
    public async Task Token_IsReadOnceAndKeptInSyncWithEverySet()
    {
        var secrets = new CountingSecretStore();
        var handler = new ScriptedHandler();
        handler.On(Discovery, ApiVersion("8.2"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var source = new FreeboxSource(new HttpClient(handler), secrets, new FakeModuleLog(), null, time);
        handler.On(Authorize, Ok($$"""{"app_token":"{{AppToken}}","track_id":42}"""));
        handler.On(Track, TrackStatus("granted"));
        handler.On(Login, Ok($$"""{"logged_in":false,"challenge":"{{Challenge}}"}"""));
        handler.On(OpenSession, Ok($$"""{"session_token":"{{Session}}"}"""));
        handler.On(Connection, Ok("""{"state":"up","rate_down":1,"rate_up":2}"""));

        await source.ReadAsync(default); // pas de jeton : demande
        await source.ReadAsync(default); // accordé : enregistré puis lecture
        await source.ReadAsync(default);
        await source.ReadAsync(default);

        Assert.Equal(1, secrets.Gets["freebox.app_token"]); // une seule lecture du coffre, malgré quatre lectures du débit

        // Le jeton rejeté est effacé du coffre et du cache : la lecture suivante redemande l'autorisation, sans relire le coffre.
        handler.Clear(Connection);
        handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);
        handler.Clear(OpenSession);
        handler.On(OpenSession, Err("invalid_token"), HttpStatusCode.Forbidden);
        await source.ReadAsync(default);
        Assert.Null(secrets.Get("freebox.app_token"));
        secrets.Gets.Clear();
        var next = await source.ReadAsync(default);
        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage), next.Status);
        Assert.Empty(secrets.Gets);
    }

    [Fact]
    public void FreeboxResponse_ToString_NeverPrintsTheResult()
    {
        using var doc = JsonDocument.Parse($$"""{"app_token":"{{AppToken}}","session_token":"{{Session}}"}""");
        var response = new FreeboxResponse(true, "some_code", doc.RootElement.Clone());

        var text = response.ToString();

        Assert.DoesNotContain(AppToken, text);
        Assert.DoesNotContain(Session, text);
        Assert.Contains("some_code", text);
    }

    [Fact]
    public void FreeboxException_IsNotPartOfThePublicApi() =>
        Assert.False(typeof(FreeboxException).IsVisible);

    [Fact]
    public async Task Interval_IsOneSecondOnlyOnceRatesAreValidated_AndResetsOnFailure()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Ok("""{"state":"up","rate_down":"beaucoup","rate_up":2}"""));
        rig.Handler.On(Connection, Ok("""{"state":"up","rate_down":3,"rate_up":4}"""));
        rig.Handler.On(Connection, Ok("""{"state":"up"}"""));
        rig.Handler.On(Connection, Ok("""{"state":"up","rate_down":5,"rate_up":6}"""));

        await Assert.ThrowsAsync<FreeboxException>(() => rig.Read()); // débit illisible : pas connecté
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Source.Interval);

        await rig.Read();
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Source.Interval);

        await Assert.ThrowsAsync<FreeboxException>(() => rig.Read()); // la lecture lève : retour à 2 s
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Source.Interval);

        await rig.Read();
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Source.Interval);
    }

    [Fact]
    public async Task Interval_IsTwoSecondsWhenTheReadEndsOffline()
    {
        var rig = new Rig();
        rig.Secrets.Set("freebox.app_token", AppToken);
        rig.LoginAndConnection();
        await rig.Read();
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Source.Interval);

        rig.Handler.Clear(Connection);
        rig.Handler.On(Connection, Err("auth_required"), HttpStatusCode.Forbidden);
        var reading = await rig.Read();

        Assert.Equal(ModuleHealth.Offline, reading.Status.Health);
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Source.Interval);
    }
}
