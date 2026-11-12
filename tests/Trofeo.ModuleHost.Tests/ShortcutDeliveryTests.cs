using Trofeo.Extensions.Protocol;

namespace Trofeo.ModuleHost.Tests;

/// <summary>
/// Un raccourci que le service transmet (<see cref="Shortcut"/>) arrive à <c>OnShortcutAsync</c> de l'instance de la case, dans le vrai
/// hôte. L'hôte ne filtre rien : ni la liste des raccourcis du module (le module répond ce qu'il veut) ni la vue (c'est le service qui
/// décide de transmettre ou non). Un module qui échoue, une instance inconnue ou un module lent ne font jamais tomber l'hôte.
/// </summary>
[Trait("Windows", "ModuleHost")]
[Collection("HostRig")]
public sealed class ShortcutDeliveryTests
{
    private static async Task<HostRig> StartedAsync()
    {
        var rig = HostRig.Start();
        rig.AutoFrameDone = true;
        await rig.SendSampleHelloAsync();
        await rig.WaitAsync<Ready>();
        return rig;
    }

    [WindowsHostFact]
    public async Task Shortcut_IsDeliveredToTheInstance()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"echo"}""");
        await rig.WaitForLogAsync("started:echo");

        await rig.SendAsync(new Shortcut(instance.Id, "toggle"));
        await rig.SendAsync(new Shortcut(instance.Id, "next"));

        // Sur l'instance de la case (son mode vient de ses réglages), et dans l'ordre d'envoi.
        await rig.WaitForLogAsync("shortcut:toggle:echo");
        await rig.WaitForLogAsync("shortcut:next:echo");
        var lines = rig.Of<Log>().Select(log => log.Message).Where(message => message.Contains("shortcut:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("shortcut:toggle", lines[0]);
        Assert.Contains("shortcut:next", lines[1]);
    }

    [WindowsHostFact]
    public async Task Shortcut_SentBeforeTheModuleStarted_IsDeliveredOnceStarted()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"echo","startDelayMs":500}""");

        await rig.SendAsync(new Shortcut(instance.Id, "early"));

        await rig.WaitForLogAsync("started:echo");
        await rig.WaitForLogAsync("shortcut:early:echo");
    }

    [WindowsHostFact]
    public async Task Shortcut_UnknownId_IsIgnoredWithoutCrash()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"echo"}""");
        await rig.WaitForLogAsync("started:echo");

        // L'hôte ne vérifie pas la liste du manifeste : le module reçoit l'identifiant et en fait ce qu'il veut (ici, rien).
        await rig.SendAsync(new Shortcut(instance.Id, "n-existe-pas"));
        await rig.SendAsync(new Shortcut(Guid.NewGuid(), "toggle")); // une instance inconnue (fermée entre-temps) est ignorée

        await rig.WaitForLogAsync("shortcut:n-existe-pas:echo");
        await Task.Delay(300);
        Assert.False(rig.Process.HasExited);
        Assert.DoesNotContain(rig.Of<Failure>(), _ => true);
        Assert.Single(rig.Of<Log>(), log => log.Message.Contains("shortcut:", StringComparison.Ordinal));
        await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id); // l'instance dessine toujours
    }

    [WindowsHostFact]
    public async Task Shortcut_ThrowingModule_IsLoggedAndTheHostKeepsRunning()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"echo"}""");
        await rig.WaitForLogAsync("started:echo");

        await rig.SendAsync(new Shortcut(instance.Id, "boom"));

        var error = await rig.WaitAsync<Log>(log => log.Level == "error" && log.Message.Contains("OnShortcutAsync", StringComparison.Ordinal));
        Assert.Contains("boom du raccourci", error.Message);
        Assert.False(rig.Process.HasExited);

        // Le raccourci suivant est livré normalement, et la case n'est pas mise en erreur pour autant.
        await rig.SendAsync(new Shortcut(instance.Id, "toggle"));
        await rig.WaitForLogAsync("shortcut:toggle:echo");
        Assert.DoesNotContain(rig.Of<Status>(), status => status.Health != Trofeo.ModuleKit.ModuleHealth.Online);
        Assert.False(rig.Process.HasExited);
    }

    [WindowsHostFact]
    public async Task Shortcut_ForHiddenInstance_IsStillDelivered()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"echo"}""");
        await rig.WaitForLogAsync("started:echo");
        await rig.SendAsync(new Visibility(instance.Id, false));

        // Le service seul filtre par vue : l'hôte livre même à une case cachée.
        await rig.SendAsync(new Shortcut(instance.Id, "toggle"));

        await rig.WaitForLogAsync("shortcut:toggle:echo");
    }

    [WindowsHostFact]
    public async Task Shortcut_SlowModule_DoesNotPileUpWithoutLimit()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"slowShortcut"}""");
        await rig.WaitForLogAsync("started:slowShortcut");

        // Soixante raccourcis d'un coup devant un module qui met 200 ms à chacun : la file est bornée, le surplus est jeté et dit au journal.
        for (var i = 0; i < 60; i++)
        {
            await rig.SendAsync(new Shortcut(instance.Id, "t" + i));
        }

        await rig.WaitAsync<Log>(log => log.Level == "warn" && log.Message.Contains("trop de raccourcis", StringComparison.Ordinal));
        await Task.Delay(1500); // le temps de vider ce qui a été gardé
        var delivered = rig.Of<Log>().Count(log => log.Message.Contains("shortcut:t", StringComparison.Ordinal));
        Assert.InRange(delivered, 1, 20);
        Assert.False(rig.Process.HasExited);
    }
}
