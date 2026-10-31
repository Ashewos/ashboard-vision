; Installateur d'Ashboard Vision (Inno Setup 7.1 ou plus). On le construit avec installer/build.ps1, qui passe :
;   /DAppVersion=<semver>   /DSourceDir=<dossier publié>   /DOutputDir=<dossier de sortie>   [/DPawnIoSetup=<PawnIO_setup.exe>]

#ifndef AppVersion
  #error Passe la version : /DAppVersion=1.0.0 (installer/build.ps1 le fait).
#endif
#ifndef SourceDir
  #define SourceDir "publish"
#endif
#ifndef OutputDir
  #define OutputDir "out"
#endif

#define AppName "Ashboard Vision"
#define AppPublisher "Ashewos"
#define AppUrl "https://github.com/Ashewos/ashboard-vision"
; 1.0.0-beta.1 devient 1.0.0 pour la version numérique du fichier.
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)

[Setup]
; NE JAMAIS CHANGER : c'est l'identité de l'application. Un autre identifiant ferait deux installations côte à côte au lieu
; d'une mise à jour, et la désinstallation de l'une laisserait l'autre.
AppId={{111C6025-5DD9-4E18-8B24-7A08FDA9E7AA}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription=Installateur d'{#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Trofeo.Settings.exe

; Installateur 64 bits, en administrateur : l'assistant des capteurs (tâche SYSTEM) et PawnIO se règlent pendant l'installation.
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

; Windows ferme le service et la fenêtre s'ils tiennent des fichiers ; PrepareToInstall les arrête déjà avant (y compris ceux lancés depuis le dépôt).
CloseApplications=yes
RestartApplications=no

LicenseFile=..\LICENSE
SetupIconFile=..\assets\brand\ashboard-vision.ico

; Pages claires ou sombres selon le thème de Windows (Inno Setup 6.6 et plus), avec les images assorties.
; Chaque directive liste la taille 1x puis la 2x : Inno prend celle qui convient à l'écran.
WizardStyle=modern dynamic
WizardImageFile=..\assets\brand\assistant-clair.bmp,..\assets\brand\assistant-clair-2x.bmp
WizardImageFileDynamicDark=..\assets\brand\assistant-sombre.bmp,..\assets\brand\assistant-sombre-2x.bmp
WizardSmallImageFile=..\assets\brand\petit-logo-clair.bmp,..\assets\brand\petit-logo-clair-2x.bmp
WizardSmallImageFileDynamicDark=..\assets\brand\petit-logo-sombre.bmp,..\assets\brand\petit-logo-sombre-2x.bmp

Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=Ashboard-Vision-Setup-{#AppVersion}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "bureau"; Description: "Créer un raccourci sur le bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked
Name: "demarrage"; Description: "Démarrer {#AppName} avec Windows"; GroupDescription: "Démarrage :"
#ifdef PawnIoSetup
Name: "pawnio"; Description: "Installer le pilote des températures (PawnIO)"; GroupDescription: "Températures :"
#endif

[Files]
; Les trois programmes, les modules, les polices et leurs licences OFL, le mod des quotas, les licences.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef PawnIoSetup
; PawnIO passe par le dossier temporaire : l'assistant des capteurs le lance, puis Windows efface le dossier.
Source: "{#PawnIoSetup}"; DestDir: "{tmp}"; DestName: "PawnIO_setup.exe"; Tasks: pawnio; Flags: deleteafterinstall
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Trofeo.Settings.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Trofeo.Settings.exe"; Tasks: bureau

[InstallDelete]
; Une mise à jour retire d'abord les bibliothèques d'avant, avant de copier les nouvelles : celles qui n'existent plus ne restent pas.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.deps.json"
Type: files; Name: "{app}\*.runtimeconfig.json"

[Run]
; 1. L'assistant des capteurs : copie dans Program Files\ashboard-vision\sensors, tâche planifiée, retrait des anciens emplacements.
;    Avec PawnIO s'il est coché ; sans lui, l'assistant ne s'installe que si PawnIO est déjà là (sinon la commande ne change rien).
#ifdef PawnIoSetup
Filename: "{app}\Trofeo.SensorHelper.exe"; Parameters: "install --pawnio ""{tmp}\PawnIO_setup.exe"""; StatusMsg: "Installation de l'assistant des capteurs..."; Flags: runhidden waituntilterminated; Tasks: pawnio
Filename: "{app}\Trofeo.SensorHelper.exe"; Parameters: "install"; StatusMsg: "Installation de l'assistant des capteurs..."; Flags: runhidden waituntilterminated; Tasks: not pawnio
#else
Filename: "{app}\Trofeo.SensorHelper.exe"; Parameters: "install"; StatusMsg: "Installation de l'assistant des capteurs..."; Flags: runhidden waituntilterminated
#endif
; 2. Le service, sans élévation (le tuyau de contrôle n'accepte que l'utilisateur courant). Une seule entrée : la case « Lancer »
;    de la dernière page, et, en mode silencieux (mise à jour lancée par le logiciel), un lancement sans question.
Filename: "{app}\Trofeo.Display.exe"; Description: "Lancer {#AppName}"; Flags: nowait postinstall runasoriginaluser

[UninstallDelete]
; Ce que le programme a pu écrire à côté de lui (l'installateur y met options-installation.json), puis le dossier lui-même.
Type: filesandordirs; Name: "{app}"
; Le dossier parent de l'assistant des capteurs, s'il ne reste plus rien dedans (ses fichiers sont retirés par « uninstall »).
Type: dirifempty; Name: "{autopf}\ashboard-vision"

[Code]
const
  ServiceExe = 'Trofeo.Display.exe';
  SettingsExe = 'Trofeo.Settings.exe';
  HelperExe = 'Trofeo.SensorHelper.exe';

// Arrête tous les processus de ce nom, d'où qu'ils viennent. Un processus absent n'est pas une erreur.
procedure StopProcess(const ImageName: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ImageName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Le service et la fenêtre verrouillent leurs fichiers ; l'assistant des capteurs (tâche SYSTEM) aussi.
procedure StopAllPrograms;
begin
  StopProcess(ServiceExe);
  StopProcess(SettingsExe);
  StopProcess(HelperExe);
  Sleep(500); // le temps que Windows relâche les fichiers
end;

// Le choix de l'installateur, lu par le logiciel au premier lancement (src/Trofeo.Distribution/InstallOptions.cs).
procedure WriteInstallOptions;
var
  Value: String;
begin
  if WizardIsTaskSelected('demarrage') then
    Value := 'true'
  else
    Value := 'false';
  ForceDirectories(ExpandConstant('{app}'));
  if not SaveStringToFile(ExpandConstant('{app}\options-installation.json'), '{ "demarrerAvecWindows": ' + Value + ' }' + #13#10, False) then
    Log('options-installation.json n''a pas pu être écrit.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAllPrograms;
  WriteInstallOptions;
  Result := '';
end;

// Désinstallation : on arrête d'abord tout (le nettoyage de l'utilisateur ne fait rien si le service tient son mutex), puis le
// nettoyage propre à l'utilisateur (valeur Run, mod des quotas, question sur les réglages), puis l'assistant des capteurs
// (tâche et dossier). Tout cela avant la suppression des fichiers, dont ces programmes font partie.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Parameters: String;
begin
  if CurUninstallStep <> usUninstall then Exit;

  StopAllPrograms;

  Parameters := '--desinstaller-utilisateur';
  if UninstallSilent() then Parameters := Parameters + ' --silencieux';
  if not Exec(ExpandConstant('{app}\' + ServiceExe), Parameters, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    Log('Le nettoyage de l''utilisateur n''a pas pu être lancé : ' + SysErrorMessage(ResultCode))
  else if ResultCode <> 0 then
    Log('Le nettoyage de l''utilisateur a rendu le code ' + IntToStr(ResultCode));

  if not Exec(ExpandConstant('{app}\' + HelperExe), 'uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Le retrait de l''assistant des capteurs n''a pas pu être lancé : ' + SysErrorMessage(ResultCode))
  else if ResultCode <> 0 then
    Log('Le retrait de l''assistant des capteurs a rendu le code ' + IntToStr(ResultCode));
end;
