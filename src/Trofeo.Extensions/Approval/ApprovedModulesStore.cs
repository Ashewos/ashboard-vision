using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AshboardVision.Sdk;

namespace Trofeo.Extensions;

/// <summary>Ce que <see cref="ApprovedModulesStore.Load"/> a lu, et si la liste a été trouvée altérée.</summary>
/// <param name="Modules">La liste lue ; <see cref="ApprovedModules.Empty"/> si le fichier est absent ou altéré.</param>
/// <param name="Tampered"><c>true</c> si le fichier existe mais que son sceau ne tient pas (ou qu'il est illisible) :
/// rien n'est alors considéré comme validé.</param>
public sealed record ApprovedLoad(ApprovedModules Modules, bool Tampered);

/// <summary>
/// La liste scellée des modules validés (<c>modules.json</c>). Le fichier est <c>{ "data": {…}, "mac": "…" }</c> :
/// le sceau est un HMAC-SHA256 calculé sur les octets UTF-8 exacts du texte de <c>data</c>, avec une clé aléatoire de
/// 32 octets rangée dans <c>modules.key</c> (chiffrée par DPAPI, compte Windows courant). Toute modification à la main,
/// même un simple changement d'ordre ou d'espaces, ou un remplacement de la clé, fait tomber le sceau : la liste est
/// alors vide et marquée altérée. Écritures atomiques (fichier temporaire, puis remplacement).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApprovedModulesStore
{
    internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ashboard-vision.modules");
    private const int KeyLength = 32;

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
    /// <param name="warn">Reçoit l'avertissement quand la liste est altérée ; par défaut, le journal (<see cref="Trace"/>).</param>
    public ApprovedModulesStore(string filePath, string keyPath, Action<string>? warn = null)
    {
        _filePath = filePath;
        _keyPath = keyPath;
        _warn = warn ?? (message => Trace.TraceWarning(message));
    }

    /// <summary>Lit la liste. Fichier absent : liste vide, non altérée. Sceau faux, clé ou fichier illisible : liste vide, altérée.</summary>
    public ApprovedLoad Load()
    {
        if (!File.Exists(_filePath)) return new ApprovedLoad(ApprovedModules.Empty, Tampered: false);

        try
        {
            var key = ReadKey() ?? throw new CryptographicException("La clé du sceau est absente ou illisible.");
            var modules = ReadVerified(File.ReadAllBytes(_filePath), key);
            return new ApprovedLoad(modules, Tampered: false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException)
        {
            _warn($"La liste des modules validés ({_filePath}) a été modifiée ou est illisible : aucun module complémentaire n'est considéré comme validé ({ex.Message})");
            return new ApprovedLoad(ApprovedModules.Empty, Tampered: true);
        }
    }

    /// <summary>
    /// Écrit la liste scellée. Si la clé est absente ou illisible, une nouvelle est créée : une clé qu'on n'a pas su lire
    /// n'est jamais réutilisée.
    /// </summary>
    public void Save(ApprovedModules modules)
    {
        var key = ReadKey();
        if (key is null)
        {
            key = RandomNumberGenerator.GetBytes(KeyLength);
            WriteAtomically(_keyPath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        }

        var data = JsonSerializer.SerializeToUtf8Bytes(ToDto(modules), DataOptions);
        var mac = Convert.ToBase64String(HMACSHA256.HashData(key, data));

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.UTF8.GetBytes("{\n  \"data\": "));
        buffer.Write(data);
        buffer.Write(Encoding.UTF8.GetBytes($",\n  \"mac\": \"{mac}\"\n}}\n"));
        WriteAtomically(_filePath, buffer.ToArray());
    }

    /// <summary>La clé du sceau, ou <c>null</c> si le fichier est absent, illisible ou de mauvaise longueur.</summary>
    private byte[]? ReadKey()
    {
        try
        {
            if (!File.Exists(_keyPath)) return null;
            var key = ProtectedData.Unprotect(File.ReadAllBytes(_keyPath), Entropy, DataProtectionScope.CurrentUser);
            return key.Length == KeyLength ? key : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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

    private static void WriteAtomically(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

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
