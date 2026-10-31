; Installateur d'Ashboard Vision (Inno Setup 7.1 ou plus). On le construit avec installer/build.ps1, qui passe :
;   /DAppVersion=<semver>   /DSourceDir=<dossier publié>   /DOutputDir=<dossier de sortie>
;   /DPawnIoUrl=<adresse officielle de PawnIO_setup.exe>   /DPawnIoSha256=<son empreinte SHA-256>   (lues dans pawnio.json)
; PawnIO n'est jamais embarqué : l'installateur le télécharge pendant l'installation (décision d'Alex, 31/10/2026).

#ifndef AppVersion
  #error Passe la version : /DAppVersion=1.0.0 (installer/build.ps1 le fait).
#endif
#ifndef PawnIoUrl
  #error Passe l'adresse de PawnIO : /DPawnIoUrl=... (installer/build.ps1 la lit dans pawnio.json).
#endif
#ifndef PawnIoSha256
  #error Passe l'empreinte de PawnIO : /DPawnIoSha256=... (installer/build.ps1 la lit dans pawnio.json).
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
Name: "pawnio"; Description: "Installer le pilote des températures (PawnIO), connexion Internet requise"; GroupDescription: "Températures :"

[Files]
; Les trois programmes, les modules, les polices et leurs licences OFL, le mod des quotas, les licences.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Trofeo.Settings.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Trofeo.Settings.exe"; Tasks: bureau

[InstallDelete]
; Une mise à jour retire d'abord les bibliothèques d'avant, avant de copier les nouvelles : celles qui n'existent plus ne restent pas.
; Seulement quand une installation d'Ashboard Vision est déjà là (un dossier choisi à la main, avec d'autres programmes, n'est pas vidé).
Type: files; Name: "{app}\*.dll"; Check: PreviousInstallExists
Type: files; Name: "{app}\*.deps.json"; Check: PreviousInstallExists
Type: files; Name: "{app}\*.runtimeconfig.json"; Check: PreviousInstallExists

[Run]
; L'assistant des capteurs s'installe dans CurStepChanged (ssPostInstall) : le code de sortie est journalisé et, sauf en mode
; silencieux, une panne est signalée. Le service, lui, est lancé sans élévation (le tuyau de contrôle n'accepte que l'utilisateur
; courant). Une seule entrée : la case « Lancer » de la dernière page, et, en mode silencieux (mise à jour lancée par le logiciel),
; un lancement sans question.
Filename: "{app}\Trofeo.Display.exe"; Description: "Lancer {#AppName}"; Flags: nowait postinstall runasoriginaluser

[UninstallDelete]
; Le seul fichier écrit à côté des programmes sans que Setup le suive : le choix de l'installateur (le service, lui, écrit dans
; %AppData%\ashboard-vision et dans le dossier temporaire). Puis le dossier lui-même, seulement s'il est vide : un dossier partagé reste.
Type: files; Name: "{app}\options-installation.json"
Type: dirifempty; Name: "{app}"
; Le dossier parent de l'assistant des capteurs, s'il ne reste plus rien dedans (ses fichiers sont retirés par « uninstall »).
Type: dirifempty; Name: "{autopf}\ashboard-vision"

[Code]
const
  PawnIoUrl = '{#PawnIoUrl}';
  PawnIoSha256 = '{#PawnIoSha256}';
  PawnIoFileName = 'PawnIO_setup.exe';
  PawnIoUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO'; // la même clé que PawnIoDetector (src/Trofeo.Sensors)
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
  StopProcess(SettingsExe); // d'abord : la fenêtre sait relancer le service
  StopProcess(ServiceExe);
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

// Vrai quand une installation d'Ashboard Vision est déjà dans le dossier choisi.
function PreviousInstallExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\' + ServiceExe));
end;

var
  PawnIoDownloaded: Boolean; // le fichier de PawnIO est dans {tmp}, vérifié par son empreinte

function OnPawnIoProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

// PawnIO n'est pas embarqué : s'il est demandé et pas déjà installé (une mise à jour silencieuse ne le retélécharge pas), il est
// téléchargé depuis son adresse officielle avec l'empreinte attendue, qu'Inno vérifie (un fichier différent est refusé).
// Un échec (pas d'Internet, empreinte différente) n'arrête pas l'installation : elle continue sans PawnIO, et la panne est dite
// (jamais en mode silencieux : journal seulement).
procedure DownloadPawnIo;
var
  Page: TDownloadWizardPage;
  Failure: String;
  Aborted: Boolean;
begin
  PawnIoDownloaded := False;
  Aborted := False;
  if not WizardIsTaskSelected('pawnio') then Exit;
  if RegKeyExists(HKLM64, PawnIoUninstallKey) then
  begin
    Log('PawnIO est déjà installé : rien à télécharger.');
    Exit;
  end;

  Failure := '';
  try
    if WizardSilent() then
      DownloadTemporaryFile(PawnIoUrl, PawnIoFileName, PawnIoSha256, nil)
    else
    begin
      Page := CreateDownloadPage('Téléchargement du pilote des températures', 'Ashboard Vision télécharge PawnIO depuis son site officiel.', @OnPawnIoProgress);
      Page.ShowBaseNameInsteadOfUrl := True;
      Page.Clear;
      Page.Add(PawnIoUrl, PawnIoFileName, PawnIoSha256);
      Page.Show;
      try
        try
          Page.Download;
        except
          // Le bouton Annuler de la page lève aussi une exception : ce n'est pas une panne de réseau.
          Aborted := Page.AbortedByUser;
          RaiseException(GetExceptionMessage);
        end;
      finally
        Page.Hide;
      end;
    end;
    PawnIoDownloaded := True;
    Log('PawnIO téléchargé et vérifié.');
  except
    Failure := GetExceptionMessage;
    Log('PawnIO n''a pas pu être téléchargé : ' + Failure);
  end;

  if (Failure <> '') and Aborted then
    MsgBox('Téléchargement de PawnIO annulé : l''installation continue sans le pilote des températures. '
      + 'Tu pourras l''installer plus tard depuis la fenêtre de réglage (Paramètres, Températures du processeur).', mbInformation, MB_OK)
  else if (Failure <> '') and not WizardSilent() then
    MsgBox('Le pilote des températures (PawnIO) n''a pas pu être téléchargé : vérifie ta connexion à Internet. '
      + 'L''installation continue sans lui. Tu pourras l''installer plus tard depuis la fenêtre de réglage '
      + '(Paramètres, Températures du processeur), avec l''installeur de PawnIO pris sur pawnio.eu.', mbInformation, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAllPrograms;
  WriteInstallOptions;
  DownloadPawnIo;
  Result := '';
end;

// L'assistant des capteurs : copie dans Program Files\ashboard-vision\sensors, tâche planifiée, retrait des anciens emplacements, et
// PawnIO si la case est cochée. Codes de sortie (src/Trofeo.SensorHelper/Program.cs) : 0 réussi, 3 PawnIO absent et pas d'installeur
// donné (normal quand la case est décochée), 4 échec de PawnIO, 5 erreur d'entrée-sortie, 1 et 2 usage ou droits.
procedure InstallSensorHelper;
var
  ResultCode: Integer;
  Parameters: String;
  Message: String;
begin
  Parameters := 'install';
  if PawnIoDownloaded then
    Parameters := Parameters + ' --pawnio "' + ExpandConstant('{tmp}\' + PawnIoFileName) + '"';

  Message := '';
  if not Exec(ExpandConstant('{app}\' + HelperExe), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('L''assistant des capteurs n''a pas pu être lancé : ' + SysErrorMessage(ResultCode));
    Message := 'L''assistant des capteurs n''a pas pu être lancé. Ashboard Vision marche, mais sans les températures du processeur.';
  end
  else
  begin
    Log('Assistant des capteurs (' + Parameters + ') : code de sortie ' + IntToStr(ResultCode));
    if ResultCode = 4 then
      Message := 'Le pilote des températures (PawnIO) n''a pas pu s''installer, donc l''assistant des capteurs non plus. Ashboard Vision marche, mais sans les températures du processeur. Tu peux relancer l''installateur pour réessayer.'
    else if (ResultCode <> 0) and (ResultCode <> 3) then
      Message := 'L''assistant des capteurs n''a pas pu s''installer (code ' + IntToStr(ResultCode) + '). Ashboard Vision marche, mais sans les températures du processeur. Tu peux relancer l''installateur pour réessayer.';
  end;

  if (Message <> '') and not WizardSilent() then
    MsgBox(Message, mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallSensorHelper;
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
