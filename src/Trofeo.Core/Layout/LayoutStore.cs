using System.Text.Json;

namespace Trofeo.Core.Layout;

/// <summary>Empreinte d'un fichier de disposition : change quand il est réécrit.</summary>
public readonly record struct LayoutStamp(long Length, DateTime LastWriteUtc);

/// <summary>Lecture et écriture de la disposition dans un fichier JSON.</summary>
public sealed class LayoutStore
{
    private readonly string _filePath;
    private readonly Action<string>? _warn;

    /// <param name="filePath">Le fichier <c>layout.json</c>.</param>
    /// <param name="warn">Reçoit les avertissements (en français) : apparence, raccourci ou plafond d'images illisible écarté.</param>
    public LayoutStore(string filePath, Action<string>? warn = null)
    {
        _filePath = filePath;
        _warn = warn;
    }

    /// <summary>
    /// Ne lève jamais d'exception pour un problème de fichier. Fichier absent ou illisible (verrouillé) : disposition
    /// par défaut, sans copie. Fichier corrompu ou invalide (JSON illisible, rotation hors {0, 180}, module hors du
    /// damier, modules qui se chevauchent, type absent, identifiants d'instance en double) : défaut, et copie conservée en <c>.bad</c>.
    /// <c>Appearance</c> est lu à part, avec indulgence : s'il est illisible (mauvais type JSON, <c>Kind</c> inconnu), il
    /// vaut <c>null</c> et la disposition est gardée, sans <c>.bad</c>, avec un avertissement au <c>warn</c> du constructeur.
    /// <c>Shortcuts</c> (un raccourci illisible est écarté) et <c>MaxFps</c> (hors de 1 à 12 : <c>null</c>) sont lus de
    /// la même façon. <c>Views</c> aussi : un nom trop long est coupé, une vue en trop ou en double est écartée ou refaite
    /// (avertissement), et un fichier d'avant les vues (<c>Placements</c> à la racine) devient une seule vue « Vue 1 ».
    /// Un thème inconnu est gardé tel quel (c'est
    /// <see cref="Rendering.AppearanceResolver"/> qui retombe sur Classique).
    /// Les types inconnus et les tailles minimum dépendent du registre des modules : à vérifier par l'appelant
    /// avec <see cref="LayoutValidator.Check"/>.
    /// </summary>
    public DashboardLayout Load()
    {
        string json;
        try
        {
            if (!File.Exists(_filePath)) return DashboardLayout.Default;
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DashboardLayout.Default;
        }

        var result = LayoutJson.Parse(json, _warn);
        if (result.Layout is not null) return result.Layout;

        // Toute erreur de lecture ou de validation (JSON, type absent, débordement...) : défaut + copie .bad.
        try
        {
            File.Copy(_filePath, _filePath + ".bad", overwrite: true);
        }
        catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
        {
            // La sauvegarde est un bonus : on retombe quand même sur la disposition par défaut.
        }

        return DashboardLayout.Default;
    }

    /// <summary>
    /// Comme <see cref="Load"/>, mais sans jamais retomber sur le défaut ni copier en <c>.bad</c> : pour recharger le
    /// fichier pendant que le service tourne. Fichier absent : la disposition par défaut, sans erreur. Fichier
    /// verrouillé, illisible ou invalide : <see cref="LayoutLoadResult.Layout"/> vaut <c>null</c> et
    /// <see cref="LayoutLoadResult.Error"/> dit pourquoi, en français. Les avertissements vont aussi au <c>warn</c>
    /// du constructeur.
    /// </summary>
    public LayoutLoadResult TryLoad()
    {
        string json;
        try
        {
            if (!File.Exists(_filePath)) return new LayoutLoadResult(DashboardLayout.Default, null, []);
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LayoutLoadResult(null, "Le fichier layout.json est inaccessible (utilisé par un autre programme ?).", []);
        }

        return LayoutJson.Parse(json, _warn);
    }

    /// <summary>Le chemin du fichier <c>layout.json</c> (pour en garder une copie avant de le remplacer).</summary>
    public string FilePath => _filePath;

    /// <summary>Empreinte du fichier (taille, date d'écriture) ; <c>null</c> s'il est absent ou inaccessible.</summary>
    public LayoutStamp? Stamp()
    {
        try
        {
            var info = new FileInfo(_filePath);
            return info.Exists ? new LayoutStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Écriture atomique : fichier temporaire puis remplacement.</summary>
    public void Save(DashboardLayout layout)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(layout, LayoutJson.Options));
        File.Move(tmp, _filePath, overwrite: true);
    }
}
