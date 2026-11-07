using Trofeo.Core.Secrets;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;
using Trofeo.Shared;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Un module ne doit pas pouvoir faire grossir sans limite le coffre à secrets (un seul fichier, partagé et réécrit en entier à chaque
/// écriture) ni la mémoire du service (file d'envoi vers un hôte qui ne lit plus).
/// </summary>
[Collection("Hosts")]
public sealed class SecretQuotaTests : IAsyncLifetime
{
    private readonly ModuleSandbox _folders = new();
    private readonly List<SupervisorRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        _folders.Dispose();
    }

    private async Task<(SupervisorRig Rig, FakeHostProcess Host, QuotaSecrets Store)> StartAsync()
    {
        var folder = _folders.SmallModule();
        var factory = new FakeHostFactory();
        var store = new QuotaSecrets();
        var rig = new SupervisorRig(ModuleSandbox.Ready(folder, ModuleSandbox.Manifest(entry: "module.dll")), factory, secrets: store);
        _rigs.Add(rig);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();
        return (rig, host, store);
    }

    // --- Le plafond, en fonction pure ------------------------------------------------------------------------------------------------

    [Fact]
    public void Fits_NewKeyUnderTheCaps_IsAccepted()
    {
        Assert.True(SecretLimits.Fits(new Dictionary<string, string>(), "jeton", "abc"));
    }

    [Fact]
    public void Fits_Deleting_IsAlwaysAccepted()
    {
        var full = Enumerable.Range(0, SecretLimits.MaxKeys).ToDictionary(i => $"k{i}", _ => "v");

        Assert.True(SecretLimits.Fits(full, "k1", null));
    }

    [Fact]
    public void Fits_SixtyFifthKey_IsRefused()
    {
        var full = Enumerable.Range(0, SecretLimits.MaxKeys).ToDictionary(i => $"k{i}", _ => "v");

        Assert.False(SecretLimits.Fits(full, "nouvelle", "v"));
    }

    [Fact]
    public void Fits_SameSizeReplacementAtTheKeyCap_IsAccepted()
    {
        var full = Enumerable.Range(0, SecretLimits.MaxKeys).ToDictionary(i => $"k{i}", _ => "v");

        Assert.True(SecretLimits.Fits(full, "k3", "w"));
    }

    [Fact]
    public void Fits_TotalSizeIsCountedInUtf8BytesOfKeysAndValues()
    {
        // 255 Kio déjà pris, il en reste 1 Kio : « é » compte deux octets.
        var current = new Dictionary<string, string> { ["gros"] = new string('a', SecretLimits.MaxTotalBytes - 1024 - 4) };

        Assert.True(SecretLimits.Fits(current, "k", new string('a', 1023)));
        Assert.False(SecretLimits.Fits(current, "k", new string('é', 600)));
    }

    [Fact]
    public void Fits_TotalCapReached_RefusesGrowthButAllowsSameOrSmallerReplacement()
    {
        var current = new Dictionary<string, string> { ["a"] = new string('x', SecretLimits.MaxTotalBytes - 1 - 1) }; // 1 + (cap - 2) = cap - 1
        current["b"] = "y"; // total = cap exactly

        Assert.False(SecretLimits.Fits(current, "c", "z")); // nouvelle clé : refusée
        Assert.False(SecretLimits.Fits(current, "b", "yy")); // plus long : refusé
        Assert.True(SecretLimits.Fits(current, "b", "z")); // même taille : permis
        Assert.True(SecretLimits.Fits(current, "a", new string('x', 10))); // plus court : permis
    }

    /// <summary>Une écriture à la fois, attendue : sinon la file bornée de l'hôte peut écarter des demandes et fausser ce qu'on mesure.</summary>
    private static async Task SetAsync(FakeHostProcess host, int id, string key, string? value)
    {
        await host.SendAsync(new SecretSet(id, key, value));
        await host.WaitAsync<SecretValue>(reply => reply.Id == id);
    }

    // --- Dans le superviseur ---------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task SecretSet_SixtyFifthKey_IsRefusedButAnswered()
    {
        var (_, host, store) = await StartAsync();

        for (var i = 1; i <= SecretLimits.MaxKeys + 1; i++)
        {
            await SetAsync(host, i, $"cle{i}", "v");
        }

        Assert.Equal(SecretLimits.MaxKeys, store.Count);
        Assert.Null(store.Get($"cle{SecretLimits.MaxKeys + 1}"));
        Assert.False(host.HasExited);
    }

    [WindowsHostFact]
    public async Task SecretSet_TotalOverTheCap_IsRefused()
    {
        var (_, host, store) = await StartAsync();
        var biggest = new string('a', SecretLimits.MaxValueLength);

        for (var i = 1; i <= 5; i++)
        {
            await SetAsync(host, i, $"g{i}", biggest);
        }

        Assert.Equal(3, store.Count); // 3 × 64 Kio + les clés tiennent dans 256 Kio ; la quatrième dépasse
        Assert.Null(store.Get("g4"));
        Assert.Null(store.Get("g5"));
    }

    [WindowsHostFact]
    public async Task SecretSet_ReplacingAtTheKeyCap_IsAllowed()
    {
        var (_, host, store) = await StartAsync();
        for (var i = 1; i <= SecretLimits.MaxKeys; i++)
        {
            await SetAsync(host, i, $"cle{i}", "v");
        }

        await SetAsync(host, 100, "cle3", "w");
        await SetAsync(host, 101, "cle4", null); // effacer libère une place
        await SetAsync(host, 102, "nouvelle", "n");

        Assert.Equal("w", store.Get("cle3"));
        Assert.Null(store.Get("cle4"));
        Assert.Equal("n", store.Get("nouvelle"));
        Assert.Equal(SecretLimits.MaxKeys, store.Count);
    }

    // --- La file d'envoi vers l'hôte ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Post_PendingBytesOverTheCap_StopsTheHost()
    {
        var process = new KillCounter();
        var connection = new HostConnection(process, null, new MessageChannel(Stream.Null, Stream.Null), DateTimeOffset.UtcNow);
        var big = new string('a', SecretLimits.MaxValueLength);
        var weight = HostConnection.WeightOf(new SecretValue(1, big));
        var fit = (int)(HostConnection.MaxQueuedBytes / weight);

        for (var i = 0; i < fit; i++)
        {
            connection.Post(new SecretValue(i, big));
        }

        Assert.False(connection.Overflowed);
        Assert.Equal(0, process.Kills);

        connection.Post(new SecretValue(fit, big));

        Assert.True(connection.Overflowed);
        Assert.Equal(1, process.Kills);
        Assert.Equal(32L * 1024 * 1024, HostConnection.MaxQueuedBytes);
    }

    [Fact]
    public void Post_SmallMessages_AreNotCountedAsBytes()
    {
        var process = new KillCounter();
        var connection = new HostConnection(process, null, new MessageChannel(Stream.Null, Stream.Null), DateTimeOffset.UtcNow);

        for (var i = 0; i < 1000; i++)
        {
            connection.Post(new Limits(10));
        }

        Assert.False(connection.Overflowed);
    }

    [Fact]
    public async Task WriteLoop_DrainedMessages_FreeTheirBytes()
    {
        var process = new KillCounter();
        var connection = new HostConnection(process, null, new MessageChannel(Stream.Null, Stream.Null), DateTimeOffset.UtcNow);
        var big = new string('a', SecretLimits.MaxValueLength);
        var writer = connection.WriteLoopAsync();

        for (var i = 0; i < 300; i++) // 37 Mio en tout, mais la file se vide à mesure
        {
            connection.Post(new SecretValue(i, big));
            await TestWait.UntilAsync(() => connection.PendingBytes == 0, null, () => $"{connection.PendingBytes} octets en attente");
        }

        connection.Close();
        await writer;
        Assert.False(connection.Overflowed);
    }

    private sealed class KillCounter : IHostProcess
    {
        private int _kills;

        public int Kills => Volatile.Read(ref _kills);

        public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;

        public string DataDirectory => Path.GetTempPath();

        public long DuplicateInto(System.Runtime.InteropServices.SafeHandle source) => throw new NotSupportedException();

        public void Kill() => Interlocked.Increment(ref _kills);

        public JobUsage? ReadUsage() => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Un coffre en mémoire qui applique les plafonds du service, comme le coffre réel et le faux des créateurs.</summary>
    private sealed class QuotaSecrets : IGuardedSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public int Count
        {
            get
            {
                lock (_values)
                {
                    return _values.Count;
                }
            }
        }

        public string? Get(string key)
        {
            lock (_values)
            {
                return _values.GetValueOrDefault(key);
            }
        }

        public void Set(string key, string? value) => TrySet(key, value, _ => true);

        public bool TrySet(string key, string? value, Func<IReadOnlyDictionary<string, string>, bool> accept)
        {
            lock (_values)
            {
                if (!accept(_values))
                {
                    return false;
                }

                if (value is null)
                {
                    _values.Remove(key);
                }
                else
                {
                    _values[key] = value;
                }

                return true;
            }
        }
    }
}
