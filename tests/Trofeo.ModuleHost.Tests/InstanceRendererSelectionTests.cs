using Trofeo.Extensions.Protocol;

namespace Trofeo.ModuleHost.Tests;

/// <summary>
/// Les éléments choisis d'un module de la communauté (plan 9, tâche 5) : dans « Liquid Glass avancé », <c>DrawSelection</c> note le
/// rectangle au lieu de dessiner, et l'hôte l'envoie avec l'image dans <see cref="Frame"/>. Le module d'essai (mode <c>select</c>) appelle
/// <c>DrawSelection</c> ; l'hôte est le vrai.
/// </summary>
[Trait("Windows", "ModuleHost")]
[Collection("HostRig")]
public sealed class InstanceRendererSelectionTests
{
    private static async Task<HostRig> StartedAsync(string appearance)
    {
        var rig = HostRig.Start();
        rig.AutoFrameDone = true;
        await rig.SendSampleHelloAsync(null, 30, appearance);
        await rig.WaitAsync<Ready>();
        return rig;
    }

    [WindowsHostFact]
    public async Task InstanceRenderer_SendsSelectionsWithFrame()
    {
        await using var rig = await StartedAsync("liquid-glass-avance");
        var instance = await rig.OpenAsync("""{"mode":"select"}""");

        var frame = await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id);

        var selection = Assert.Single(frame.Selections!);
        Assert.Equal(new SelectionRect(10, 5, 50, 20), selection); // relatif à la case ; la pastille n'est pas dessinée par l'hôte
    }

    [WindowsHostFact]
    public async Task InstanceRenderer_Selections_DrawNothingInTheImage()
    {
        await using var rig = await StartedAsync("liquid-glass-avance");
        var plain = await rig.OpenAsync("""{"mode":"static"}""");
        var selecting = await rig.OpenAsync("""{"mode":"select"}""");

        var plainFrame = await rig.WaitAsync<Frame>(f => f.InstanceId == plain.Id);
        var selectingFrame = await rig.WaitAsync<Frame>(f => f.InstanceId == selecting.Id);

        Assert.Null(plainFrame.Selections);
        Assert.NotNull(selectingFrame.Selections);
        using var a = plain.Read(plainFrame.Buffer);
        using var b = selecting.Read(selectingFrame.Buffer);
        Assert.True(a.Bytes.AsSpan().SequenceEqual(b.Bytes), "L'hôte ne dessine pas la pastille : l'image est celle du module sans elle.");
    }

    [WindowsHostFact]
    public async Task InstanceRenderer_Selections_AreCappedAndFiltered()
    {
        await using var rig = await StartedAsync("liquid-glass-avance");
        var instance = await rig.OpenAsync("""{"mode":"selectMany"}""");

        var frame = await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id);

        // Ni NaN, ni infini, ni rectangle vide ou à l'envers : les seize premiers rectangles valides, et l'image est bien partie.
        var selections = frame.Selections!;
        Assert.Equal(Frame.MaxSelections, selections.Count);
        for (var i = 0; i < selections.Count; i++)
        {
            Assert.Equal(new SelectionRect(i * 4, 2, 3, 10), selections[i]);
        }
    }

    [WindowsHostFact]
    public async Task InstanceRenderer_OtherThemes_DrawTheOldTabAndSendNoSelection()
    {
        await using var rig = await StartedAsync("classique");
        var instance = await rig.OpenAsync("""{"mode":"select"}""");

        var frame = await rig.WaitAsync<Frame>(f => f.InstanceId == instance.Id);

        Assert.Null(frame.Selections);
        using var image = instance.Read(frame.Buffer);
        Assert.NotEqual(0, image.GetPixel(10 + 1, 5 + 1).Alpha); // l'onglet d'avant est dessiné dans l'image, comme avant
    }
}
