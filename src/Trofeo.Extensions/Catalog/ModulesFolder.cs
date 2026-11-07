namespace Trofeo.Extensions;

/// <summary>Où vivent les modules de la communauté : <c>&lt;dossier des modules&gt;\&lt;id&gt;\&lt;version&gt;\</c>.</summary>
public static class ModulesFolder
{
    /// <summary>Le dossier par défaut : <c>Documents\Ashboard Vision\Modules</c>.</summary>
    public static string Default { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ashboard Vision", "Modules");

    /// <summary>Le dossier des modules choisi par l'utilisateur, ou le dossier par défaut s'il n'en a pas choisi.</summary>
    public static string Resolve(ApprovedModules modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return string.IsNullOrWhiteSpace(modules.ModulesRoot) ? Default : modules.ModulesRoot;
    }
}
