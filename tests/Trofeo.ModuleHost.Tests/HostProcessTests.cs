using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost.Tests;

/// <summary>
/// L'hôte de module pour de vrai : le vrai exécutable, lancé sans enfermement par des tubes anonymes ordinaires, avec un
/// module de test (<c>sample</c>) qui raconte ce qu'il fait par le journal. Une seule collection : les tests tournent
/// l'un après l'autre.
/// </summary>
[Trait("Windows", "ModuleHost")]
[Collection("HostRig")]
public sealed class HostProcessTests
{
    private static async Task<HostRig> StartedAsync(ModulePermission[]? granted = null, int maxFps = 30, bool autoFrameDone = true)
    {
        var rig = HostRig.Start();
        rig.AutoFrameDone = autoFrameDone;
        await rig.SendSampleHelloAsync(granted, maxFps);
        await rig.WaitAsync<Ready>();
        return rig;
    }

    [WindowsHostFact]
    public void SampleModule_KeepsItsOwnCopiesOfTheSharedAssemblies()
    {
        // Le test suivant n'a de sens que si le dossier du module contient des copies que l'hôte doit ignorer.
        Assert.True(File.Exists(Path.Combine(HostRig.SampleFolder, "AshboardVision.Sdk.dll")));
        Assert.True(File.Exists(Path.Combine(HostRig.SampleFolder, "Trofeo.ModuleKit.dll")));
        Assert.True(File.Exists(Path.Combine(HostRig.SampleFolder, "SkiaSharp.dll")));
    }

    [WindowsHostFact]
    public async Task Host_LoadsModuleAndSendsFirstFrame()
    {
        await using var rig = await StartedAsync();
        Assert.Equal("1.0", (await rig.WaitAsync<Ready>()).RequiredSdk);

        var instance = await rig.OpenAsync("""{"color":"#FF8000"}""");
        var frame = await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id);

        Assert.Equal(0, frame.Buffer);
        Assert.False(frame.Animating);
        using var bitmap = instance.Read(0);
        Assert.Equal(new SKColor(255, 128, 0), bitmap.GetPixel(5, 5));
        Assert.Equal(new SKColor(255, 128, 0), bitmap.GetPixel(40, 40));
        var accent = AppearanceResolver.Resolve(AppearanceSettings.Default, new SkiaSystemFonts()).Theme.Accent;
        Assert.Equal(accent.WithAlpha(255), bitmap.GetPixel(instance.Width - 5, 5).WithAlpha(255));
    }

    [WindowsHostFact]
    public async Task Host_PassesTheCellsSizeAndTextScaleToTheModule()
    {
        await using var rig = await StartedAsync();

        var instance = await rig.OpenAsync("""{"mode":"ctx"}""", width: 120, height: 60, columns: 3, rows: 2);

        var log = await rig.WaitForLogAsync("ctx:");
        Assert.Contains("ctx:3x2:120x60:1.25:classique", log.Message);
        Assert.Equal(instance.Id, (await rig.WaitAsync<Frame>()).InstanceId);
    }

    [WindowsHostFact]
    public async Task Host_NeverWritesBufferAwaitingFrameDone()
    {
        await using var rig = await StartedAsync(maxFps: 60, autoFrameDone: false);

        var instance = await rig.OpenAsync("""{"mode":"burst"}""");
        await Task.Delay(1500); // dix demandes de dessin espacées de 50 ms, jamais acquittées

        var frames = rig.Of<Frame>().Where(f => f.InstanceId == instance.Id).ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal([0, 1], frames.Select(f => f.Buffer).Order());

        // Dès qu'un tampon est rendu, l'image attendue (les demandes restées sans réponse) y est dessinée.
        await rig.SendAsync(new FrameDone(instance.Id, 0));
        await rig.WaitUntilAsync(() => rig.Of<Frame>().Count() == 3);
        Assert.Equal(0, rig.Of<Frame>().Last().Buffer);
        await Task.Delay(300);
        Assert.Equal(3, rig.Of<Frame>().Count());
    }

    [WindowsHostFact]
    public async Task Host_RespectsMaxFps()
    {
        await using var rig = await StartedAsync(maxFps: 4);

        var instance = await rig.OpenAsync("""{"mode":"animated"}""");
        await rig.WaitAsync<Frame>();
        await Task.Delay(2000);

        var count = rig.Of<Frame>().Count(f => f.InstanceId == instance.Id);
        Assert.InRange(count, 7, 9);
        Assert.True(rig.Of<Frame>().All(f => f.Animating));
    }

    [WindowsHostFact]
    public async Task Host_LimitsChange_AppliesToRunningInstances()
    {
        await using var rig = await StartedAsync(maxFps: 2);
        await rig.OpenAsync("""{"mode":"animated"}""");
        await rig.WaitAsync<Frame>();

        await rig.SendAsync(new Limits(20));
        var before = rig.Of<Frame>().Count();
        await Task.Delay(1000);

        Assert.True(rig.Of<Frame>().Count() - before >= 10, "Le nouveau plafond devait s'appliquer sans rouvrir l'instance.");
    }

    [WindowsHostFact]
    public async Task Host_SensorsWithoutPermission_ReturnsDenied()
    {
        await using var rig = await StartedAsync();

        await rig.OpenAsync("""{"mode":"sensors"}""");

        await rig.WaitForLogAsync("sensors:Denied");
        await rig.WaitAsync<Frame>();
        Assert.Empty(rig.Of<DataGet>());
    }

    [WindowsHostFact]
    public async Task Host_SensorsWithPermission_ReturnsTheServiceValues()
    {
        await using var rig = HostRig.Start();
        rig.Handler = async message =>
        {
            if (message is DataGet { Kind: "sensors" } get)
            {
                var reading = new SensorReading("Ryzen", null, new Dictionary<string, double> { ["cpu.load"] = 42.5 });
                await rig.SendAsync(new DataValue(get.Id, "ok", HostPayloads.SensorsToJson(reading)));
            }
        };
        await rig.SendSampleHelloAsync([ModulePermission.Sensors]);
        await rig.WaitAsync<Ready>();

        await rig.OpenAsync("""{"mode":"sensors"}""");

        await rig.WaitForLogAsync("sensors:Ok:Ryzen:42.5");
    }

    [WindowsHostFact]
    public async Task Host_NowPlayingWhenTheServiceSaysUnavailable_ReturnsUnavailable()
    {
        await using var rig = HostRig.Start();
        rig.Handler = async message =>
        {
            if (message is DataGet { Kind: "nowPlaying" } get)
            {
                await rig.SendAsync(new DataValue(get.Id, "unavailable"));
            }
        };
        await rig.SendSampleHelloAsync([ModulePermission.NowPlaying]);
        await rig.WaitAsync<Ready>();

        await rig.OpenAsync("""{"mode":"nowplaying"}""");

        await rig.WaitForLogAsync("nowplaying:Unavailable");
    }

    [WindowsHostFact]
    public async Task Host_ConnectLocalWithoutPermission_ReturnsDenied()
    {
        await using var rig = await StartedAsync([ModulePermission.Internet, ModulePermission.LocalNetwork]);

        await rig.OpenAsync("""{"mode":"connect"}""");

        await rig.WaitForLogAsync("connect:Denied");
        Assert.Empty(rig.Of<LocalConnect>());
    }

    [WindowsHostFact]
    public async Task Host_ConnectLocalRefusedByTheService_ReturnsDenied()
    {
        await using var rig = HostRig.Start();
        rig.Handler = async message =>
        {
            if (message is LocalConnect connect)
            {
                await rig.SendAsync(new LocalRefused(connect.Id, "Port non autorisé"));
            }
        };
        await rig.SendSampleHelloAsync([ModulePermission.ThisComputer]);
        await rig.WaitAsync<Ready>();

        var instance = await rig.OpenAsync("""{"mode":"connect"}""");

        await rig.WaitForLogAsync("connect:Denied");
        var request = Assert.Single(rig.Of<LocalConnect>());
        Assert.Equal((instance.Id, 1234), (request.InstanceId, request.Port));
    }

    [WindowsHostFact]
    public async Task HttpClient_Localhost_GoesThroughRelay()
    {
        await using var rig = HostRig.Start();
        var served = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler = async message =>
        {
            if (message is LocalConnect connect)
            {
                // Un petit serveur HTTP au bout des deux tubes, comme le service le relaiera vers un vrai serveur.
                await rig.RelayAsync(connect, async stream =>
                {
                    served.TrySetResult(await ReadRequestAsync(stream));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"));
                    await Task.Delay(500); // le temps que le client lise
                });
            }
        };
        await rig.SendSampleHelloAsync([ModulePermission.ThisComputer]);
        await rig.WaitAsync<Ready>();

        await rig.OpenAsync("""{"mode":"http"}""");

        await rig.WaitForLogAsync("http:hello");
        var request = await served.Task;
        Assert.StartsWith("GET / HTTP/1.1", request);
        Assert.Contains("Host: localhost:1234", request);
        Assert.Equal(1234, Assert.Single(rig.Of<LocalConnect>()).Port);
    }

    [WindowsHostFact]
    public async Task HttpClient_PinnedCertificate_IsStoredThenEnforced()
    {
        using var first = SelfSigned();
        using var other = SelfSigned();
        var certificate = first;
        var vault = new Dictionary<string, string>();
        await using var rig = HostRig.Start();
        rig.Handler = async message =>
        {
            switch (message)
            {
                case LocalConnect connect:
                    var serving = certificate;
                    await rig.RelayAsync(connect, async stream =>
                    {
                        using var tls = new System.Net.Security.SslStream(stream);
                        await tls.AuthenticateAsServerAsync(serving);
                        await ReadRequestAsync(tls);
                        await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nsecure"));
                        await Task.Delay(500);
                    });
                    break;
                case SecretSet set:
                    lock (vault)
                    {
                        if (set.Value is null) vault.Remove(set.Key); else vault[set.Key] = set.Value;
                    }

                    await rig.SendAsync(new SecretValue(set.Id));
                    break;
                case SecretGet get:
                    string? stored;
                    lock (vault)
                    {
                        stored = vault.GetValueOrDefault(get.Key);
                    }

                    await rig.SendAsync(new SecretValue(get.Id, stored));
                    break;
            }
        };
        await rig.SendSampleHelloAsync([ModulePermission.ThisComputer]);
        await rig.WaitAsync<Ready>();

        // Première rencontre : le certificat (que personne ne signe) est accepté et son empreinte rangée sous « cert:srv ».
        await rig.OpenAsync("""{"mode":"https"}""");
        await rig.WaitForLogAsync("https:secure");
        lock (vault)
        {
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first.RawData)), vault["cert:srv"]);
        }

        // Un autre certificat pour la même clé est refusé.
        certificate = other;
        await rig.OpenAsync("""{"mode":"https"}""");
        await rig.WaitForLogAsync("https-error:HttpRequestException");
        Assert.Single(rig.Of<Log>(), log => log.Message.Contains("https:secure", StringComparison.Ordinal)); // seule la première rencontre a abouti
    }

    private static async Task<string> ReadRequestAsync(Stream stream)
    {
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
        {
            request.Append((char)one[0]);
        }

        return request.ToString();
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
    }

    [WindowsHostFact]
    public async Task Host_OutOfMemoryInTheConstructor_CrashesTheHost()
    {
        // Activator.CreateInstance enveloppe l'exception dans une TargetInvocationException : elle doit tout de même tuer l'hôte.
        await using var rig = HostRig.Start(environment: new Dictionary<string, string> { ["SAMPLE_OOM_CTOR"] = "1" });
        await rig.SendSampleHelloAsync();
        await rig.WaitAsync<Ready>();

        await rig.OpenAsync();

        Assert.Equal(unchecked((int)0xE0434352), await rig.WaitForExitAsync());
        Assert.Empty(rig.Of<Status>());
    }

    [WindowsHostFact]
    public async Task Host_RenderThrowsOnce_RecoversWithoutInvalidate()
    {
        await using var rig = await StartedAsync();

        var instance = await rig.OpenAsync("""{"mode":"throwOnce"}""");

        var error = await rig.WaitAsync<Status>(s => s.InstanceId == instance.Id);
        Assert.Equal((ModuleHealth.Offline, "Erreur du module"), (error.Health, error.Message));
        // Sans que le module ne redemande quoi que ce soit : un nouvel essai après le délai d'une seconde réussit.
        var frame = await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id, TimeSpan.FromSeconds(10));
        await rig.WaitUntilAsync(() => rig.Of<Status>().Count(s => s.InstanceId == instance.Id) == 2);
        Assert.Equal(ModuleHealth.Online, rig.Of<Status>().Last(s => s.InstanceId == instance.Id).Health);
        using var bitmap = instance.Read(frame.Buffer);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(5, 5));
    }

    [WindowsHostFact]
    public async Task Host_LookupReplyTooBigForAFrame_AnswersEmptyInsteadOfNothing()
    {
        await using var rig = await StartedAsync();

        await rig.SendAsync(new Lookup(21, "repo", "huge"));
        await rig.SendAsync(new Lookup(22, "repo", "ok"));

        Assert.Empty((await rig.WaitAsync<LookupReply>(r => r.Id == 21)).Results);
        Assert.Single((await rig.WaitAsync<LookupReply>(r => r.Id == 22)).Results);
    }

    [WindowsHostFact]
    public async Task Host_RunActionConnectingToThisComputer_CarriesTheInstanceId()
    {
        await using var rig = HostRig.Start();
        rig.Handler = async message =>
        {
            if (message is LocalConnect connect)
            {
                await rig.SendAsync(new LocalRefused(connect.Id, "Port non autorisé"));
            }
        };
        await rig.SendSampleHelloAsync([ModulePermission.ThisComputer]);
        await rig.WaitAsync<Ready>();
        var instance = Guid.NewGuid();
        using var settings = JsonDocument.Parse("""{"mode":"réglé"}""");

        await rig.SendAsync(new RunAction(5, instance, "connect", settings.RootElement.Clone()));

        var reply = await rig.WaitAsync<ActionReply>(r => r.Id == 5);
        Assert.Equal(new ActionResult(false, "connect:Denied"), reply.Result);
        Assert.Equal(instance, Assert.Single(rig.Of<LocalConnect>()).InstanceId);
    }

    [WindowsHostTheory]
    [InlineData("http://localhost:4321/", true, 4321)]
    [InlineData("http://LOCALHOST:4322/", true, 4322)]
    [InlineData("http://127.0.0.1:4323/", true, 4323)]
    [InlineData("http://[::1]:4324/", true, 4324)]
    [InlineData("http://nom-inexistant.invalid:4325/", false, 0)]
    [InlineData("http://[::ffff:127.0.0.1]:1/", false, 0)]
    public async Task HttpClient_OnlyTheAddressesOfThisComputerGoThroughTheRelay(string url, bool relayed, int port)
    {
        await using var rig = HostRig.Start();
        rig.Handler = async message =>
        {
            if (message is LocalConnect connect)
            {
                await rig.SendAsync(new LocalRefused(connect.Id, "Port non autorisé"));
            }
        };
        await rig.SendSampleHelloAsync([ModulePermission.ThisComputer, ModulePermission.Internet]);
        await rig.WaitAsync<Ready>();

        await rig.OpenAsync(JsonSerializer.Serialize(new { mode = "http", url }));

        await rig.WaitForLogAsync("http-error:");
        if (relayed)
        {
            Assert.Equal(port, Assert.Single(rig.Of<LocalConnect>()).Port);
        }
        else
        {
            Assert.Empty(rig.Of<LocalConnect>());
        }
    }

    [WindowsHostFact]
    public async Task Host_OutOfMemoryInRender_CrashesTheHost()
    {
        await using var rig = await StartedAsync();

        await rig.OpenAsync("""{"mode":"oomRender"}""");

        // Jamais « Erreur du module » : l'hôte tombe (exception non rattrapée, 0xE0434352) et le superviseur du service s'en charge.
        Assert.Equal(unchecked((int)0xE0434352), await rig.WaitForExitAsync());
        Assert.Empty(rig.Of<Status>());
    }

    [WindowsHostFact]
    public async Task Host_OutOfMemoryInStart_CrashesTheHost()
    {
        await using var rig = await StartedAsync();

        await rig.OpenAsync("""{"mode":"oomStart"}""");

        Assert.Equal(unchecked((int)0xE0434352), await rig.WaitForExitAsync());
    }

    [WindowsHostFact]
    public async Task Host_Secrets_GoThroughTheService()
    {
        await using var rig = HostRig.Start();
        var vault = new Dictionary<string, string>();
        rig.Handler = async message =>
        {
            switch (message)
            {
                case SecretSet set:
                    if (set.Value is null) vault.Remove(set.Key); else vault[set.Key] = set.Value;
                    await rig.SendAsync(new SecretValue(set.Id));
                    break;
                case SecretGet get:
                    await rig.SendAsync(new SecretValue(get.Id, vault.GetValueOrDefault(get.Key)));
                    break;
            }
        };
        await rig.SendSampleHelloAsync();
        await rig.WaitAsync<Ready>();

        await rig.OpenAsync("""{"mode":"secrets"}""");

        await rig.WaitForLogAsync("secret:v1:null");
        Assert.Equal("v1", vault["k"]);
    }

    [WindowsHostFact]
    public async Task Host_ModuleStatus_IsForwarded()
    {
        await using var rig = await StartedAsync();

        var instance = await rig.OpenAsync("""{"mode":"status"}""");

        var status = await rig.WaitAsync<Status>(s => s.InstanceId == instance.Id);
        Assert.Equal((ModuleHealth.NotConfigured, "À régler"), (status.Health, status.Message));
    }

    [WindowsHostFact]
    public async Task Host_LogIsLimitedToTwentyMessagesPerSecond()
    {
        await using var rig = await StartedAsync();

        await rig.OpenAsync("""{"mode":"logburst"}""");
        await rig.WaitAsync<Frame>();
        await Task.Delay(300);

        var burst = rig.Of<Log>().Count(log => log.Message.Contains("burst-", StringComparison.Ordinal));
        Assert.InRange(burst, 15, 20);
    }

    [WindowsHostFact]
    public async Task Host_TwoModuleClasses_Fails()
    {
        await using var rig = HostRig.Start();
        await rig.SendHelloAsync(Path.Combine(HostRig.BaseFolder, "two"), "Trofeo.ModuleHost.Tests.TwoClassesModule.dll");

        var failure = await rig.WaitAsync<Failure>();

        Assert.Contains("FirstModule", failure.Message);
        Assert.Contains("SecondModule", failure.Message);
        Assert.NotEqual(0, await rig.WaitForExitAsync());
        Assert.Empty(rig.Of<Ready>());
    }

    [WindowsHostTheory]
    [InlineData("Absent.dll")]
    [InlineData(@"..\Trofeo.ModuleHost.Tests.dll")]
    [InlineData("AshboardVision.Sdk.dll")] // charge, mais ne contient aucune classe de module
    public async Task Host_BadEntry_Fails(string entry)
    {
        await using var rig = HostRig.Start();
        await rig.SendHelloAsync(HostRig.SampleFolder, entry);

        await rig.WaitAsync<Failure>();

        Assert.NotEqual(0, await rig.WaitForExitAsync());
        Assert.Empty(rig.Of<Ready>());
    }

    [WindowsHostFact]
    public async Task Host_NotAHelloFirst_Fails()
    {
        await using var rig = HostRig.Start();

        await rig.SendAsync(new Limits(10));

        await rig.WaitAsync<Failure>();
        Assert.NotEqual(0, await rig.WaitForExitAsync());
    }

    [WindowsHostTheory]
    [InlineData(0, "1.0")]
    [InlineData(30, "2.0")]
    public async Task Host_InvalidHello_Fails(int maxFps, string protocol)
    {
        await using var rig = HostRig.Start();
        await rig.SendAsync(new Hello(protocol, HostRig.SampleFolder, "Trofeo.ModuleHost.Tests.SampleModule.dll", "test.sample", [],
            Path.GetTempPath(), HostPayloads.AppearanceToJson(AppearanceSettings.Default), maxFps));

        await rig.WaitAsync<Failure>();
        Assert.NotEqual(0, await rig.WaitForExitAsync());
    }

    [WindowsHostFact]
    public async Task Host_ModuleThrowsInRender_ReportsOfflineAndKeepsRunning()
    {
        await using var rig = await StartedAsync();

        var broken = await rig.OpenAsync("""{"mode":"throwRender"}""");
        var status = await rig.WaitAsync<Status>(s => s.InstanceId == broken.Id);
        Assert.Equal((ModuleHealth.Offline, "Erreur du module"), (status.Health, status.Message));

        // L'hôte continue : une autre instance dessine, et la première ne produit aucune image.
        var healthy = await rig.OpenAsync("""{"color":"#00FF00"}""");
        await rig.WaitAsync<Frame>(f => f.InstanceId == healthy.Id);
        Assert.False(rig.Process.HasExited);
        Assert.DoesNotContain(rig.Of<Frame>(), f => f.InstanceId == broken.Id);
        Assert.Single(rig.Of<Status>(), s => s.InstanceId == broken.Id);
    }

    [WindowsHostFact]
    public async Task Host_ModuleThrowsInStart_ReportsOfflineAndKeepsRunning()
    {
        await using var rig = await StartedAsync();

        var broken = await rig.OpenAsync("""{"mode":"throwStart"}""");

        var status = await rig.WaitAsync<Status>(s => s.InstanceId == broken.Id);
        Assert.Equal((ModuleHealth.Offline, "Erreur du module"), (status.Health, status.Message));
        Assert.Contains(rig.Of<Log>(), log => log.Level == "error" && log.Message.Contains("boom au démarrage", StringComparison.Ordinal));
        var healthy = await rig.OpenAsync();
        await rig.WaitAsync<Frame>(f => f.InstanceId == healthy.Id);
        Assert.DoesNotContain(rig.Of<Frame>(), f => f.InstanceId == broken.Id); // une instance qui n'a pas démarré ne dessine pas
    }

    [WindowsHostFact]
    public async Task Host_Lookup_AnswersFromTheModule()
    {
        await using var rig = await StartedAsync();

        await rig.SendAsync(new Lookup(7, "repo", "ash"));

        var reply = await rig.WaitAsync<LookupReply>(r => r.Id == 7);
        var result = Assert.Single(reply.Results);
        Assert.Equal("Résultat ash", result.Label);
        Assert.Equal("repo:ash:Object", result.Value.GetString());
    }

    [WindowsHostFact]
    public async Task Host_LookupThatThrows_AnswersWithNothingAndKeepsRunning()
    {
        await using var rig = await StartedAsync();

        await rig.SendAsync(new Lookup(8, "repo", "boom"));
        await rig.SendAsync(new Lookup(9, "repo", "ok"));

        Assert.Empty((await rig.WaitAsync<LookupReply>(r => r.Id == 8)).Results);
        Assert.Single((await rig.WaitAsync<LookupReply>(r => r.Id == 9)).Results);
    }

    [WindowsHostFact]
    public async Task Host_RunAction_UsesTheSettingsOfTheRequest()
    {
        await using var rig = await StartedAsync();
        using var settings = JsonDocument.Parse("""{"mode":"réglé"}""");

        await rig.SendAsync(new RunAction(3, Guid.NewGuid(), "test", settings.RootElement.Clone()));
        await rig.SendAsync(new RunAction(4, Guid.NewGuid(), "echec", settings.RootElement.Clone()));

        var ok = (await rig.WaitAsync<ActionReply>(r => r.Id == 3)).Result;
        Assert.Equal(new ActionResult(true, "action test avec réglé"), ok);
        Assert.False((await rig.WaitAsync<ActionReply>(r => r.Id == 4)).Result.Ok);
    }

    [WindowsHostFact]
    public async Task Host_AppearanceChanged_RedrawsWithTheNewTheme()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync();
        await rig.WaitAsync<Frame>();
        var before = instance.Read(0).GetPixel(instance.Width - 5, 5);

        await rig.SendAsync(new AppearanceChanged(HostPayloads.AppearanceToJson(new AppearanceSettings("pixel"))));
        var second = await rig.WaitAsync<Frame>(f => f.Buffer == 1);

        var after = instance.Read(second.Buffer).GetPixel(instance.Width - 5, 5);
        Assert.NotEqual(before, after);
        var pixel = AppearanceResolver.Resolve(new AppearanceSettings("pixel"), new SkiaSystemFonts()).Theme.Accent;
        Assert.Equal(pixel.WithAlpha(255), after.WithAlpha(255));
    }

    [WindowsHostFact]
    public async Task Host_Close_DisposesTheModule()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"dispose"}""");
        await rig.WaitAsync<Frame>();

        await rig.SendAsync(new Close(instance.Id));

        await rig.WaitForLogAsync("disposed");
        Assert.False(rig.Process.HasExited);
    }

    [WindowsHostFact]
    public async Task Host_TwoInstances_DrawIntoTheirOwnBuffers()
    {
        await using var rig = await StartedAsync();

        var red = await rig.OpenAsync("""{"color":"#FF0000"}""", width: 64, height: 32);
        var blue = await rig.OpenAsync("""{"color":"#0000FF"}""", width: 80, height: 40);

        await rig.WaitAsync<Frame>(f => f.InstanceId == red.Id);
        await rig.WaitAsync<Frame>(f => f.InstanceId == blue.Id);
        Assert.Equal(new SKColor(255, 0, 0), red.Read(0).GetPixel(3, 3));
        Assert.Equal(new SKColor(0, 0, 255), blue.Read(0).GetPixel(3, 3));
    }

    [WindowsHostFact]
    public async Task Host_InputClosed_Exits()
    {
        await using var rig = await StartedAsync();
        await rig.OpenAsync();
        await rig.WaitAsync<Frame>();

        rig.CloseInput();

        Assert.Equal(0, await rig.WaitForExitAsync(TimeSpan.FromSeconds(15)));
    }

    [WindowsHostFact]
    public async Task Host_Shutdown_Exits()
    {
        await using var rig = await StartedAsync();
        await rig.OpenAsync();
        await rig.WaitAsync<Frame>();

        await rig.SendAsync(new Shutdown());

        Assert.Equal(0, await rig.WaitForExitAsync(TimeSpan.FromSeconds(15)));
    }

    [WindowsHostFact]
    public async Task Host_Has_ReflectsTheGrantedPermissions()
    {
        await using var rig = await StartedAsync([ModulePermission.Sensors, ModulePermission.Internet]);

        await rig.OpenAsync("""{"mode":"has"}""");

        await rig.WaitForLogAsync("has:Internet,Sensors");
    }
}
