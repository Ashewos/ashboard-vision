using System.Reflection;
using Microsoft.Win32;

namespace Trofeo.Display;

/// <summary>
/// Le thème de la barre des tâches (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>, valeur
/// <c>SystemUsesLightTheme</c>) et l'icône de la zone de notification qui lui va : l'icône claire sur une barre claire, l'icône
/// sombre sur une barre sombre (le choix d'Alex). Les fichiers <c>tray-clair.ico</c> et <c>tray-sombre.ico</c> sont des ressources incorporées de
/// <c>Trofeo.Display</c>, sous ces noms.
/// </summary>
internal static class TaskbarTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightValue = "SystemUsesLightTheme";

    /// <summary>La ressource de l'icône pour une barre claire (<c>tray-clair.ico</c>) ou sombre (<c>tray-sombre.ico</c>).</summary>
    internal static string IconResourceFor(bool lightTaskbar) => lightTaskbar ? "tray-clair.ico" : "tray-sombre.ico";

    /// <summary>Vrai si la valeur du registre vaut 1. Une valeur absente ou d'un autre type vaut « sombre ».</summary>
    internal static bool IsLight(object? registryValue) => registryValue is int and 1;

    /// <summary>La barre des tâches est-elle claire en ce moment ? Une clé illisible vaut « sombre ».</summary>
    internal static bool IsTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return IsLight(key?.GetValue(LightValue));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// L'icône incorporée sous ce nom, ou <c>null</c> si elle n'existe pas. L'appelant libère l'icône rendue. Une ressource illisible
    /// vaut « absente » : l'icône de la zone de notification n'arrête jamais le service.
    /// </summary>
    internal static Icon? TryLoadIcon(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            return stream is null ? null : new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }
}
