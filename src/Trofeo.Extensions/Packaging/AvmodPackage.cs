using System.IO.Compression;
using System.Text;

namespace Trofeo.Extensions;

/// <summary>Ce qu'on sait d'un <c>.avmod</c> sans y avoir écrit quoi que ce soit : son manifeste, ou ce qui l'empêche d'être installé.</summary>
/// <param name="Manifest">Le manifeste, ou <c>null</c> si le paquet est refusé.</param>
/// <param name="Errors">Ce qui ne va pas, en français, à montrer tel quel ; vide si le paquet est accepté.</param>
/// <param name="UncompressedBytes">La taille décompressée annoncée par les en-têtes de l'archive.</param>
public sealed record AvmodInspection(ModuleManifest? Manifest, IReadOnlyList<string> Errors, long UncompressedBytes);

/// <summary>
/// Un paquet <c>.avmod</c> : une archive ZIP avec <c>manifest.json</c> à la racine, la DLL du module, ses
/// dépendances et ses images. Le contenu vient de n'importe qui : l'archive est inspectée en entier avant
/// d'écrire un seul octet, et l'extraction n'utilise jamais <c>ExtractToDirectory</c>.
/// </summary>
public static class AvmodPackage
{
    /// <summary>Taille décompressée maximale du paquet entier (50 Mio).</summary>
    public const long MaxUncompressedBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Taille maximale du fichier <c>.avmod</c> lui-même : celle d'un paquet décompressé, plus une marge d'un Mio pour les en-têtes
    /// de l'archive. Vérifiée avant d'ouvrir l'archive ; c'est aussi la limite du téléchargement d'une mise à jour.
    /// </summary>
    public const long MaxFileBytes = MaxUncompressedBytes + 1024 * 1024;

    /// <summary>Nombre maximal d'entrées (fichiers et dossiers).</summary>
    public const int MaxEntries = 2000;

    /// <summary>
    /// Taille maximale du répertoire central de l'archive : 2 Kio par entrée au plus (un nom de 200 caractères et les champs
    /// facultatifs d'horodatage ou de nom Unicode tiennent largement dans 400 octets). Au-delà, la bibliothèque ZIP lirait une
    /// liste d'entrées démesurée en mémoire.
    /// </summary>
    private const long MaxDirectoryBytes = (long)MaxEntries * 2048;

    /// <summary>Le nom du manifeste, à la racine du paquet.</summary>
    public const string ManifestFileName = "manifest.json";

    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxListedErrors = 10;

    /// <summary>Longueur maximale d'un chemin dans le paquet (le dossier d'installation s'y ajoute, sous les 260 caractères de Windows).</summary>
    public const int MaxPathLength = 200;

    /// <summary>Nombre maximal de niveaux (dossiers et fichier) d'un chemin dans le paquet.</summary>
    public const int MaxPathDepth = 16;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Appelé juste avant le renommage final de <see cref="ExtractTo"/> : sert aux tests à provoquer un échec tardif.</summary>
    internal static Action? AfterExtractionForTests { get; set; }

    /// <summary>
    /// Inspecte un <c>.avmod</c> sans rien écrire sur le disque. Un contenu douteux (archive illisible, chemins
    /// piégés, trop gros, manifeste invalide) ne lève pas d'exception : il revient en erreurs.
    /// </summary>
    /// <exception cref="IOException">Le fichier ne peut pas être ouvert.</exception>
    public static AvmodInspection Inspect(string path)
    {
        using var stream = OpenFile(path);
        if (!TryOpenArchive(stream, out var archive, out var openError))
        {
            return new AvmodInspection(null, [openError], 0);
        }

        using (archive)
        {
            return InspectArchive(archive, out _);
        }
    }

    /// <summary>
    /// Extrait le paquet dans <paramref name="targetDirectory"/>, qui ne doit pas exister. L'archive est d'abord
    /// inspectée (rien n'est écrit si elle est refusée), puis extraite dans <c>&lt;cible&gt;.tmp-&lt;guid&gt;</c>, renommée
    /// d'un bloc ; en cas d'échec le dossier temporaire est supprimé. Les octets réellement décompressés sont comptés :
    /// au-delà de 50 Mio, l'extraction s'arrête, quoi que disent les en-têtes.
    /// </summary>
    /// <exception cref="InvalidDataException">Le paquet est refusé (le message reprend les erreurs de <see cref="Inspect"/>).</exception>
    /// <exception cref="IOException">La cible existe déjà, ou l'écriture échoue.</exception>
    public static void ExtractTo(string path, string targetDirectory)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));

        using var stream = OpenFile(path);
        if (!TryOpenArchive(stream, out var archive, out var openError))
        {
            throw new InvalidDataException(openError);
        }

        using (archive)
        {
            var inspection = InspectArchive(archive, out var items);
            if (inspection.Errors.Count > 0)
            {
                throw new InvalidDataException(string.Join(" ", inspection.Errors));
            }

            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new IOException($"« {target} » existe déjà.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new IOException($"« {target} » n'a pas de dossier parent."));
            var temp = $"{target}.tmp-{Guid.NewGuid():N}";
            try
            {
                Directory.CreateDirectory(temp);
                var prefix = temp + Path.DirectorySeparatorChar;
                long written = 0;
                foreach (var item in items)
                {
                    var destination = Path.GetFullPath(Path.Combine(temp, item.Path));
                    // Les noms sont déjà vérifiés ; ceci garantit encore que rien ne sort du dossier temporaire.
                    if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Le chemin « {Display(item.Path)} » sort du dossier du module.");
                    }

                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var source = item.Entry.Open();
                    using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyLimited(source, file, ref written, MaxUncompressedBytes, TooLargeMessage);
                }

                AfterExtractionForTests?.Invoke();
                Directory.Move(temp, target);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, recursive: true);
                    }
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Le nettoyage est un effort : l'erreur d'origine est celle qui compte.
                }

                throw;
            }
        }
    }

    private static string TooLargeMessage =>
        $"Le paquet est trop gros une fois décompressé ({MaxUncompressedBytes / (1024 * 1024)} Mio au plus).";

    /// <summary>
    /// Copie <paramref name="source"/> vers <paramref name="destination"/> en comptant les octets réellement lus
    /// dans <paramref name="total"/> ; au-delà de <paramref name="max"/>, s'arrête avec <paramref name="overflowMessage"/>.
    /// </summary>
    internal static void CopyLimited(Stream source, Stream destination, ref long total, long max, string overflowMessage)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw new InvalidDataException(overflowMessage);
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static FileStream OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private const string UnreadableMessage = "Ce fichier n'est pas un paquet .avmod valide (archive ZIP illisible).";

    /// <summary>
    /// Ouvre l'archive, après deux vérifications qui ne chargent rien : la taille du fichier, puis le nombre d'entrées et la taille
    /// du répertoire central annoncés par l'enregistrement de fin. <c>ZipArchive</c> et <c>Entries</c> chargent tout le répertoire
    /// central en mémoire : un fichier de quelques centaines de Mio fait d'entrées minuscules ferait des millions d'objets, dans le service.
    /// </summary>
    private static bool TryOpenArchive(Stream stream, out ZipArchive archive, out string error)
    {
        archive = null!;
        if (stream.Length > MaxFileBytes)
        {
            error = $"Le fichier fait {stream.Length / (1024.0 * 1024.0):0.#} Mio : un paquet .avmod ne dépasse pas {MaxFileBytes / (1024 * 1024)} Mio.";
            return false;
        }

        try
        {
            if (ReadDirectoryAnnouncement(stream) is { } announced)
            {
                if (announced.Entries > MaxEntries)
                {
                    error = $"Le paquet contient {announced.Entries} éléments : {MaxEntries} au plus.";
                    return false;
                }

                if (announced.Bytes > MaxDirectoryBytes)
                {
                    error = $"Le répertoire central du paquet est démesuré ({announced.Bytes / (1024 * 1024)} Mio) : {MaxEntries} éléments au plus.";
                    return false;
                }
            }

            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            archive = null!;
            error = UnreadableMessage;
            return false;
        }
    }

    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EndRecordSignature = 0x06064b50;
    private const int EndRecordSize = 22;

    /// <summary>Ce que l'enregistrement de fin d'une archive annonce du répertoire central : le nombre d'entrées et sa taille en octets.</summary>
    internal readonly record struct DirectoryAnnouncement(long Entries, long Bytes);

    /// <summary>
    /// Lit l'enregistrement de fin du répertoire central comme <c>ZipArchive</c> le cherche (la dernière signature dans les 64 Kio
    /// de la fin), et l'enregistrement zip64 qu'il désigne le cas échéant. <c>null</c> si aucun enregistrement de fin n'est trouvé
    /// (<c>ZipArchive</c> refusera le fichier à son tour). Laisse le flux au début.
    /// </summary>
    /// <exception cref="InvalidDataException">L'enregistrement annonce du zip64 mais l'enregistrement zip64 est introuvable ou faux.</exception>
    internal static DirectoryAnnouncement? ReadDirectoryAnnouncement(Stream stream)
    {
        var length = stream.Length;
        if (length < EndRecordSize)
        {
            return null;
        }

        var tail = (int)Math.Min(length, ushort.MaxValue + EndRecordSize);
        var buffer = new byte[tail];
        stream.Seek(length - tail, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        stream.Seek(0, SeekOrigin.Begin);

        ReadOnlySpan<byte> signature = [0x50, 0x4b, 0x05, 0x06];
        var at = -1;
        for (var from = tail - EndRecordSize; from >= 0; from--)
        {
            if (buffer.AsSpan(from, 4).SequenceEqual(signature))
            {
                at = from;
                break;
            }
        }

        if (at < 0)
        {
            return null;
        }

        long entries = BitConverter.ToUInt16(buffer, at + 10);
        long bytes = BitConverter.ToUInt32(buffer, at + 12);
        if (entries != ushort.MaxValue && bytes != uint.MaxValue)
        {
            return new DirectoryAnnouncement(entries, bytes);
        }

        // Zip64 : le localisateur (20 octets) précède l'enregistrement de fin et désigne l'enregistrement zip64 (56 octets).
        var endPosition = length - tail + at;
        if (endPosition < 20 + 56)
        {
            throw new InvalidDataException("zip64 introuvable");
        }

        var locator = new byte[20];
        stream.Seek(endPosition - 20, SeekOrigin.Begin);
        stream.ReadExactly(locator);
        var offset = BitConverter.ToInt64(locator, 8);
        if (BitConverter.ToUInt32(locator, 0) != Zip64LocatorSignature || offset < 0 || offset > endPosition - 20 - 56)
        {
            stream.Seek(0, SeekOrigin.Begin);
            throw new InvalidDataException("zip64 introuvable");
        }

        var record = new byte[56];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(record);
        stream.Seek(0, SeekOrigin.Begin);
        if (BitConverter.ToUInt32(record, 0) != Zip64EndRecordSignature)
        {
            throw new InvalidDataException("zip64 introuvable");
        }

        var entries64 = BitConverter.ToUInt64(record, 32);
        var bytes64 = BitConverter.ToUInt64(record, 40);
        return new DirectoryAnnouncement(
            entries64 > long.MaxValue ? long.MaxValue : (long)entries64, bytes64 > long.MaxValue ? long.MaxValue : (long)bytes64);
    }

    private sealed record Item(ZipArchiveEntry Entry, string Path, bool IsDirectory);

    /// <summary>Tout ce qui se vérifie sans écrire : nombre d'entrées, taille annoncée, chemins, manifeste.</summary>
    private static AvmodInspection InspectArchive(ZipArchive archive, out List<Item> items)
    {
        items = [];
        var errors = new List<string>();
        var entries = archive.Entries;

        long declared = 0;
        foreach (var entry in entries)
        {
            declared += entry.Length;
        }

        if (entries.Count > MaxEntries)
        {
            errors.Add($"Le paquet contient {entries.Count} éléments : {MaxEntries} au plus.");
        }

        if (declared > MaxUncompressedBytes)
        {
            errors.Add($"Le paquet fait {declared / (1024.0 * 1024.0):0.#} Mio une fois décompressé : {MaxUncompressedBytes / (1024 * 1024)} Mio au plus.");
        }

        if (errors.Count > 0)
        {
            return new AvmodInspection(null, errors, declared);
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = 0;
        foreach (var entry in entries)
        {
            if (!TryNormalize(entry.FullName, out var normalized, out var isDirectory, out var problem))
            {
                AddProblem(errors, ref problems, $"Le paquet contient un chemin refusé « {Display(entry.FullName)} » ({problem}).");
                continue;
            }

            if (isDirectory && entry.Length > 0)
            {
                AddProblem(errors, ref problems, $"Le dossier « {Display(normalized)} » du paquet contient des données.");
                continue;
            }

            // Les dossiers parents existent, explicitement ou non : un fichier ne peut pas porter leur nom.
            for (var slash = normalized.IndexOf('/'); slash >= 0; slash = normalized.IndexOf('/', slash + 1))
            {
                folders.Add(normalized[..slash]);
            }

            if (isDirectory)
            {
                folders.Add(normalized);
            }
            else if (files.TryGetValue(normalized, out var first))
            {
                AddProblem(errors, ref problems, $"Deux fichiers du paquet ont le même nom sous Windows : « {Display(first)} » et « {Display(normalized)} ».");
            }
            else
            {
                files[normalized] = normalized;
            }

            items.Add(new Item(entry, normalized, isDirectory));
        }

        foreach (var file in files.Keys)
        {
            if (folders.Contains(file))
            {
                AddProblem(errors, ref problems, $"« {Display(file)} » est à la fois un fichier et un dossier dans le paquet.");
            }
        }

        if (problems > MaxListedErrors)
        {
            errors.Add($"… et {problems - MaxListedErrors} autre(s) problème(s).");
        }

        if (errors.Count > 0)
        {
            return new AvmodInspection(null, errors, declared);
        }

        var manifestItem = items.FirstOrDefault(item => !item.IsDirectory && item.Path == ManifestFileName);
        if (manifestItem is null)
        {
            return new AvmodInspection(null, [$"Le paquet ne contient pas de {ManifestFileName} à la racine."], declared);
        }

        string json;
        try
        {
            json = ReadManifestText(manifestItem.Entry);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or DecoderFallbackException)
        {
            return new AvmodInspection(null, [$"Le {ManifestFileName} du paquet est illisible (UTF-8 attendu)."], declared);
        }

        var read = ManifestReader.Read(json);
        if (read.Manifest is not { } manifest)
        {
            return new AvmodInspection(null, read.Errors, declared);
        }

        if (!items.Any(item => !item.IsDirectory && string.Equals(item.Path, manifest.Entry, StringComparison.OrdinalIgnoreCase)))
        {
            return new AvmodInspection(null, [$"Le paquet ne contient pas la DLL du module (« {manifest.Entry} » à la racine)."], declared);
        }

        return new AvmodInspection(manifest, [], declared);
    }

    private static void AddProblem(List<string> errors, ref int problems, string message)
    {
        problems++;
        if (problems <= MaxListedErrors)
        {
            errors.Add(message);
        }
    }

    private static string ReadManifestText(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        long total = 0;
        CopyLimited(source, buffer, ref total, MaxManifestBytes, $"Le {ManifestFileName} du paquet est trop gros ({MaxManifestBytes / 1024} Kio au plus).");

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }

    /// <summary>
    /// Met un nom d'entrée sous la forme <c>dossier/fichier</c> (sans <c>/</c> final pour un dossier) et refuse ce
    /// qui sortirait du dossier du module ou ne s'écrirait pas tel quel sous Windows.
    /// </summary>
    private static bool TryNormalize(string raw, out string normalized, out bool isDirectory, out string problem)
    {
        normalized = "";
        isDirectory = false;
        problem = "";

        // Les bornes d'abord, avant tout travail sur le nom : un nom de ZIP peut faire 65 535 octets.
        if (raw.Length > MaxPathLength + 1)
        {
            problem = $"chemin de plus de {MaxPathLength} caractères";
            return false;
        }

        var name = raw.Replace('\\', '/');
        if (name.Length == 0)
        {
            problem = "nom vide";
            return false;
        }

        if (name.StartsWith('/'))
        {
            problem = "chemin absolu";
            return false;
        }

        if (name.Contains(':'))
        {
            problem = "« : » interdit (lecteur ou flux alternatif)";
            return false;
        }

        isDirectory = name.EndsWith('/');
        var body = isDirectory ? name[..^1] : name;
        if (body.Length > MaxPathLength)
        {
            problem = $"chemin de plus de {MaxPathLength} caractères";
            return false;
        }

        if (body.Count(c => c == '/') >= MaxPathDepth)
        {
            problem = $"chemin de plus de {MaxPathDepth} niveaux";
            return false;
        }

        foreach (var segment in body.Split('/'))
        {
            if (segment.Length == 0)
            {
                problem = "dossier vide dans le chemin";
                return false;
            }

            if (segment is "." or "..")
            {
                problem = "« . » ou « .. » dans le chemin";
                return false;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                problem = "nom qui finit par un point ou une espace";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Any(c => c < ' ' || "<>\"|?*".Contains(c)))
            {
                problem = "caractère interdit dans un nom de fichier";
                return false;
            }

            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem))
            {
                problem = "nom réservé par Windows";
                return false;
            }
        }

        normalized = body;
        return true;
    }

    /// <summary>Un nom venu d'un paquet, rendu montrable : sans caractère de contrôle, et pas trop long.</summary>
    private static string Display(string text)
    {
        var shown = text.Length > 80 ? text[..80] : text;
        var cleaned = new string(shown.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return text.Length > 80 ? cleaned + "…" : cleaned;
    }
}
