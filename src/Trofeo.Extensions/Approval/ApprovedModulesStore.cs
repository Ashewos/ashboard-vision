using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AshboardVision.Sdk;

namespace Trofeo.Extensions;

/// <summary>Ce que <see cref="ApprovedModulesStore.Load"/> a lu, et si la liste a été trouvée altérée ou illisible.</summary>
/// <param name="Modules">La liste lue ; <see cref="ApprovedModules.Empty"/> si le fichier est absent, altéré ou illisible.</param>
/// <param name="Tampered"><c>true</c> si le fichier existe mais que son sceau ne tient pas (sceau faux ou absent, JSON ou
/// contenu invalide, clé absente ou indéchiffrable) : rien n'est alors considéré comme validé.</param>
/// <param name="Unreadable"><c>true</c> si le fichier ou sa clé n'a pas pu être lu pour une raison d'entrée-sortie
/// (verrou d'un antivirus, de OneDrive, d'un éditeur…), même après quelques essais. Ce n'est pas une altération : la liste
/// est vide pour l'instant, mais l'appelant ne doit surtout pas l'écraser par une sauvegarde.</param>
public sealed record ApprovedLoad(ApprovedModules Modules, bool Tampered, bool Unreadable = false);

/// <summary>
/// La liste scellée des modules validés (<c>modules.json</c>). Le fichier est <c>{ "data": {…}, "mac": "…" }</c> :
/// le sceau est un HMAC-SHA256 calculé sur les octets UTF-8 exacts du texte de <c>data</c>, avec une clé aléatoire de
/// 32 octets rangée dans <c>modules.key</c> (chiffrée par DPAPI, compte Windows courant). Toute modification à la main,
/// même un simple changement d'ordre ou d'espaces, ou un remplacement de la clé, fait tomber le sceau : la liste est
/// alors vide et marquée altérée. Un échec d'entrée-sortie (fichier verrouillé) n'est pas une altération : il est signalé
/// à part (<see cref="ApprovedLoad.Unreadable"/>). Écritures atomiques (fichier temporaire, puis remplacement).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApprovedModulesStore
{
    internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ashboard-vision.modules");
    private const int KeyLength = 32;
    private const int ReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions DataOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly string _keyPath;
    private readonly Action<string> _warn;

    /// <param name="filePath">Le fichier de la liste (<c>modules.json</c>).</param>
    /// <param name="keyPath">Le fichier de la clé (<c>modules.key</c>).</param>
    /// <param name="warn">Reçoit les avertissements (liste altérée ou illisible) ; par défaut, le journal (<see cref="Trace"/>).</param>
    public ApprovedModulesStore(string filePath, string keyPath, Action<string>? warn = null)
    {
        _filePath = filePath;
        _keyPath = keyPath;
        _warn = warn ?? (message => Trace.TraceWarning(message));
    }

    /// <summary>
    /// Lit la liste. Fichier absent : liste vide, non altérée. Sceau faux, JSON ou contenu invalide, clé absente ou
    /// indéchiffrable : liste vide, altérée. Échec d'entrée-sortie persistant (fichier ou clé) : liste vide, illisible
    /// (<see cref="ApprovedLoad.Unreadable"/>), pas altérée.
    /// </summary>
    public ApprovedLoad Load()
    {
        var file = ReadFileWithRetry(_filePath);
        if (file.Missing) return new ApprovedLoad(ApprovedModules.Empty, Tampered: false);
        if (file.IoError is { } fileError) return Unreadable(_filePath, fileError);

        var key = ReadKey();
        if (key.IoError is { } keyError) return Unreadable(_keyPath, keyError);

        try
        {
            if (key.Key is null) throw new CryptographicException("La clé du sceau est absente ou indéchiffrable.");
            return new ApprovedLoad(ReadVerified(file.Bytes!, key.Key), Tampered: false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or InvalidOperationException)
        {
            _warn($"La liste des modules validés ({_filePath}) a été modifiée ou est illisible : aucun module complémentaire n'est considéré comme validé ({ex.Message})");
            return new ApprovedLoad(ApprovedModules.Empty, Tampered: true);
        }
    }

    /// <summary>
    /// Écrit la liste scellée. Une nouvelle clé n'est créée que si le fichier de clé est absent ou indéchiffrable (la
    /// liste était alors déjà altérée). Si la clé existe mais n'a pas pu être lue (entrée-sortie), lève
    /// <see cref="IOException"/> au lieu de la remplacer. Le fichier temporaire de la liste est écrit avant tout
    /// remplacement, puis la clé (si elle est nouvelle) est déplacée, puis la liste.
    /// </summary>
    public void Save(ApprovedModules modules)
    {
        var existing = ReadKey();
        if (existing.IoError is { } error)
        {
            throw new IOException($"La clé des modules validés ({_keyPath}) n'a pas pu être lue : la liste n'est pas enregistrée.", error);
        }

        var key = existing.Key ?? RandomNumberGenerator.GetBytes(KeyLength);
        var data = JsonSerializer.SerializeToUtf8Bytes(ToDto(modules), DataOptions);
        var mac = Convert.ToBase64String(HMACSHA256.HashData(key, data));

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.UTF8.GetBytes("{\n  \"data\": "));
        buffer.Write(data);
        buffer.Write(Encoding.UTF8.GetBytes($",\n  \"mac\": \"{mac}\"\n}}\n"));

        EnsureDirectory(_filePath);
        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, buffer.ToArray());
        if (existing.Key is null)
        {
            WriteAtomically(_keyPath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        }

        File.Move(tmp, _filePath, overwrite: true);
    }

    private ApprovedLoad Unreadable(string path, Exception error)
    {
        _warn($"Le fichier {path} n'a pas pu être lu pour l'instant ({error.Message}) : la liste des modules validés est ignorée sans être modifiée.");
        return new ApprovedLoad(ApprovedModules.Empty, Tampered: false, Unreadable: true);
    }

    /// <summary>Lit un fichier en réessayant après un échec d'entrée-sortie ; un fichier absent est signalé, ce n'est pas une erreur.</summary>
    private static FileRead ReadFileWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileRead(File.ReadAllBytes(path), Missing: false, IoError: null);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new FileRead(null, Missing: true, IoError: null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts) return new FileRead(null, Missing: false, IoError: ex);
                Thread.Sleep(RetryDelay);
            }
        }
    }

    /// <summary>
    /// La clé du sceau. <see cref="KeyRead.Key"/> est <c>null</c> si le fichier est absent, indéchiffrable ou de mauvaise
    /// longueur ; <see cref="KeyRead.IoError"/> est renseigné si le fichier existe mais n'a pas pu être lu.
    /// </summary>
    private KeyRead ReadKey()
    {
        var file = ReadFileWithRetry(_keyPath);
        if (file.IoError is not null) return new KeyRead(null, file.IoError);
        if (file.Bytes is null) return new KeyRead(null, null);

        try
        {
            var key = ProtectedData.Unprotect(file.Bytes, Entropy, DataProtectionScope.CurrentUser);
            return new KeyRead(key.Length == KeyLength ? key : null, null);
        }
        catch (CryptographicException)
        {
            return new KeyRead(null, null);
        }
    }

    private sealed record FileRead(byte[]? Bytes, bool Missing, Exception? IoError);

    private sealed record KeyRead(byte[]? Key, Exception? IoError);

    private static ApprovedModules ReadVerified(byte[] file, byte[] key)
    {
        using var document = JsonDocument.Parse(file);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("mac", out var macElement) || macElement.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("Le fichier n'a pas la forme { data, mac }.");
        }

        // Le sceau couvre le texte exact de « data » tel qu'il est dans le fichier : un changement d'ordre ou d'espaces
        // le fait tomber, jamais valider.
        var dataBytes = Encoding.UTF8.GetBytes(data.GetRawText());
        var expected = HMACSHA256.HashData(key, dataBytes);
        var actual = Convert.FromBase64String(macElement.GetString()!);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new CryptographicException("Le sceau ne correspond pas.");
        }

        var dto = JsonSerializer.Deserialize<StoreDto>(dataBytes, DataOptions)
                  ?? throw new FormatException("Le contenu est vide.");
        return FromDto(dto);
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        EnsureDirectory(path);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static StoreDto ToDto(ApprovedModules modules) => new()
    {
        ModulesRoot = modules.ModulesRoot,
        DeveloperMode = modules.DeveloperMode,
        DevFolders = [.. modules.DevFolders.Select(f => new DevFolderDto { Path = f.Path, Granted = ToNames(f.Granted) })],
        Modules = [.. modules.Modules.Select(m => new ModuleDto
        {
            Id = m.Id,
            Version = m.Version,
            Directory = m.Directory,
            ContentHash = m.ContentHash,
            Granted = ToNames(m.Granted),
            SetAsideReason = m.SetAsideReason,
            Repository = m.Repository,
        })],
    };

    private static ApprovedModules FromDto(StoreDto dto) => new(
        dto.ModulesRoot,
        dto.DeveloperMode,
        [.. (dto.DevFolders ?? []).Select(f => new DevFolder(Required(f.Path), ToPermissions(f.Granted)))],
        [.. (dto.Modules ?? []).Select(m => new ApprovedModule(
            Required(m.Id), Required(m.Version), Required(m.Directory), Required(m.ContentHash),
            ToPermissions(m.Granted), m.SetAsideReason, m.Repository))]);

    private static string Required(string? value) => value ?? throw new FormatException("Un champ obligatoire manque.");

    private static List<string> ToNames(IReadOnlyList<ModulePermission> permissions) =>
        [.. permissions.Select(ModulePermissions.ToManifest)];

    private static IReadOnlyList<ModulePermission> ToPermissions(List<string>? names)
    {
        var permissions = new List<ModulePermission>();
        foreach (var name in names ?? [])
        {
            if (!ModulePermissions.TryParse(name, out var permission))
            {
                throw new FormatException($"Permission inconnue : « {name} ».");
            }

            permissions.Add(permission);
        }

        return permissions;
    }

    private sealed class StoreDto
    {
        public string? ModulesRoot { get; set; }

        public bool DeveloperMode { get; set; }

        public List<DevFolderDto>? DevFolders { get; set; }

        public List<ModuleDto>? Modules { get; set; }
    }

    private sealed class DevFolderDto
    {
        public string? Path { get; set; }

        public List<string>? Granted { get; set; }
    }

    private sealed class ModuleDto
    {
        public string? Id { get; set; }

        public string? Version { get; set; }

        public string? Directory { get; set; }

        public string? ContentHash { get; set; }

        public List<string>? Granted { get; set; }

        public string? SetAsideReason { get; set; }

        public string? Repository { get; set; }
    }
}
