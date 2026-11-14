using Trofeo.Extensions.Protocol;

namespace Trofeo.ModuleHost.Tests;

/// <summary>
/// Une instance cachée (sa case est dans une vue qui n'est pas affichée) : le module tourne dans le vrai hôte, mais l'hôte ne la
/// dessine pas avant qu'elle redevienne visible. Les modules d'essai démarrent lentement (<c>startDelayMs</c>) pour que le message
/// <see cref="Visibility"/>, qui suit l'ouverture, arrive avant la première image.
/// </summary>
[Trait("Windows", "ModuleHost")]
[Collection("HostRig")]
public sealed class InstanceRendererVisibilityTests
{
    private static async Task<HostRig> StartedAsync(int maxFps = 30)
    {
        var rig = HostRig.Start();
        rig.AutoFrameDone = true;
        await rig.SendSampleHelloAsync(null, maxFps);
        await rig.WaitAsync<Ready>();
        return rig;
    }

    [WindowsHostFact]
    public async Task HiddenInstance_IsNotDrawn_EvenWhenAnimating()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"animated","startDelayMs":300}""");
        await rig.SendAsync(new Visibility(instance.Id, false));

        await rig.WaitForLogAsync("started:animated");
        await Task.Delay(1000);
        Assert.Empty(rig.Of<Frame>());

        // Visible de nouveau, l'instance animée reprend son rythme.
        await rig.SendAsync(new Visibility(instance.Id, true));
        await rig.WaitUntilAsync(() => rig.Of<Frame>().Count(f => f.InstanceId == instance.Id) >= 3);
        Assert.True(rig.Of<Frame>().All(f => f.Animating));
    }

    [WindowsHostFact]
    public async Task HiddenInstance_DirtyRequest_IsDrawnWhenShown()
    {
        await using var rig = await StartedAsync();
        // Après son démarrage lent, le module demande dix dessins, espacés de 50 ms, pendant que la case est cachée.
        var instance = await rig.OpenAsync("""{"mode":"burst","startDelayMs":300}""");
        await rig.SendAsync(new Visibility(instance.Id, false));

        await rig.WaitForLogAsync("started:burst");
        await Task.Delay(1000);
        Assert.Empty(rig.Of<Frame>());

        await rig.SendAsync(new Visibility(instance.Id, true));
        var frame = await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id, TimeSpan.FromSeconds(5));
        Assert.False(frame.Animating);

        // Une seule image pour toutes ces demandes accumulées.
        await Task.Delay(300);
        Assert.Single(rig.Of<Frame>());
    }

    [WindowsHostFact]
    public async Task HiddenInstance_KeepsRunning()
    {
        await using var rig = await StartedAsync();
        var instance = await rig.OpenAsync("""{"mode":"ticker","startDelayMs":300}""");
        await rig.SendAsync(new Visibility(instance.Id, false));

        await rig.WaitForLogAsync("started:ticker"); // StartAsync est appelé, cachée ou non
        await rig.WaitForLogAsync("tick-0");
        await rig.WaitForLogAsync("tick-5"); // la minuterie du module tourne toujours
        Assert.Empty(rig.Of<Frame>());
    }

    [WindowsHostFact]
    public async Task Visibility_OfAnUnknownInstance_IsIgnored()
    {
        await using var rig = await StartedAsync();

        await rig.SendAsync(new Visibility(Guid.NewGuid(), false));
        var instance = await rig.OpenAsync();

        await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id);
        Assert.False(rig.Process.HasExited);
    }
}
