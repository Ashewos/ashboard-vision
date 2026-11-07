using Trofeo.Control;

namespace Trofeo.Settings.Services;

public static class ModuleTypeLabels
{
    /// <summary>
    /// Le nom d'un type de module tel que la fenêtre le montre. Celui d'un module complémentaire vient de son manifeste (une donnée non
    /// fiable) : il est nettoyé (<see cref="UntrustedText"/>), et l'identifiant le remplace s'il ne reste rien.
    /// </summary>
    public static string Label(this ModuleTypeInfo type)
    {
        if (type.Community is null)
            return type.DisplayName;
        var cleaned = UntrustedText.Line(type.DisplayName);
        return cleaned.Length > 0 ? cleaned : type.TypeId;
    }
}
