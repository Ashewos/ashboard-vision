using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Tests;

/// <summary>Un module est enfermé par des droits d'accès (ACL) sur son dossier : un disque qui n'en garde pas (exFAT, FAT32) ne peut pas les porter.</summary>
public sealed class VolumeAclsTests
{
    [Theory]
    [InlineData(0x00000008u, true)] // FILE_PERSISTENT_ACLS seul
    [InlineData(0x00000008u | 0x00000002u | 0x00000001u, true)] // avec d'autres capacités
    [InlineData(0x00000000u, false)]
    [InlineData(0x00000002u | 0x00000001u | 0x00000004u, false)] // beaucoup de capacités, mais pas celle-là (FAT32 / exFAT)
    public void SupportsPersistentAcls_ReadsTheFlag(uint flags, bool expected)
    {
        Assert.Equal(expected, VolumeAcls.SupportsPersistentAcls(flags));
    }

    [Fact]
    public void Check_TheTemporaryFolder_IsKnownToHaveAcls()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Le dossier temporaire est sur le disque du système : NTFS.
        Assert.True(VolumeAcls.Check(Path.GetTempPath()));
    }

    [Fact]
    public void Check_AnUnreadableVolume_IsUnknownNotRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = "ZYXWVUTSRQPONMLKJIHGFEDCBA".First(letter => !used.Contains(letter));

        Assert.Null(VolumeAcls.Check($@"{free}:\absent\dossier"));
    }
}
