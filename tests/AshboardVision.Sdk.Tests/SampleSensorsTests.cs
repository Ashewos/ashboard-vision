using AshboardVision.Sdk.Tool;

namespace AshboardVision.Sdk.Tests;

/// <summary>Les fausses données de capteurs du banc d'essai parlent les unités du logiciel : un module qui les lit ne doit pas être trompé.</summary>
public sealed class SampleSensorsTests
{
    [Theory]
    [InlineData("ram.used")]
    [InlineData("ram.total")]
    public void Ram_IsInGigabytes(string key)
    {
        // Le logiciel donne la mémoire vive en Go (SensorKeys) : 32 Go de mémoire, pas 32 768.
        var value = SampleSensors.Reading().Values[key];

        Assert.InRange(value, 1, 1024);
    }

    [Fact]
    public void Ram_UsedFitsInTotal()
    {
        var values = SampleSensors.Reading().Values;

        Assert.InRange(values["ram.used"], 0, values["ram.total"]);
    }

    [Theory]
    [InlineData("gpu.vram.used")]
    [InlineData("gpu.vram.total")]
    public void Vram_StaysInMegabytes(string key)
    {
        // La mémoire vidéo, elle, est en Mo : 12 Go de VRAM s'écrivent 12 288.
        Assert.InRange(SampleSensors.Reading().Values[key], 256, 1_000_000);
    }
}
