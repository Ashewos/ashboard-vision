namespace Trofeo.ModuleKit;

/// <summary>
/// Remarques d'un module sur sa propre configuration (« 3 lignes sur 5 tiennent à cette taille. »).
/// Lu depuis n'importe quel thread.
/// </summary>
public interface IModuleNotices
{
    /// <summary>Les remarques en cours. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    IReadOnlyList<string> Notices { get; }
}
