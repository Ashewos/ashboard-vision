using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace Trofeo.Sensors;

/// <summary>
/// Identité de l'assistant de capteurs. Le client n'accepte un tube que si son propriétaire est SYSTEM ou le
/// groupe Administrateurs : un utilisateur ordinaire ne peut pas créer un objet qui leur appartienne, donc il
/// ne peut pas se faire passer pour l'assistant en créant le tube avant lui. (Le chemin de l'exécutable du
/// serveur n'est pas lisible depuis un processus non élevé quand l'assistant tourne sous SYSTEM.)
/// </summary>
public static class HelperIdentity
{
    /// <summary><c>%ProgramFiles%\Ashboard Vision\sensors\</c>, séparateur final compris : dossier d'installation de l'assistant.</summary>
    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ashboard Vision", "sensors")
        + Path.DirectorySeparatorChar;

    /// <summary>
    /// <c>%ProgramFiles%\trofeo-HB-dashboard\sensors\</c>, séparateur final compris : dossier d'installation d'avant le changement de
    /// nom. L'installation de l'assistant le supprime.
    /// </summary>
    public static string LegacyInstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "trofeo-HB-dashboard", "sensors")
        + Path.DirectorySeparatorChar;

    /// <summary>Vrai si le tube connecté appartient à SYSTEM ou aux Administrateurs. Toute erreur de lecture donne faux.</summary>
    public static bool IsTrusted(NamedPipeClientStream pipe)
    {
        try
        {
            return IsTrustedOwner(ReadOwner(pipe));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Vérification du propriétaire du tube des capteurs impossible : {ex.Message}");
            return false;
        }
    }

    /// <summary>Règle de confiance : SYSTEM (S-1-5-18) ou BUILTIN\Administrators (S-1-5-32-544).</summary>
    internal static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null
        && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    /// <summary>Propriétaire du tube (demande le droit de lire le descripteur de sécurité, accordé aux utilisateurs authentifiés).</summary>
    internal static SecurityIdentifier? ReadOwner(NamedPipeClientStream pipe) =>
        pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
}
