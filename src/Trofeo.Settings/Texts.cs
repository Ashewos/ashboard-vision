using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Trofeo.Settings;

/// <summary>
/// Les textes de la fenêtre, lus dans <c>Resources/Strings.resx</c> : une propriété par clé (le XAML les lit par
/// <c>{x:Static}</c>). Un test vérifie que chaque clé a sa propriété, et que chacune sert. Écrite à la main, parce que
/// le générateur de ressources de Roslyn n'existe qu'en préversion.
/// </summary>
public static class Texts
{
    private static readonly ResourceManager Manager = new("Trofeo.Settings.Resources.Strings", typeof(Texts).Assembly);

    private static string Get([CallerMemberName] string key = "") => Manager.GetString(key, CultureInfo.InvariantCulture) ?? key;

    /// <summary>Titre de la fenêtre.</summary>
    public static string WindowTitle => Get();
    /// <summary>Nom du produit, à gauche de l'en-tête.</summary>
    public static string AppName => Get();
    /// <summary>Nom accessible de la barre d'onglets.</summary>
    public static string TabsName => Get();
    /// <summary>Disposition</summary>
    public static string TabLayout => Get();
    /// <summary>Personnalisation</summary>
    public static string TabAppearance => Get();
    /// <summary>Modules</summary>
    public static string TabModules => Get();
    /// <summary>Raccourcis</summary>
    public static string TabShortcuts => Get();
    /// <summary>Paramètres</summary>
    public static string TabSettings => Get();
    /// <summary>Pastille de l'écran : l'écran répond.</summary>
    public static string ScreenOnline => Get();
    /// <summary>Pastille de l'écran : aucun écran.</summary>
    public static string ScreenIdle => Get();
    /// <summary>Pastille de l'écran : le service reprend la liaison.</summary>
    public static string ScreenRecovering => Get();
    /// <summary>Pastille ambre : le service compose sans écran branché.</summary>
    public static string EmulatedMode => Get();
    /// <summary>Pastille pendant la connexion au service.</summary>
    public static string LinkConnecting => Get();
    /// <summary>Pastille quand le service ne tourne pas.</summary>
    public static string LinkStopped => Get();
    /// <summary>Nom accessible de la bande.</summary>
    public static string StripName => Get();
    /// <summary>Texte de la bande tant qu'aucune image n'est arrivée.</summary>
    public static string StripWaiting => Get();
    /// <summary>Le service d'affichage ne tourne pas. Sans lui, l'écran reste éteint et rien ne peut être réglé.</summary>
    public static string ServiceStoppedMessage => Get();
    /// <summary>Boîte de message au démarrage : le service n'a pas fini de déménager les réglages.</summary>
    public static string MoveInProgress => Get();
    /// <summary>Boîte de message au démarrage : le service n'a pas pu être lancé pour déménager les réglages.</summary>
    public static string MoveServiceMissing => Get();
    /// <summary>Pastille quand le service parle une autre version que la fenêtre.</summary>
    public static string LinkVersionMismatch => Get();
    /// <summary>Panneau quand le service parle une autre version que la fenêtre.</summary>
    public static string VersionMismatchMessage => Get();
    /// <summary>Démarrer le service</summary>
    public static string StartService => Get();
    /// <summary>Le service d'affichage est introuvable. Lance Trofeo.Display.exe toi-même.</summary>
    public static string ServiceNotFound => Get();
    /// <summary>Erreur quand la liaison est coupée.</summary>
    public static string LinkLost => Get();
    /// <summary>Erreur quand la liaison se ferme pendant une demande (fermeture de la fenêtre, coupure).</summary>
    public static string LinkCut => Get();
    /// <summary>La disposition a changé en dehors des réglages : elle a été relue.</summary>
    public static string LayoutChangedOutside => Get();
    /// <summary>{0} : le motif, sans point final.</summary>
    public static string LayoutUnreadable => Get();
    /// <summary>Bouton de la ligne ambre quand layout.json est illisible.</summary>
    public static string ResetLayout => Get();
    /// <summary>{0} : le message de l'exception.</summary>
    public static string UnexpectedError => Get();
    /// <summary>Le service refuse sans donner de motif.</summary>
    public static string ReloadRefused => Get();
    /// <summary>{0} : la cause.</summary>
    public static string SaveFailed => Get();
    /// <summary>Titre de la liste des modules placés.</summary>
    public static string LayoutOnScreen => Get();
    /// <summary>Titre de la liste des modules retirés de l'écran.</summary>
    public static string LayoutShelved => Get();
    /// <summary>Bouton qui ouvre la liste des types de modules.</summary>
    public static string LayoutAddModule => Get();
    /// <summary>Remet un module retiré sur l'écran.</summary>
    public static string LayoutRestore => Get();
    /// <summary>Supprime pour de bon un module retiré.</summary>
    public static string LayoutForget => Get();
    /// <summary>Confirmation avant d'oublier un module retiré.</summary>
    public static string LayoutForgetConfirm => Get();
    /// <summary>Renonce à oublier un module retiré.</summary>
    public static string LayoutForgetKeep => Get();
    /// <summary>Consigne sous la bande.</summary>
    public static string LayoutHint => Get();
    /// <summary>Annule la dernière modification de la disposition.</summary>
    public static string LayoutUndo => Get();
    /// <summary>Rétablit la modification annulée.</summary>
    public static string LayoutRedo => Get();
    /// <summary>{0} : le nom du module qui gêne.</summary>
    public static string LayoutBlocked => Get();
    /// <summary>Le cadre sort du damier.</summary>
    public static string LayoutOutside => Get();
    /// <summary>{0} : le nom du module ; {1} × {2} : colonnes × rangées minimum.</summary>
    public static string LayoutBelowMinimum => Get();
    /// <summary>Ajout impossible.</summary>
    public static string LayoutNoRoom => Get();
    /// <summary>Le service ne connaît pas ce type.</summary>
    public static string LayoutUnknownType => Get();
    /// <summary>{0} × {1} : colonnes × rangées du module choisi.</summary>
    public static string LayoutCells => Get();
    /// <summary>{0} × {1} : largeur × hauteur en pixels du module choisi.</summary>
    public static string LayoutPixels => Get();
    /// <summary>{0} × {1} : colonnes × rangées minimum du type.</summary>
    public static string LayoutMinimum => Get();
    /// <summary>Taille d'un module dans la liste : colonnes × rangées.</summary>
    public static string LayoutSize => Get();
    /// <summary>Titre des boutons de préréglage.</summary>
    public static string LayoutPresets => Get();
    /// <summary>Compact</summary>
    public static string LayoutPresetCompact => Get();
    /// <summary>Idéal</summary>
    public static string LayoutPresetIdeal => Get();
    /// <summary>Large</summary>
    public static string LayoutPresetLarge => Get();
    /// <summary>Titre des boutons de taille de texte.</summary>
    public static string LayoutTextSize => Get();
    /// <summary>Petit</summary>
    public static string LayoutTextSmall => Get();
    /// <summary>Normal</summary>
    public static string LayoutTextNormal => Get();
    /// <summary>Grand</summary>
    public static string LayoutTextLarge => Get();
    /// <summary>Retire le module choisi de l'écran (il reste dans « Retirés »).</summary>
    public static string LayoutRemove => Get();
    /// <summary>Colonne de droite sans module choisi.</summary>
    public static string LayoutNothingSelected => Get();
    /// <summary>Liste des modules placés vide.</summary>
    public static string LayoutEmpty => Get();
    /// <summary>Point d'état d'un module.</summary>
    public static string LayoutHealthOnline => Get();
    /// <summary>Point d'état d'un module.</summary>
    public static string LayoutHealthIdle => Get();
    /// <summary>Point d'état d'un module.</summary>
    public static string LayoutHealthOffline => Get();
    /// <summary>Nom accessible du damier posé sur la bande.</summary>
    public static string LayoutGridName => Get();
    /// <summary>Titre de la section des thèmes.</summary>
    public static string AppearanceTheme => Get();
    /// <summary>Nom du thème Classique.</summary>
    public static string AppearanceThemeClassic => Get();
    /// <summary>Phrase de la carte Classique.</summary>
    public static string AppearanceThemeClassicText => Get();
    /// <summary>Nom du thème Liquid Glass.</summary>
    public static string AppearanceThemeLiquidGlass => Get();
    /// <summary>Phrase de la carte Liquid Glass.</summary>
    public static string AppearanceThemeLiquidGlassText => Get();
    /// <summary>Nom du thème Pixel.</summary>
    public static string AppearanceThemePixel => Get();
    /// <summary>Phrase de la carte Pixel.</summary>
    public static string AppearanceThemePixelText => Get();
    /// <summary>Titre de la section du curseur de transparence des panneaux.</summary>
    public static string AppearanceOpacity => Get();
    /// <summary>{0} : la transparence choisie, de 0 à 100.</summary>
    public static string AppearanceOpacityValue => Get();
    /// <summary>Transparence ou police : ne rien imposer, suivre le thème.</summary>
    public static string AppearanceFromThemeFeminine => Get();
    /// <summary>Fond : ne rien imposer, suivre le thème.</summary>
    public static string AppearanceFromThemeMasculine => Get();
    /// <summary>Titre de la section de la liste des polices.</summary>
    public static string AppearanceFont => Get();
    /// <summary>Titre de la section du fond de l'écran.</summary>
    public static string AppearanceBackground => Get();
    /// <summary>Fond : une couleur unie.</summary>
    public static string AppearanceBackgroundColor => Get();
    /// <summary>Fond : une image fixe.</summary>
    public static string AppearanceBackgroundPhoto => Get();
    /// <summary>Fond : un GIF ou un WebP animé.</summary>
    public static string AppearanceBackgroundGif => Get();
    /// <summary>Fond : une vidéo.</summary>
    public static string AppearanceBackgroundVideo => Get();
    /// <summary>Nom accessible du champ de la couleur.</summary>
    public static string AppearanceHexName => Get();
    /// <summary>Texte d'aide du champ de la couleur.</summary>
    public static string AppearanceHexHint => Get();
    /// <summary>Le champ de la couleur ne contient pas #RRGGBB.</summary>
    public static string AppearanceBadColor => Get();
    /// <summary>Ouvre le sélecteur de fichier du fond.</summary>
    public static string AppearanceChooseFile => Get();
    /// <summary>Fond photo, GIF ou vidéo sans fichier.</summary>
    public static string AppearanceNoFile => Get();
    /// <summary>Conseil sous le choix d'une vidéo.</summary>
    public static string AppearanceVideoAdvice => Get();
    /// <summary>Titre du sélecteur de fichier (photo).</summary>
    public static string AppearancePickPhoto => Get();
    /// <summary>Titre du sélecteur de fichier (GIF).</summary>
    public static string AppearancePickGif => Get();
    /// <summary>Titre du sélecteur de fichier (vidéo).</summary>
    public static string AppearancePickVideo => Get();
    /// <summary>Nom du filtre du sélecteur de fichier (photos).</summary>
    public static string AppearanceTypePhotos => Get();
    /// <summary>Nom du filtre du sélecteur de fichier (GIF).</summary>
    public static string AppearanceTypeGifs => Get();
    /// <summary>Nom du filtre du sélecteur de fichier (vidéos).</summary>
    public static string AppearanceTypeVideos => Get();
    /// <summary>Titre de l'interrupteur de la garde de lisibilité.</summary>
    public static string AppearanceLegibility => Get();
    /// <summary>Explication sous l'interrupteur.</summary>
    public static string AppearanceLegibilityHelp => Get();
    /// <summary>Titre de l'interrupteur du glissement d'une vue à l'autre.</summary>
    public static string AppearanceAnimateViewSwitch => Get();
    /// <summary>Explication sous l'interrupteur.</summary>
    public static string AppearanceAnimateViewSwitchHelp => Get();
    /// <summary>{0} : la cause. Le sélecteur de fichier de Windows a échoué.</summary>
    public static string AppearancePickFailed => Get();
    /// <summary>Colonne de droite de l'onglet Modules sans module choisi.</summary>
    public static string ModulesNothingSelected => Get();
    /// <summary>Le type de module n'a aucun schéma de réglages.</summary>
    public static string ModulesNothingToSet => Get();
    /// <summary>Un secret est dans le coffre (sa valeur ne s'affiche jamais).</summary>
    public static string ModulesSecretPresent => Get();
    /// <summary>Aucun secret dans le coffre.</summary>
    public static string ModulesSecretAbsent => Get();
    /// <summary>Ouvre la saisie masquée d'un secret.</summary>
    public static string ModulesSecretEdit => Get();
    /// <summary>Écrit le secret saisi dans le coffre.</summary>
    public static string ModulesSecretSave => Get();
    /// <summary>Retire le secret du coffre.</summary>
    public static string ModulesSecretClear => Get();
    /// <summary>Renonce à une saisie ou à une action en attente de confirmation.</summary>
    public static string ModulesCancel => Get();
    /// <summary>{0} : la cause. Le coffre à secrets est verrouillé ou illisible (suite de « Enregistrer »).</summary>
    public static string ModulesSecretSaveFailed => Get();
    /// <summary>{0} : la cause. Le coffre à secrets est verrouillé ou illisible (suite de « Effacer »).</summary>
    public static string ModulesSecretClearFailed => Get();
    /// <summary>{0} : la cause. Le coffre n'a pas pu dire si la clé est enregistrée ; elle est montrée absente.</summary>
    public static string ModulesSecretUnreadable => Get();
    /// <summary>Fenêtre sans coffre (tests, aperçus).</summary>
    public static string ModulesSecretUnavailable => Get();
    /// <summary>{0} : la cause. Ligne Amber de la fenêtre après un secret changé.</summary>
    public static string ModulesRestartFailed => Get();
    /// <summary>Ligne Amber : le service ne connaît pas l'instance.</summary>
    public static string ModulesRestartMissing => Get();
    /// <summary>Texte du bouton d'une action pendant qu'elle tourne.</summary>
    public static string ModulesActionRunning => Get();
    /// <summary>Sous une action d'un module retiré : le service ne le fait pas tourner.</summary>
    public static string ModulesActionShelved => Get();
    /// <summary>Relance le chargement d'une liste vide.</summary>
    public static string ModulesLookupRefresh => Get();
    /// <summary>{0} : la cause. Une recherche ou le chargement d'une liste a échoué.</summary>
    public static string ModulesLookupFailed => Get();
    /// <summary>Une recherche n'a rien trouvé.</summary>
    public static string ModulesLookupNoResult => Get();
    /// <summary>Une liste à choisir est vide.</summary>
    public static string ModulesLookupNothing => Get();
    /// <summary>Une recherche est en cours.</summary>
    public static string ModulesLookupLoading => Get();
    /// <summary>Nom accessible de la flèche qui monte une ligne.</summary>
    public static string ModulesMoveUp => Get();
    /// <summary>Nom accessible de la flèche qui descend une ligne.</summary>
    public static string ModulesMoveDown => Get();
    /// <summary>Phrase sous la liste des raccourcis.</summary>
    public static string ShortcutsIntro => Get();
    /// <summary>La liste des raccourcis est vide.</summary>
    public static string ShortcutsEmpty => Get();
    /// <summary>Action d'un raccourci.</summary>
    public static string ShortcutPrevious => Get();
    /// <summary>Action d'un raccourci.</summary>
    public static string ShortcutNext => Get();
    /// <summary>{0} : le rang de l'onglet, à partir de 1.</summary>
    public static string ShortcutSelect => Get();
    /// <summary>Nom accessible de la liste des actions d'un nouveau raccourci.</summary>
    public static string ShortcutAction => Get();
    /// <summary>Nom accessible de la liste des modules visés par un raccourci.</summary>
    public static string ShortcutTarget => Get();
    /// <summary>Module visé : le premier qui accepte les commandes.</summary>
    public static string ShortcutTargetFirst => Get();
    /// <summary>Module visé : l'instance n'est plus sur l'écran.</summary>
    public static string ShortcutTargetGone => Get();
    /// <summary>Bouton : change les touches d'un raccourci.</summary>
    public static string ShortcutEdit => Get();
    /// <summary>Bouton : retire un raccourci.</summary>
    public static string ShortcutDelete => Get();
    /// <summary>Bouton.</summary>
    public static string ShortcutAdd => Get();
    /// <summary>Bouton.</summary>
    public static string ShortcutReset => Get();
    /// <summary>Pendant la capture d'une combinaison.</summary>
    public static string ShortcutCapture => Get();
    /// <summary>{0} : les touches (Ctrl+Alt+Right). {1} : l'action qui les a déjà.</summary>
    public static string ShortcutDuplicate => Get();
    /// <summary>Une lettre ou un chiffre avec Ctrl ou Maj seulement est refusé : il faut Alt ou Win.</summary>
    public static string ShortcutNeedsAltOrWin => Get();
    /// <summary>Titre de section.</summary>
    public static string SettingsOrientation => Get();
    /// <summary>Choix d'orientation.</summary>
    public static string SettingsUpright => Get();
    /// <summary>Choix d'orientation.</summary>
    public static string SettingsFlipped => Get();
    /// <summary>Interrupteur.</summary>
    public static string SettingsStartup => Get();
    /// <summary>Sous l'interrupteur de démarrage.</summary>
    public static string SettingsStartupHelp => Get();
    /// <summary>Trofeo.Display.exe n'est nulle part.</summary>
    public static string SettingsStartupMissing => Get();
    /// <summary>{0} : la cause.</summary>
    public static string SettingsStartupFailed => Get();
    /// <summary>Titre de section.</summary>
    public static string SettingsFps => Get();
    /// <summary>Valeur du curseur au maximum.</summary>
    public static string SettingsFpsMax => Get();
    /// <summary>Titre de section.</summary>
    public static string SettingsPreview => Get();
    /// <summary>Interrupteur.</summary>
    public static string SettingsRealTime => Get();
    /// <summary>Sous l'interrupteur du rendu réel.</summary>
    public static string SettingsRealTimeHelp => Get();
    /// <summary>Titre de section.</summary>
    public static string SettingsSensors => Get();
    /// <summary>État des capteurs.</summary>
    public static string SensorsActive => Get();
    /// <summary>État des capteurs.</summary>
    public static string SensorsNoDriver => Get();
    /// <summary>État des capteurs.</summary>
    public static string SensorsNoHelper => Get();
    /// <summary>Bouton.</summary>
    public static string SensorsInstall => Get();
    /// <summary>Titre du sélecteur de l'installeur de PawnIO.</summary>
    public static string SensorsPickTitle => Get();
    /// <summary>Type de fichier du sélecteur.</summary>
    public static string SensorsPickType => Get();
    /// <summary>Le lancement en administrateur a été refusé.</summary>
    public static string SensorsDenied => Get();
    /// <summary>L'assistant d'installation est lancé.</summary>
    public static string SensorsStarted => Get();
    /// <summary>Trofeo.SensorHelper.exe n'est nulle part.</summary>
    public static string SensorsHelperMissing => Get();
    /// <summary>{0} : la cause.</summary>
    public static string SensorsLaunchFailed => Get();
    /// <summary>Titre de section.</summary>
    public static string SettingsUsage => Get();
    /// <summary>{0} : mémoire en Mo. {1} : processeur, avec une virgule.</summary>
    public static string UsageService => Get();
    /// <summary>{0} : mémoire de la fenêtre en Mo.</summary>
    public static string UsageOwn => Get();
    /// <summary>Sous la consommation.</summary>
    public static string UsageGoal => Get();
    /// <summary>Titre de section.</summary>
    public static string SettingsShare => Get();
    /// <summary>Bouton.</summary>
    public static string ShareExport => Get();
    /// <summary>Bouton.</summary>
    public static string ShareImport => Get();
    /// <summary>Titre de la boîte « Enregistrer sous ».</summary>
    public static string ShareExportTitle => Get();
    /// <summary>Titre du sélecteur de fichier.</summary>
    public static string ShareImportTitle => Get();
    /// <summary>Type de fichier des boîtes d'export et d'import.</summary>
    public static string ShareFileType => Get();
    /// <summary>{0} : le nom du fichier.</summary>
    public static string ShareExported => Get();
    /// <summary>Après un import réussi.</summary>
    public static string ShareImported => Get();
    /// <summary>{0} : les noms des modules, séparés par une virgule.</summary>
    public static string ShareKeptSettings => Get();
    /// <summary>{0} : la cause.</summary>
    public static string ShareReadFailed => Get();
    /// <summary>{0} : la cause.</summary>
    public static string ShareWriteFailed => Get();
    /// <summary>{0} : le type de module.</summary>
    public static string ShareUnknownType => Get();
    /// <summary>{0} : le nom du module. {1} × {2} : son minimum en cases.</summary>
    public static string ShareTooSmall => Get();
    /// <summary>{0} : la version du service.</summary>
    public static string SettingsVersion => Get();
    /// <summary>{0} : le numéro de l'étape, {1} : le nombre d'étapes qui existent.</summary>
    public static string FirstRunProgress => Get();
    /// <summary>Nom accessible de la barre de progression.</summary>
    public static string FirstRunProgressName => Get();
    /// <summary>Bouton : applique l'étape et passe à la suivante.</summary>
    public static string FirstRunContinue => Get();
    /// <summary>Bouton de la dernière étape : ferme l'assistant.</summary>
    public static string FirstRunFinish => Get();
    /// <summary>Bouton : passe à la suite sans rien changer.</summary>
    public static string FirstRunSkip => Get();
    /// <summary>Titre de l'étape 1.</summary>
    public static string FirstRunScreenTitle => Get();
    /// <summary>Titre de l'étape 2.</summary>
    public static string FirstRunOrientationTitle => Get();
    /// <summary>Titre de l'étape 3.</summary>
    public static string FirstRunModulesTitle => Get();
    /// <summary>Titre de l'étape 4.</summary>
    public static string FirstRunClaudeTitle => Get();
    /// <summary>Titre de l'étape 5.</summary>
    public static string FirstRunNetworkTitle => Get();
    /// <summary>Titre de l'étape 6.</summary>
    public static string FirstRunWeatherTitle => Get();
    /// <summary>Titre de l'étape 8.</summary>
    public static string FirstRunStartupTitle => Get();
    /// <summary>Étape 1, au-dessus de la pastille.</summary>
    public static string FirstRunScreenIntro => Get();
    /// <summary>Étape 1 : l'écran est branché.</summary>
    public static string FirstRunScreenReady => Get();
    /// <summary>Étape 1 : le motif de l'écran contient TRCC.</summary>
    public static string FirstRunScreenBusy => Get();
    /// <summary>Étape 1 : l'écran n'est pas branché.</summary>
    public static string FirstRunScreenMissing => Get();
    /// <summary>Étape 1 : mode émulé.</summary>
    public static string FirstRunScreenEmulated => Get();
    /// <summary>Étape 2.</summary>
    public static string FirstRunOrientationQuestion => Get();
    /// <summary>Étape 2, sous la question.</summary>
    public static string FirstRunOrientationHelp => Get();
    /// <summary>Étape 2 : le texte est à l'endroit, on continue.</summary>
    public static string FirstRunYes => Get();
    /// <summary>Étape 2 : bascule la rotation de l'écran.</summary>
    public static string FirstRunNoFlip => Get();
    /// <summary>Étape 3.</summary>
    public static string FirstRunModulesIntro => Get();
    /// <summary>Étape 4.</summary>
    public static string FirstRunClaudeIntro => Get();
    /// <summary>Étape 5.</summary>
    public static string FirstRunNetworkIntro => Get();
    /// <summary>Étape 6.</summary>
    public static string FirstRunWeatherIntro => Get();
    /// <summary>Étape 7.</summary>
    public static string FirstRunSensorsIntro => Get();
    /// <summary>Étape 8.</summary>
    public static string FirstRunStartupIntro => Get();
    /// <summary>Titre de section, dans Paramètres.</summary>
    public static string SettingsFirstRun => Get();
    /// <summary>Sous le titre de section.</summary>
    public static string SettingsFirstRunHelp => Get();
    /// <summary>Bouton.</summary>
    public static string SettingsFirstRunRestart => Get();
    /// <summary>Titre de section, dans Paramètres.</summary>
    public static string SettingsUpdates => Get();
    /// <summary>Nom du choix entre Stable et Bêta (et nom accessible de ses deux boutons).</summary>
    public static string UpdatesChannel => Get();
    /// <summary>Bouton du choix de version : seulement les versions stables.</summary>
    public static string UpdatesStable => Get();
    /// <summary>Bouton du choix de version : aussi les bêtas d'essai.</summary>
    public static string UpdatesBeta => Get();
    /// <summary>Bouton : cherche une mise à jour tout de suite.</summary>
    public static string UpdatesCheck => Get();
    /// <summary>Bouton. {0} : la version proposée.</summary>
    public static string UpdatesInstall => Get();
    /// <summary>Boîte du retour de la bêta à la stable. {0} : la version stable proposée.</summary>
    public static string UpdatesDowngrade => Get();
    /// <summary>Case de la boîte du retour en arrière : garde la bêta jusqu'à une stable plus récente.</summary>
    public static string UpdatesWaitForStable => Get();
    /// <summary>Bouton de la boîte du retour en arrière quand la case « attendre » est cochée : rien n'est installé.</summary>
    public static string UpdatesKeepBeta => Get();
    /// <summary>Sous un champ numérique ou de port. {0} : le minimum, {1} : le maximum, {2} : la valeur proposée.</summary>
    public static string ModulesNumberRange => Get();
    /// <summary>Sous un champ numérique ou de port quand la saisie ne convient pas. {0} : le minimum, {1} : le maximum.</summary>
    public static string ModulesNumberInvalid => Get();
    /// <summary>En-tête des réglages d'un module complémentaire, et nom du point d'exclamation jaune.</summary>
    public static string CommunityNotice => Get();
    /// <summary>Sous le nom d'un module complémentaire. {0} : l'auteur, {1} : la version.</summary>
    public static string CommunityAuthorVersion => Get();
    /// <summary>{0} : les noms des permissions, séparés par des virgules.</summary>
    public static string CommunityGranted => Get();
    /// <summary>Un module complémentaire sans aucune permission accordée.</summary>
    public static string CommunityNoneGranted => Get();
    /// <summary>Nom de la permission (identique à celui du service).</summary>
    public static string PermissionLocalNetwork => Get();
    /// <summary>Explication de la permission (identique à celle du service).</summary>
    public static string PermissionLocalNetworkHelp => Get();
    /// <summary>Nom de la permission (identique à celui du service).</summary>
    public static string PermissionInternet => Get();
    /// <summary>Explication de la permission (identique à celle du service).</summary>
    public static string PermissionInternetHelp => Get();
    /// <summary>Nom de la permission (identique à celui du service).</summary>
    public static string PermissionThisComputer => Get();
    /// <summary>Explication de la permission (identique à celle du service).</summary>
    public static string PermissionThisComputerHelp => Get();
    /// <summary>Nom de la permission (identique à celui du service).</summary>
    public static string PermissionSensors => Get();
    /// <summary>Explication de la permission (identique à celle du service).</summary>
    public static string PermissionSensorsHelp => Get();
    /// <summary>Nom de la permission (identique à celui du service).</summary>
    public static string PermissionNowPlaying => Get();
    /// <summary>Explication de la permission (identique à celle du service).</summary>
    public static string PermissionNowPlayingHelp => Get();
    /// <summary>Repère à côté d'une permission que la nouvelle version demande en plus.</summary>
    public static string PermissionNew => Get();
    /// <summary>Avertissement de la boîte d'installation, de validation et de mise à jour d'un module complémentaire.</summary>
    public static string DialogWarning => Get();
    /// <summary>Titre de la boîte d'installation.</summary>
    public static string DialogInstallTitle => Get();
    /// <summary>Titre de la boîte de validation d'un module trouvé dans le dossier des modules.</summary>
    public static string DialogApproveTitle => Get();
    /// <summary>Titre de la boîte de mise à jour.</summary>
    public static string DialogUpdateTitle => Get();
    /// <summary>Titre de la boîte qui change les permissions d'un module.</summary>
    public static string DialogPermissionsTitle => Get();
    /// <summary>{0} : l'auteur du module.</summary>
    public static string DialogAuthor => Get();
    /// <summary>{0} : la version du module.</summary>
    public static string DialogVersion => Get();
    /// <summary>Mise à jour. {0} : la version installée, {1} : la nouvelle.</summary>
    public static string DialogVersionUpdate => Get();
    /// <summary>Au-dessus des cases des permissions.</summary>
    public static string DialogPermissionsIntro => Get();
    /// <summary>Quand le manifeste ne déclare aucune permission.</summary>
    public static string DialogNoPermissions => Get();
    /// <summary>Mise à jour qui déclare une permission de plus.</summary>
    public static string DialogNewPermissions => Get();
    /// <summary>Titre des notes d'une version.</summary>
    public static string DialogNotes => Get();
    /// <summary>Pendant que le service télécharge et vérifie la mise à jour. {0} : la version.</summary>
    public static string DialogPreparing => Get();
    /// <summary>Pendant qu'une demande est en route.</summary>
    public static string DialogWorking => Get();
    /// <summary>Bouton de la boîte d'installation.</summary>
    public static string DialogInstall => Get();
    /// <summary>Bouton de la boîte de validation.</summary>
    public static string DialogApprove => Get();
    /// <summary>Bouton de la boîte des permissions.</summary>
    public static string DialogSave => Get();
    /// <summary>Titre de section, dans Paramètres.</summary>
    public static string SettingsCommunity => Get();
    /// <summary>Sous le titre de la section.</summary>
    public static string CommunityHelp => Get();
    /// <summary>Bouton : choisit un fichier .avmod.</summary>
    public static string CommunityInstallButton => Get();
    /// <summary>Titre du sélecteur de fichier.</summary>
    public static string CommunityPickTitle => Get();
    /// <summary>Nom du type de fichier .avmod.</summary>
    public static string CommunityPickType => Get();
    /// <summary>Section sans module.</summary>
    public static string CommunityNone => Get();
    /// <summary>Bandeau quand la liste scellée a été trouvée altérée.</summary>
    public static string CommunityTampered => Get();
    /// <summary>Un .avmod ouvert depuis un partage réseau : la fenêtre ne se connecte pas à un hôte, elle demande de copier le fichier.</summary>
    public static string PackageNetworkPath => Get();
    /// <summary>Un .avmod ouvert qui n'existe plus (ou dont le chemin est invalide).</summary>
    public static string PackageMissing => Get();
    /// <summary>Un .avmod dont l'emplacement ne permet pas de dire s'il est sur ce PC (lecteur de type inconnu, nom de périphérique).</summary>
    public static string PackageUnopenable => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateReady => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateNeedsApproval => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateChanged => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateNeedsNewerApp => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateTooOld => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateInvalid => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateUnavailable => Get();
    /// <summary>État d'un module complémentaire.</summary>
    public static string CommunityStateSetAside => Get();
    /// <summary>Précise l'origine d'un module chargé depuis un dossier de travail.</summary>
    public static string CommunityDevFolder => Get();
    /// <summary>Sur la ligne d'un module. {0} : la version proposée.</summary>
    public static string CommunityUpdateOffer => Get();
    /// <summary>Bouton de la ligne d'un module.</summary>
    public static string CommunityPermissions => Get();
    /// <summary>Bouton de la ligne d'un module à valider.</summary>
    public static string CommunityApprove => Get();
    /// <summary>Bouton de la ligne d'un module, et de la boîte de mise à jour.</summary>
    public static string CommunityUpdate => Get();
    /// <summary>Bouton de la ligne d'un module mis de côté.</summary>
    public static string CommunityRelaunch => Get();
    /// <summary>Bouton de la ligne d'un module, et de sa confirmation.</summary>
    public static string CommunityUninstall => Get();
    /// <summary>Confirmation avant de désinstaller un module.</summary>
    public static string CommunityUninstallConfirm => Get();
    /// <summary>Boîte d'installation : le paquet a des erreurs, dont aucune ne se lit une fois nettoyée.</summary>
    public static string DialogInvalidPackage => Get();
    /// <summary>Boîte de mise à jour : le service refuse sans dire pourquoi.</summary>
    public static string DialogPreparationFailed => Get();
    /// <summary>Titre de la zone du dossier des modules, dans Paramètres.</summary>
    public static string CommunityFolderTitle => Get();
    /// <summary>Sous le dossier des modules.</summary>
    public static string CommunityFolderHelp => Get();
    /// <summary>Bouton : choisit le dossier des modules.</summary>
    public static string CommunityFolderChange => Get();
    /// <summary>Titre du sélecteur de dossier.</summary>
    public static string CommunityFolderPickTitle => Get();
    /// <summary>Titre de la zone du mode développeur.</summary>
    public static string DeveloperTitle => Get();
    /// <summary>Sous l'interrupteur du mode développeur.</summary>
    public static string DeveloperHelp => Get();
    /// <summary>Bandeau en haut de la fenêtre tant que le mode développeur est allumé.</summary>
    public static string DeveloperBanner => Get();
    /// <summary>Titre de la liste des dossiers du mode développeur.</summary>
    public static string DeveloperFolders => Get();
    /// <summary>Liste des dossiers de travail vide.</summary>
    public static string DeveloperNoFolders => Get();
    /// <summary>Ligne d'un dossier de travail dont le module n'est pas dans la liste.</summary>
    public static string DeveloperNoModule => Get();
    /// <summary>Bouton : choisit le dossier de travail d'un module.</summary>
    public static string DeveloperAddFolder => Get();
    /// <summary>Titre du sélecteur de dossier.</summary>
    public static string DeveloperPickTitle => Get();
    /// <summary>Bouton d'une ligne : recharge le module avec les fichiers de son dossier.</summary>
    public static string DeveloperReload => Get();
    /// <summary>Bouton d'une ligne : retire le dossier de la liste (le dossier lui-même n'est pas touché).</summary>
    public static string DeveloperRemove => Get();
    /// <summary>Titre de la boîte qui ajoute un dossier de travail ou recharge son module.</summary>
    public static string DialogDevTitle => Get();
    /// <summary>Bouton de confirmation de la boîte d'un dossier de travail.</summary>
    public static string DialogDevAdd => Get();
    /// <summary>Titre de section, dans Paramètres.</summary>
    public static string ConsumptionTitle => Get();
    /// <summary>Sous le titre de la consommation.</summary>
    public static string ConsumptionHelp => Get();
    /// <summary>En-tête de colonne du tableau de consommation.</summary>
    public static string ConsumptionModule => Get();
    /// <summary>En-tête de colonne du tableau de consommation.</summary>
    public static string ConsumptionCpu => Get();
    /// <summary>En-tête de colonne du tableau de consommation.</summary>
    public static string ConsumptionMemory => Get();
    /// <summary>Repère orange d'un module qui consomme beaucoup depuis un moment.</summary>
    public static string ConsumptionHigh => Get();
    /// <summary>Processeur d'une ligne. {0} : le pourcentage, avec la virgule.</summary>
    public static string ConsumptionCpuValue => Get();
    /// <summary>Mémoire d'une ligne. {0} : les mégaoctets.</summary>
    public static string ConsumptionMemoryValue => Get();
    /// <summary>Bouton : replie le tableau de consommation (plus aucune mesure demandée).</summary>
    public static string ConsumptionHide => Get();
    /// <summary>Bouton : déplie le tableau de consommation.</summary>
    public static string ConsumptionShow => Get();
    /// <summary>Quand le service ne répond pas à la lecture de la consommation.</summary>
    public static string ConsumptionFailed => Get();
}
