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
}
