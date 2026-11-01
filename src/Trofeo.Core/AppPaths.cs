namespace Trofeo.Core;

/// <summary>Emplacements des fichiers de l'application.</summary>
public static class AppPaths
{
    /// <summary>Dossier des réglages : <c>%AppData%\ashboard-vision</c>.</summary>
    public static string SettingsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductIdentity.Id);

    /// <summary>
    /// Dossier des réglages d'avant le changement de nom : <c>%AppData%\trofeo-HB-dashboard</c>. Il n'est plus écrit ;
    /// l'installation le déménage vers <see cref="SettingsDir"/>.
    /// </summary>
    public static string LegacySettingsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductIdentity.LegacyId);
}
