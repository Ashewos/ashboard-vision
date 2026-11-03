namespace Trofeo.Settings.Services;

/// <summary>
/// <c>Trofeo.Settings.exe &lt;chemin.avmod&gt;</c> : le chemin d'un module à installer, tel que la ligne de commande le donne. Ce n'est
/// qu'un chemin : le service lit le fichier (sans rien écrire), la boîte d'installation montre ce qu'il contient, et rien ne s'installe
/// sans le clic sur « Installer ».
/// </summary>
public static class PackageArgument
{
    /// <summary>Le premier argument qui se termine par <c>.avmod</c>, en chemin complet ; <c>null</c> s'il n'y en a pas (ou si le chemin est invalide).</summary>
    public static string? Find(IEnumerable<string>? args)
    {
        foreach (var arg in args ?? [])
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.EndsWith(".avmod", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                return Path.GetFullPath(arg.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        return null;
    }
}
