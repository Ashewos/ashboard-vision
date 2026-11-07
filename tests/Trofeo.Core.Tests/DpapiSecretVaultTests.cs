using System.Security.Cryptography;
using System.Text;
using Trofeo.Core.Secrets;

namespace Trofeo.Core.Tests;

public sealed class DpapiSecretVaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-vault-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "secrets.dat");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Vault_SetThenGet_RoundTrips()
    {
        new DpapiSecretVault(FilePath).Set("network", "token", "s3cr3t-ünï");

        var value = new DpapiSecretVault(FilePath).Get("network", "token");

        Assert.Equal("s3cr3t-ünï", value);
    }

    [Fact]
    public void Vault_File_IsNotPlainText()
    {
        new DpapiSecretVault(FilePath).Set("network", "token", "valeur-tres-secrete");

        var bytes = File.ReadAllBytes(FilePath);

        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("valeur-tres-secrete")) < 0);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("token")) < 0);
    }

    [Fact]
    public void Vault_SetNull_Removes()
    {
        var vault = new DpapiSecretVault(FilePath);
        vault.Set("network", "token", "abc");
        vault.Set("network", "autre", "def");

        vault.Set("network", "token", null);

        Assert.Null(vault.Get("network", "token"));
        Assert.Equal("def", vault.Get("network", "autre"));
    }

    [Fact]
    public void Vault_TrySet_RefusedByTheGuard_WritesNothing()
    {
        var vault = new DpapiSecretVault(FilePath);
        vault.Set("alex.demo", "token", "avant");
        IReadOnlyDictionary<string, string>? seen = null;

        var accepted = vault.TrySet("alex.demo", "token", "apres", current =>
        {
            seen = new Dictionary<string, string>(current);
            return false;
        });

        Assert.False(accepted);
        Assert.Equal("avant", vault.Get("alex.demo", "token"));
        Assert.Equal(new Dictionary<string, string> { ["token"] = "avant" }, seen);
    }

    [Fact]
    public void Vault_TrySet_AcceptedByTheGuard_Writes_AndSeesAnEmptyScope()
    {
        var vault = new DpapiSecretVault(FilePath);
        var count = -1;

        var accepted = vault.Scope("alex.demo").TrySet("token", "abc", current =>
        {
            count = current.Count;
            return true;
        });

        Assert.True(accepted);
        Assert.Equal(0, count);
        Assert.Equal("abc", vault.Get("alex.demo", "token"));
    }

    [Fact]
    public void Vault_RemoveScope_ErasesOnlyThatScope()
    {
        var vault = new DpapiSecretVault(FilePath);
        vault.Scope("alex.demo").Set("token", "a");
        vault.Scope("alex.demo").Set("autre", "b");
        vault.Scope("network").Set("token", "c");

        vault.RemoveScope("alex.demo");
        vault.RemoveScope("inconnu"); // sans effet

        Assert.Null(vault.Get("alex.demo", "token"));
        Assert.Null(vault.Get("alex.demo", "autre"));
        Assert.Equal("c", vault.Get("network", "token"));
    }

    [Fact]
    public void Vault_Scopes_AreIsolated()
    {
        var vault = new DpapiSecretVault(FilePath);
        vault.Scope("clock").Set("k", "horloge");

        Assert.Null(vault.Scope("network").Get("k"));
        Assert.Equal("horloge", vault.Scope("clock").Get("k"));

        vault.Scope("network").Set("k", "réseau");
        Assert.Equal("horloge", vault.Scope("clock").Get("k"));
        Assert.Equal("réseau", vault.Scope("network").Get("k"));
    }

    [Fact]
    public void Vault_CorruptFile_ActsEmptyAndKeepsBackup()
    {
        Directory.CreateDirectory(_dir);
        var garbage = new byte[256];
        new Random(42).NextBytes(garbage);
        File.WriteAllBytes(FilePath, garbage);
        var vault = new DpapiSecretVault(FilePath);

        Assert.Null(vault.Get("network", "token"));

        Assert.True(File.Exists(FilePath + ".bad"));
        Assert.Equal(garbage, File.ReadAllBytes(FilePath + ".bad"));

        vault.Set("network", "token", "neuf"); // le prochain Set écrit un fichier valide
        Assert.Equal("neuf", new DpapiSecretVault(FilePath).Get("network", "token"));
    }

    [Fact]
    public void Vault_MissingFile_ActsEmpty()
    {
        var vault = new DpapiSecretVault(FilePath);

        Assert.Null(vault.Get("network", "token"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Vault_ReadLocked_ReturnsNullWithoutBackup()
    {
        var vault = new DpapiSecretVault(FilePath);
        vault.Set("network", "token", "abc");

        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(vault.Get("network", "token"));

        Assert.False(File.Exists(FilePath + ".bad"));
        Assert.Equal("abc", vault.Get("network", "token")); // une fois libéré, le secret est intact
    }

    [Theory]
    [InlineData("{\"a\":null}")]
    [InlineData("{\"a\":{\"k\":null}}")]
    public void Vault_NullEntries_AreAbsent(string json)
    {
        Directory.CreateDirectory(_dir);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), Encoding.UTF8.GetBytes("trofeo-HB-dashboard"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, protectedBytes);
        var vault = new DpapiSecretVault(FilePath);

        Assert.Null(vault.Get("a", "k"));
        vault.Set("a", "k", "v"); // un Set par-dessus des entrées nulles fonctionne aussi
        Assert.Equal("v", vault.Get("a", "k"));
    }
}
