using Microsoft.Win32;

namespace Trofeo.Display.Tests;

/// <summary>Sur une clé de test sous HKCU : la vraie clé <c>Run</c> de l'utilisateur n'est jamais touchée.</summary>
public sealed class RegistryRunKeyTests : IDisposable
{
    private readonly string _path = @"Software\ashboard-vision-tests\" + Guid.NewGuid().ToString("N");

    private RegistryRunKey Key => new(_path);

    [Fact]
    public void WriteReadDelete_RoundTrip()
    {
        Assert.Null(Key.Read("ashboard-vision"));

        const string command = "\"C:/Program Files/Ashboard Vision/Trofeo.Display.exe\"";
        Key.Write("ashboard-vision", command);
        Assert.Equal(command, Key.Read("ashboard-vision"));

        Key.Delete("ashboard-vision");
        Assert.Null(Key.Read("ashboard-vision"));
    }

    [Fact]
    public void Delete_MissingValueOrKey_IsNotAnError()
    {
        Key.Delete("absente");
        Key.Write("autre", "x");
        Key.Delete("absente");
        Assert.Equal("x", Key.Read("autre"));
    }

    [Fact]
    public void DefaultKey_IsTheWindowsRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryRunKey.DefaultKeyPath);
    }

    public void Dispose()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\ashboard-vision-tests", writable: true);
        root?.DeleteSubKeyTree(Path.GetFileName(_path), throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software", writable: true);
        if (root is not null && root.SubKeyCount == 0)
            parent?.DeleteSubKey("ashboard-vision-tests", throwOnMissingSubKey: false);
    }
}
