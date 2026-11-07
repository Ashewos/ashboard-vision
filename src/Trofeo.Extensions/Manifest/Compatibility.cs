namespace Trofeo.Extensions;

/// <summary>Ce que le logiciel pense d'un module d'après la version du SDK qu'il demande.</summary>
public enum CompatibilityKind
{
    /// <summary>Le module tourne.</summary>
    Compatible,

    /// <summary>Le module demande une version plus récente que celle du logiciel : il faut mettre le logiciel à jour.</summary>
    NeedsNewerApp,

    /// <summary>Le module vise une version majeure que le logiciel ne prend plus en charge : il faut que son auteur le mette à jour.</summary>
    TooOld,
}

/// <summary>
/// La compatibilité des modules avec le SDK. Un module déclare la version <b>minimale</b> du SDK dont il a besoin :
/// un module pour la 1.2 tourne dans un logiciel qui parle la 1.5. Quand le premier chiffre change, le logiciel garde
/// l'ancienne majeure dans <see cref="SupportedMajors"/> pendant une période de transition.
/// </summary>
public static class Compatibility
{
    /// <summary>La version du SDK que parle ce logiciel.</summary>
    public static SdkVersion Current { get; } = new(1, 0);

    /// <summary>Les versions majeures que ce logiciel accepte encore.</summary>
    public static IReadOnlySet<int> SupportedMajors { get; } = new HashSet<int> { 1 };

    /// <summary>Compare la version demandée par un module à celle du logiciel.</summary>
    public static CompatibilityKind Check(SdkVersion required) => Check(required, Current, SupportedMajors);

    /// <summary>La même comparaison avec une version courante et des majeures données (tests, période de transition).</summary>
    internal static CompatibilityKind Check(SdkVersion required, SdkVersion current, IReadOnlySet<int> majors)
    {
        if (required.Major > current.Major)
        {
            return CompatibilityKind.NeedsNewerApp;
        }

        if (!majors.Contains(required.Major))
        {
            return CompatibilityKind.TooOld;
        }

        // Une ancienne majeure encore acceptée n'a pas de « deuxième chiffre » à comparer à celui du logiciel.
        return required.Major == current.Major && required.Minor > current.Minor
            ? CompatibilityKind.NeedsNewerApp
            : CompatibilityKind.Compatible;
    }

    /// <summary>La phrase montrée à l'utilisateur ; vide pour <see cref="CompatibilityKind.Compatible"/>.</summary>
    public static string Message(CompatibilityKind kind) => kind switch
    {
        CompatibilityKind.NeedsNewerApp => "Ce module demande une version plus récente d'Ashboard Vision : mets à jour le logiciel.",
        CompatibilityKind.TooOld => "Ce module a été écrit pour une ancienne version du SDK qu'Ashboard Vision ne prend plus en charge : demande à son auteur de le mettre à jour.",
        _ => "",
    };
}
