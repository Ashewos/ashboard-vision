using Trofeo.ModuleKit;

namespace Trofeo.Core.Secrets;

/// <summary>
/// Un coffre à secrets qui sait refuser une écriture d'après ce qu'il contient déjà. Le service l'utilise pour plafonner le coffre
/// d'un module de la communauté (nombre de clés, taille totale) : la vérification et l'écriture se font d'un seul geste, sous le
/// même verrou, sans qu'une autre écriture se glisse entre les deux.
/// </summary>
public interface IGuardedSecretStore : ISecretStore
{
    /// <summary>
    /// Enregistre la valeur (<c>null</c> efface la clé) si <paramref name="accept"/> l'accepte, et rend vrai. <paramref name="accept"/>
    /// reçoit tout ce que le coffre contient déjà (clé -> valeur) ; elle est appelée une fois, sous le verrou du coffre. Si elle
    /// refuse, rien n'est écrit et le résultat est faux.
    /// </summary>
    bool TrySet(string key, string? value, Func<IReadOnlyDictionary<string, string>, bool> accept);
}
