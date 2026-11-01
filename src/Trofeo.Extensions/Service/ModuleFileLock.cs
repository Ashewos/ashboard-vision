namespace Trofeo.Extensions.Service;

/// <summary>
/// Le verrou d'exécution d'un module installé : chaque fichier de son dossier est ouvert en lecture avec
/// <see cref="FileShare.Read"/> (personne ne peut ni les écrire, ni les supprimer, ni les renommer, ni déplacer le dossier),
/// l'empreinte (<see cref="ContentHash"/>) est calculée sur ces fichiers <b>déjà ouverts</b>, et ils restent ouverts jusqu'à
/// <see cref="Dispose"/>. L'empreinte est donc celle de ce que l'hôte va charger, sans fenêtre entre la vérification et le
/// lancement. Le superviseur le garde jusqu'à la fin de l'hôte.
/// </summary>
public sealed class ModuleFileLock : IDisposable
{
    // Fichiers cachés et système compris, comme ContentHash : un fichier caché ne doit pas échapper à l'empreinte.
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    private readonly List<FileStream> _files;

    private ModuleFileLock(string contentHash, List<FileStream> files)
    {
        ContentHash = contentHash;
        _files = files;
    }

    /// <summary>L'empreinte du contenu du dossier, calculée sur les fichiers ouverts (même format que <see cref="Trofeo.Extensions.ContentHash"/>).</summary>
    public string ContentHash { get; }

    /// <summary>
    /// Ouvre tous les fichiers du dossier (sous-dossiers compris) et calcule l'empreinte. Lève <see cref="IOException"/> ou
    /// <see cref="UnauthorizedAccessException"/> si un fichier ne s'ouvre pas (verrou d'un antivirus, de OneDrive…), et
    /// <see cref="ArgumentException"/> si deux fichiers ne diffèrent que par la casse ; rien ne reste ouvert dans ces cas.
    /// </summary>
    public static ModuleFileLock Acquire(string directory)
    {
        var root = Path.GetFullPath(directory);
        var files = new List<FileStream>();
        try
        {
            var entries = new List<(string RelativePath, Stream Content)>();
            foreach (var file in Directory.EnumerateFiles(root, "*", AllFiles))
            {
                var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(stream);
                entries.Add((Path.GetRelativePath(root, file), stream));
            }

            return new ModuleFileLock(Trofeo.Extensions.ContentHash.Compute(entries), files);
        }
        catch
        {
            foreach (var stream in files)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    /// <summary>Libère les fichiers.</summary>
    public void Dispose()
    {
        foreach (var stream in _files)
        {
            stream.Dispose();
        }

        _files.Clear();
    }
}
