using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

public class CompatibilityTests
{
    private static readonly IReadOnlySet<int> Majors = new HashSet<int> { 1 };

    [Fact]
    public void Current_IsOnePointZero_AndMajorOneIsSupported()
    {
        Assert.Equal(new SdkVersion(1, 0), Compatibility.Current);
        Assert.Equal("1.0", Compatibility.Current.ToString());
        Assert.Equal([1], Compatibility.SupportedMajors.ToArray());
    }

    [Fact]
    public void Check_OlderMinorRequired_IsCompatible()
    {
        Assert.Equal(CompatibilityKind.Compatible, Compatibility.Check(new SdkVersion(1, 0)));
        Assert.Equal(CompatibilityKind.Compatible, Compatibility.Check(new SdkVersion(1, 2), new SdkVersion(1, 5), Majors));
        Assert.Equal(CompatibilityKind.Compatible, Compatibility.Check(new SdkVersion(1, 5), new SdkVersion(1, 5), Majors));
    }

    [Fact]
    public void Check_NewerMinorRequired_NeedsNewerApp()
    {
        Assert.Equal(CompatibilityKind.NeedsNewerApp, Compatibility.Check(new SdkVersion(1, 6), new SdkVersion(1, 5), Majors));
        Assert.Equal(CompatibilityKind.NeedsNewerApp, Compatibility.Check(new SdkVersion(1, 1)));
    }

    [Fact]
    public void Check_UnsupportedMajor_IsTooOld()
    {
        Assert.Equal(CompatibilityKind.TooOld, Compatibility.Check(new SdkVersion(0, 9), new SdkVersion(1, 5), Majors));
        Assert.Equal(CompatibilityKind.TooOld, Compatibility.Check(new SdkVersion(0, 9)));
    }

    [Fact]
    public void Check_NewerMajor_NeedsNewerApp()
    {
        Assert.Equal(CompatibilityKind.NeedsNewerApp, Compatibility.Check(new SdkVersion(2, 0)));
        Assert.Equal(CompatibilityKind.NeedsNewerApp, Compatibility.Check(new SdkVersion(2, 0), new SdkVersion(1, 5), Majors));
    }

    [Fact]
    public void Check_OldMajorKeptDuringTransition_IsCompatible()
    {
        // Le logiciel parle 2.0 mais comprend encore la majeure 1 : un module 1.9 passe, un module 2.1 non.
        var majors = new HashSet<int> { 1, 2 };
        Assert.Equal(CompatibilityKind.Compatible, Compatibility.Check(new SdkVersion(1, 9), new SdkVersion(2, 0), majors));
        Assert.Equal(CompatibilityKind.NeedsNewerApp, Compatibility.Check(new SdkVersion(2, 1), new SdkVersion(2, 0), majors));
        Assert.Equal(CompatibilityKind.TooOld, Compatibility.Check(new SdkVersion(0, 9), new SdkVersion(2, 0), majors));
    }

    [Fact]
    public void Message_NeedsNewerApp_IsExact()
    {
        Assert.Equal(
            "Ce module demande une version plus récente d'Ashboard Vision : mets à jour le logiciel.",
            Compatibility.Message(CompatibilityKind.NeedsNewerApp));
    }

    [Fact]
    public void Message_TooOld_AsksTheAuthorToUpdate()
    {
        var message = Compatibility.Message(CompatibilityKind.TooOld);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("auteur", message);
        Assert.Equal("", Compatibility.Message(CompatibilityKind.Compatible));
    }

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("1.12", 1, 12)]
    [InlineData("0.9", 0, 9)]
    public void SdkVersion_TryParse_ReadsMajorDotMinor(string text, int major, int minor)
    {
        Assert.True(SdkVersion.TryParse(text, out var version));
        Assert.Equal(new SdkVersion(major, minor), version);
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("v1.0")]
    [InlineData("1.x")]
    [InlineData("-1.0")]
    [InlineData(" 1.0")]
    [InlineData("1.0-beta")]
    public void SdkVersion_TryParse_RefusesOtherForms(string? text)
    {
        Assert.False(SdkVersion.TryParse(text, out _));
    }

    [Fact]
    public void SdkVersion_CompareTo_OrdersByMajorThenMinor()
    {
        Assert.True(new SdkVersion(1, 9).CompareTo(new SdkVersion(2, 0)) < 0);
        Assert.True(new SdkVersion(1, 2).CompareTo(new SdkVersion(1, 10)) < 0);
        Assert.Equal(0, new SdkVersion(1, 2).CompareTo(new SdkVersion(1, 2)));
    }
}
