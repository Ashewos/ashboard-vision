using AshboardVision.Sdk;

namespace Trofeo.Settings.Services;

/// <summary>
/// Le nom et l'explication de chaque permission, tels que la fenêtre les montre. Ils sont dans <c>Strings.resx</c> : la fenêtre ne
/// référence pas <c>Trofeo.Extensions</c> (un test vérifie qu'ils disent la même chose que <c>PermissionTexts</c> du service).
/// </summary>
public static class PermissionLabels
{
    public static string Label(ModulePermission permission) => permission switch
    {
        ModulePermission.LocalNetwork => Texts.PermissionLocalNetwork,
        ModulePermission.Internet => Texts.PermissionInternet,
        ModulePermission.ThisComputer => Texts.PermissionThisComputer,
        ModulePermission.Sensors => Texts.PermissionSensors,
        ModulePermission.NowPlaying => Texts.PermissionNowPlaying,
        _ => permission.ToString(),
    };

    public static string Explanation(ModulePermission permission) => permission switch
    {
        ModulePermission.LocalNetwork => Texts.PermissionLocalNetworkHelp,
        ModulePermission.Internet => Texts.PermissionInternetHelp,
        ModulePermission.ThisComputer => Texts.PermissionThisComputerHelp,
        ModulePermission.Sensors => Texts.PermissionSensorsHelp,
        ModulePermission.NowPlaying => Texts.PermissionNowPlayingHelp,
        _ => "",
    };

    /// <summary>Les noms, séparés par des virgules, dans l'ordre des permissions ; « Aucune permission accordée. » s'il n'y en a pas.</summary>
    public static string List(IEnumerable<ModulePermission> permissions)
    {
        var names = permissions.Distinct().Order().Select(Label).ToList();
        return names.Count == 0 ? Texts.CommunityNoneGranted : string.Format(Texts.CommunityGranted, string.Join(", ", names));
    }
}
