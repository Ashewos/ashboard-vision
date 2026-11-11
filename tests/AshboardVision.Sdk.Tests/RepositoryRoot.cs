namespace AshboardVision.Sdk.Tests;

/// <summary>Retrouve la racine du dépôt (le dossier de la solution) en remontant depuis le dossier des tests.</summary>
internal static class RepositoryRoot
{
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "trofeo-HB-dashboard.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("La racine du dépôt (trofeo-HB-dashboard.sln) est introuvable au-dessus de " + AppContext.BaseDirectory);
    }
}
