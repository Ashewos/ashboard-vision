using AshboardVision.Sdk;
using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

public class PermissionTests
{
    [Theory]
    [InlineData(ModulePermission.LocalNetwork, "reseau-local")]
    [InlineData(ModulePermission.Internet, "internet")]
    [InlineData(ModulePermission.ThisComputer, "ce-pc")]
    [InlineData(ModulePermission.Sensors, "capteurs")]
    [InlineData(ModulePermission.NowPlaying, "musique")]
    public void ToManifest_AndTryParse_RoundTrip(ModulePermission permission, string name)
    {
        Assert.Equal(name, ModulePermissions.ToManifest(permission));
        Assert.True(ModulePermissions.TryParse(name, out var back));
        Assert.Equal(permission, back);
    }

    [Theory]
    [InlineData("fichiers")]
    [InlineData("Internet")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_UnknownName_IsRefused(string? name)
    {
        Assert.False(ModulePermissions.TryParse(name, out _));
    }

    [Fact]
    public void Labels_AreTheOnesOfTheDesign()
    {
        Assert.Equal("Réseau local", PermissionTexts.Label(ModulePermission.LocalNetwork));
        Assert.Equal("Internet", PermissionTexts.Label(ModulePermission.Internet));
        Assert.Equal("Cet ordinateur (ports choisis dans les réglages du module)", PermissionTexts.Label(ModulePermission.ThisComputer));
        Assert.Equal("Capteurs", PermissionTexts.Label(ModulePermission.Sensors));
        Assert.Equal("Musique en cours", PermissionTexts.Label(ModulePermission.NowPlaying));
    }

    [Fact]
    public void Explanations_ExistForEveryPermission_AndAreDistinct()
    {
        var texts = Enum.GetValues<ModulePermission>().Select(PermissionTexts.Explanation).ToArray();
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Length, texts.Distinct().Count());
    }
}
