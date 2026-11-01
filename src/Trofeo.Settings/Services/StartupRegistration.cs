using Microsoft.Win32;

namespace Trofeo.Settings.Services;

/// <summary>
/// Le démarrage du service d'affichage avec Windows. Derrière une interface pour que les tests n'écrivent pas dans le
/// registre. <see cref="Enable"/> et <see cref="Disable"/> peuvent lever <see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/> ou <see cref="System.Security.SecurityException"/> si Windows refuse.
/// </summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    /// <summary>Inscrit <paramref name="exePath"/> au démarrage de la session.</summary>
    void Enable(string exePath);

    void Disable();
}

/// <summary>Sans Windows (tests, aperçus) : rien n'est inscrit.</summary>
public sealed class NoStartupRegistration : IStartupRegistration
{
    public bool IsEnabled => false;
    public void Enable(string exePath) { }
    public void Disable() { }
}

/// <summary>
/// La clé <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, valeur <c>ashboard-vision</c> : le chemin du service
/// entre guillemets. Pas besoin d'être administrateur.
/// </summary>
public sealed class RunKeyStartup : IStartupRegistration
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ashboard-vision";

    /// <summary>La valeur d'avant le changement de nom : la préparation propre à l'utilisateur, à l'installation, la retire.</summary>
    public const string LegacyValueName = "trofeo-HB-dashboard";

    /// <summary>La valeur écrite : le chemin entre guillemets, pour qu'un dossier avec des espaces ne soit pas coupé.</summary>
    public static string Command(string exePath) => "\"" + exePath + "\"";

    /// <summary>Le registre de Windows ; ailleurs (tests sous Linux), <see cref="NoStartupRegistration"/>.</summary>
    public static IStartupRegistration Create() =>
        OperatingSystem.IsWindows() ? new RunKeyStartup() : new NoStartupRegistration();

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false; // clé illisible : on la montre éteinte, et l'inscrire dira pourquoi si Windows refuse
            }
        }
    }

    public void Enable(string exePath)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, Command(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
