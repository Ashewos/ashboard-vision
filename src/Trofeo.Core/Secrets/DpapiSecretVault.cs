using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Secrets;

/// <summary>
/// Coffre à secrets d'un fichier unique, chiffré par DPAPI (compte Windows courant). Contenu JSON
/// <c>{ portée: { clé: valeur } }</c>, une portée par type de module. Le fichier est relu à chaque lecture et
/// réécrit atomiquement ; les écritures se font sous un mutex nommé, car le logiciel de réglage écrit aussi.
/// Un fichier illisible est gardé en <c>.bad</c> puis traité comme vide ; un fichier momentanément verrouillé
/// est traité comme vide à la lecture, sans copie. Les entrées nulles sont des entrées absentes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretVault
{
    private const string MutexName = @"Local\trofeo-HB-dashboard.secrets";
    internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("trofeo-HB-dashboard");
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly string _filePath;

    public DpapiSecretVault(string filePath) => _filePath = filePath;

    /// <summary>
    /// La valeur de la clé dans la portée, ou <c>null</c> si absente (ou fichier absent, illisible ou verrouillé).
    /// Ne lève rien pour un problème de fichier.
    /// </summary>
    public string? Get(string scope, string key)
    {
        using var _ = Lock();
        var data = Read(failOnLockedFile: false);
        return data.TryGetValue(scope, out var keys) && keys.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Enregistre la valeur ; <c>null</c> efface la clé. Lève si le fichier est verrouillé ou ne peut pas être écrit :
    /// partir d'un coffre vu comme vide écraserait les autres secrets.
    /// </summary>
    public void Set(string scope, string key, string? value)
    {
        using var _ = Lock();
        var data = Read(failOnLockedFile: true);
        if (value is null)
        {
            if (!data.TryGetValue(scope, out var existing) || !existing.Remove(key)) return;
            if (existing.Count == 0) data.Remove(scope);
        }
        else
        {
            if (!data.TryGetValue(scope, out var keys)) data[scope] = keys = [];
            keys[key] = value;
        }

        Write(data);
    }

    /// <summary>Une vue du coffre limitée à une portée (un type de module).</summary>
    public ISecretStore Scope(string scope) => new ScopedStore(this, scope);

    private Dictionary<string, Dictionary<string, string>> Read(bool failOnLockedFile)
    {
        if (!File.Exists(_filePath)) return [];

        try
        {
            var protectedBytes = File.ReadAllBytes(_filePath);
            var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return DropNulls(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>?>>(json));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or NotSupportedException)
        {
            KeepBackup();
            return [];
        }
        catch (Exception ex) when (!failOnLockedFile && ex is IOException or UnauthorizedAccessException)
        {
            return []; // verrouillé ou inaccessible pour l'instant : pas une corruption, donc pas de copie .bad
        }
    }

    /// <summary>Un JSON valide peut contenir des <c>null</c> : portée ou valeur nulle = absente.</summary>
    private static Dictionary<string, Dictionary<string, string>> DropNulls(Dictionary<string, Dictionary<string, string?>?>? raw)
    {
        var data = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (scope, keys) in raw ?? [])
        {
            if (keys is null) continue;
            var kept = new Dictionary<string, string>();
            foreach (var (key, value) in keys)
            {
                if (value is not null) kept[key] = value;
            }

            if (kept.Count > 0) data[scope] = kept;
        }

        return data;
    }

    private void KeepBackup()
    {
        try
        {
            File.Copy(_filePath, _filePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // La copie est un confort : le coffre se comporte quand même comme vide.
        }
    }

    private void Write(Dictionary<string, Dictionary<string, string>> data)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, _filePath, overwrite: true);
    }

    private static MutexLease Lock()
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            try
            {
                if (!mutex.WaitOne(LockTimeout))
                {
                    throw new TimeoutException("Le coffre à secrets est verrouillé par un autre processus.");
                }
            }
            catch (AbandonedMutexException)
            {
                // Le précédent propriétaire est mort : le mutex nous appartient quand même.
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new MutexLease(mutex);
    }

    private readonly struct MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private sealed class ScopedStore(DpapiSecretVault vault, string scope) : ISecretStore
    {
        public string? Get(string key) => vault.Get(scope, key);

        public void Set(string key, string? value) => vault.Set(scope, key, value);
    }
}
