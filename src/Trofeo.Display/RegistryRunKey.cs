using Microsoft.Win32;
using Trofeo.Distribution;

namespace Trofeo.Display;

/// <summary>
/// La clé <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> pour la préparation et le nettoyage propres à l'utilisateur
/// (mêmes clés que <c>RunKeyStartup</c>, dans le logiciel de réglage). Pas besoin d'être administrateur. Le chemin de la clé se règle
/// pour les tests, qui ne touchent jamais à la vraie.
/// </summary>
public sealed class RegistryRunKey(string keyPath = RegistryRunKey.DefaultKeyPath) : IRunKey
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(name) as string;
    }

    public void Write(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
