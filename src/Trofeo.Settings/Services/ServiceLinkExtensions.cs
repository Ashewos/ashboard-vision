using Trofeo.Control;

namespace Trofeo.Settings.Services;

public static class ServiceLinkExtensions
{
    /// <summary>
    /// Les types de modules que le service connaît maintenant : ceux relus après un changement des modules complémentaires, sinon ceux
    /// de la poignée de main ; <c>null</c> tant qu'on n'est pas connecté. La référence change quand la liste change : de quoi savoir si
    /// un onglet doit refaire ses listes.
    /// </summary>
    public static IReadOnlyList<ModuleTypeInfo>? KnownTypes(this IServiceLink link) => link.Types ?? link.Welcome?.Types;
}
