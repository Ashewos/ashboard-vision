; Installateur d'Ashboard Vision (Inno Setup 7.1 ou plus). On le construit avec installer/build.ps1, qui passe :
;   /DAppVersion=<semver>   /DSourceDir=<dossier publié>   /DOutputDir=<dossier de sortie>
;   /DPawnIoUrl=<adresse officielle de PawnIO_setup.exe>   /DPawnIoSha256=<son empreinte SHA-256>   (lues dans pawnio.json)
;   /DSdkDir=<dossier des quatre .nupkg du SDK des modules>   (facultatif : <SourceDir>\sdk par défaut)
; PawnIO n'est jamais embarqué : l'installateur le télécharge pendant l'installation (décision d'Alex, 31/10/2026).
; Le SDK .NET 10 non plus : si la case « SDK » est cochée et qu'il manque, l'installateur le télécharge chez Microsoft (installer/dotnet-sdk.ps1).

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
; Les quatre paquets du SDK (installer/build.ps1 les fabrique dans <SourceDir>\sdk). Ils ne sont pas copiés avec le reste (voir Excludes).
#ifndef SdkDir
  #define SdkDir SourceDir + "\sdk"
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

; L'association des fichiers .avmod (voir [Registry]) : Windows doit être prévenu pour la prendre en compte tout de suite.
ChangesAssociations=yes

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
; Décochée par défaut. Inno Setup reprend les choix de l'installation précédente (UsePreviousTasks, y compris en mise à jour silencieuse) :
; la case est donc cochée lors d'une mise à jour si le SDK était installé, décochée sinon.
Name: "sdk"; Description: "Outils pour créer des modules (SDK)"; GroupDescription: "Modules :"; Flags: unchecked

[Files]
; Les quatre programmes (service, fenêtre, assistant des capteurs, hôte des modules), les modules, les polices et leurs licences OFL, le mod des quotas, les licences.
; Le dossier sdk de la publication est écarté : ses paquets ne partent qu'avec la tâche « sdk » (ligne suivante).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\sdk\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; Les quatre paquets du SDK (AshboardVision.Sdk, .Sdk.Testing, .Sdk.Tool, .Sdk.Templates), pour la tâche « sdk » : voir InstallSdkTools.
Source: "{#SdkDir}\*.nupkg"; DestDir: "{app}\sdk"; Flags: ignoreversion; Tasks: sdk
; L'aide qui trouve le dernier SDK .NET 10 chez Microsoft et vérifie le fichier téléchargé (Windows PowerShell 5.1) : extraite à la demande, jamais copiée.
Source: "dotnet-sdk.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Registry]
; Un double-clic sur un module (.avmod) ouvre la fenêtre de réglage sur sa boîte d'installation (Trofeo.Settings.exe <chemin>) : elle montre
; ce que le module demande, et rien ne s'installe sans le clic sur « Installer ». Seule cette extension est associée. HKA : HKLM, puisque
; l'installateur est en administrateur ; les clés sont retirées à la désinstallation.
Root: HKA; Subkey: "Software\Classes\.avmod"; ValueType: string; ValueName: ""; ValueData: "AshboardVision.Module"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\AshboardVision.Module"; ValueType: string; ValueName: ""; ValueData: "Module {#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\AshboardVision.Module\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Trofeo.Settings.exe,0"
Root: HKA; Subkey: "Software\Classes\AshboardVision.Module\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Trofeo.Settings.exe"" ""%1"""

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Trofeo.Settings.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Trofeo.Settings.exe"; Tasks: bureau

[InstallDelete]
; Une mise à jour retire d'abord les bibliothèques d'avant, avant de copier les nouvelles : celles qui n'existent plus ne restent pas.
; Seulement quand une installation d'Ashboard Vision est déjà là (un dossier choisi à la main, avec d'autres programmes, n'est pas vidé).
Type: files; Name: "{app}\*.dll"; Check: PreviousInstallExists
Type: files; Name: "{app}\*.deps.json"; Check: PreviousInstallExists
Type: files; Name: "{app}\*.runtimeconfig.json"; Check: PreviousInstallExists
; Les paquets du SDK d'avant (la tâche « sdk » recopie ceux de cette version) ; le dossier part s'il est vide.
Type: files; Name: "{app}\sdk\*.nupkg"; Check: PreviousInstallExists
Type: dirifempty; Name: "{app}\sdk"; Check: PreviousInstallExists

[Run]
; L'assistant des capteurs s'installe dans CurStepChanged (ssPostInstall) : le code de sortie est journalisé et, sauf en mode
; silencieux, une panne est signalée. Tout est lancé sans élévation (le tuyau de contrôle n'accepte que l'utilisateur courant).
; La case « Lancer » de la dernière page ouvre la fenêtre de réglage, qui démarre le service (et, la première fois, son assistant).
; En mode silencieux (mise à jour lancée par le logiciel), seul le service repart ; il rouvre la fenêtre si elle était ouverte.
Filename: "{app}\Trofeo.Settings.exe"; Description: "Lancer {#AppName}"; Flags: nowait postinstall runasoriginaluser skipifsilent
Filename: "{app}\Trofeo.Display.exe"; Flags: nowait postinstall runasoriginaluser; Check: WizardSilent

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
  ModuleHostExe = 'Trofeo.ModuleHost.exe'; // lancé par le service, un par module complémentaire
  // Le SDK des modules complémentaires (tâche « sdk »).
  SdkSourceName = 'AshboardVision'; // le nom de la source NuGet qui pointe vers {app}\sdk
  SdkTemplatesId = 'AshboardVision.Sdk.Templates';
  SdkToolId = 'AshboardVision.Sdk.Tool';
  DotnetDownloadUrl = 'https://dotnet.microsoft.com/download';
  // Le SDK .NET 10 téléchargé quand il manque : l'aide PowerShell (extraite dans {tmp}) trouve l'adresse et l'empreinte dans releases.json de
  // Microsoft, puis vérifie le fichier (empreinte SHA-512 et signature Authenticode de Microsoft). Aucune version ni empreinte n'est écrite ici.
  DotnetSdkScript = 'dotnet-sdk.ps1';
  DotnetSdkSetupName = 'dotnet-sdk-win-x64.exe';
  DotnetSdkResolveFile = 'dotnet-sdk-resolve.txt';
  DotnetSdkVerifyFile = 'dotnet-sdk-verify.txt';
  // La clé de désinstallation d'Inno Setup : elle garde les cases cochées la fois d'avant (valeur « Inno Setup: Selected Tasks »).
  // Même identifiant que AppId plus haut (qui ne change jamais), avec le suffixe _is1 de Setup.
  SetupUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{111C6025-5DD9-4E18-8B24-7A08FDA9E7AA}_is1';

// Arrête tous les processus de ce nom, d'où qu'ils viennent. Un processus absent n'est pas une erreur.
procedure StopProcess(const ImageName: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ImageName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Le service et la fenêtre verrouillent leurs fichiers ; l'assistant des capteurs (tâche SYSTEM) et les hôtes des modules aussi.
procedure StopAllPrograms;
begin
  StopProcess(SettingsExe); // d'abord : la fenêtre sait relancer le service
  StopProcess(ServiceExe);
  StopProcess(ModuleHostExe); // le service s'arrête avec ses hôtes (job object) ; ceci couvre un hôte resté seul
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

// Vrai si la case « sdk » était cochée à l'installation d'avant (Inno la note dans la clé de désinstallation : liste séparée par des virgules).
function SdkTaskWasSelected: Boolean;
var
  Tasks: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, SetupUninstallKey, 'Inno Setup: Selected Tasks', Tasks) then
    Result := Pos(',sdk,', ',' + Tasks + ',') > 0;
end;

var
  PawnIoDownloaded: Boolean; // le fichier de PawnIO est dans {tmp}, vérifié par son empreinte
  SdkWasSelected: Boolean; // la tâche « sdk » était cochée à l'installation d'avant (lue avant toute copie)
  DotnetCommand: String; // la commande qui a répondu pour « dotnet » : « dotnet » (PATH) ou le chemin complet entre guillemets
  DotnetAsOriginalUser: Boolean; // vrai à l'installation (utilisateur d'origine) ; faux à la désinstallation (Exec, mêmes droits que le désinstallateur)
  SdkSetupPath: String; // le setup du SDK .NET 10, téléchargé dans {tmp} et vérifié (empreinte et signature) ; vide sinon
  SdkSetupSha512: String; // l'empreinte SHA-512 de releases.json pour ce fichier, revérifiée juste avant le lancement
  SdkFailure: String; // une phrase pour l'utilisateur quand le SDK .NET 10 n'a pas pu être obtenu ou installé (la raison exacte est au journal) ; vide sinon

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

procedure DownloadDotnetSdk; forward; // défini avec le reste du SDK, plus bas

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAllPrograms;
  WriteInstallOptions;
  SdkWasSelected := SdkTaskWasSelected; // avant toute copie : la clé de désinstallation est réécrite à la fin de l'installation
  DownloadPawnIo;
  DownloadDotnetSdk;
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

// --- Le SDK des modules complémentaires (tâche « sdk ») ---------------------------------------------------------------------
// Les quatre paquets sont copiés dans {app}\sdk par [Files]. Reste à les brancher sur le compte de l'utilisateur (source NuGet, modèle
// de projet, outil « ashboard-module »), ce que fait la commande « dotnet » : elle est lancée avec les droits de l'utilisateur d'origine
// (ExecAsOriginalUser), jamais ceux de l'administrateur, car sa configuration (NuGet.Config, modèles, outils) est dans son profil.
// Inno Setup ne rend la sortie d'un programme (ExecAndCaptureOutput, ExecAndLogOutput) qu'avec les droits de Setup, pas ceux de
// l'utilisateur d'origine : on passe par cmd.exe, et la présence d'un SDK .NET 10 se lit au code de sortie de
// « dotnet --list-sdks | findstr ».
// ExecAsOriginalUser n'existe pas à la désinstallation (la documentation d'Inno Setup 7.1 le dit) : le désinstallateur lance les
// retraits avec Exec, donc avec les droits de l'administrateur qui a validé l'élévation. C'est le même profil quand c'est le compte
// de l'utilisateur ; si un autre administrateur a saisi son mot de passe, l'outil reste dans le profil de l'utilisateur (installer/README.md).
// Aucun échec ne bloque l'installation ni la désinstallation : chaque code de sortie est journalisé.
// Pas de délai limite par commande : Exec et ExecAsOriginalUser attendent la fin du programme (ewWaitUntilTerminated) et Inno Setup ne
// donne ni délai ni poignée de processus pour l'arrêter. Les commandes n'utilisent que le disque (source locale, fichier du modèle,
// --source) : la limite est écrite dans installer/README.md.

// « cmd /s /c "<ligne>" » dans {sys} (aucun global.json ni NuGet.Config voisin), avec les droits de l'utilisateur d'origine à l'installation.
function RunAsUser(const CommandLine: String; var ResultCode: Integer): Boolean;
var
  Parameters: String;
begin
  Result := False;
  Parameters := '/s /c "' + CommandLine + '"';
  try
    if DotnetAsOriginalUser then
      Result := ExecAsOriginalUser(ExpandConstant('{cmd}'), Parameters, ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
      Result := Exec(ExpandConstant('{cmd}'), Parameters, ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not Result then
      Log('Impossible de lancer « ' + CommandLine + ' » : ' + SysErrorMessage(ResultCode));
  except
    // ExecAsOriginalUser peut lever une exception si le Setup d'origine est mort : rien ne doit bloquer l'installation.
    Result := False;
    Log('Impossible de lancer « ' + CommandLine + ' » : ' + GetExceptionMessage);
  end;
end;

// Vrai si « <Dotnet> --list-sdks » liste au moins une ligne qui commence par <Prefix> (« 10. » pour un SDK .NET 10 ; « . » pour n'importe lequel).
function DotnetHasSdk(const Dotnet, Prefix: String): Boolean;
var
  ResultCode: Integer;
  Filter: String;
begin
  if Prefix = '.' then
    Filter := '/R /C:.'
  else
    Filter := '/B /C:' + Prefix;
  ResultCode := -1;
  Result := RunAsUser(Dotnet + ' --list-sdks | "' + ExpandConstant('{sys}\findstr.exe') + '" ' + Filter, ResultCode) and (ResultCode = 0);
  Log('« ' + Dotnet + ' --list-sdks » filtré par ' + Filter + ' : code de sortie ' + IntToStr(ResultCode));
end;

// Cherche un « dotnet » qui a un SDK (un SDK .NET 10 si Need10) : celui du PATH de l'utilisateur d'abord, puis celui de Program Files.
// Rend Faux (et DotnetCommand vide) si « dotnet » est absent ou si « dotnet --list-sdks » échoue.
function FindDotnet(const Need10: Boolean): Boolean;
var
  Prefix, ProgramFilesDotnet: String;
begin
  if Need10 then
    Prefix := '10.'
  else
    Prefix := '.';
  DotnetCommand := 'dotnet';
  Result := DotnetHasSdk(DotnetCommand, Prefix);
  if not Result then
  begin
    ProgramFilesDotnet := ExpandConstant('{commonpf64}\dotnet\dotnet.exe');
    if FileExists(ProgramFilesDotnet) then
    begin
      DotnetCommand := '"' + ProgramFilesDotnet + '"';
      Result := DotnetHasSdk(DotnetCommand, Prefix);
    end;
  end;
  if not Result then DotnetCommand := '';
end;

// « dotnet <Args> » avec la commande trouvée par FindDotnet ; le code de sortie est journalisé et rendu (-1 si la commande n'a pas pu être lancée).
function RunDotnet(const Args: String): Integer;
var
  ResultCode: Integer;
begin
  Result := -1;
  ResultCode := -1;
  if RunAsUser(DotnetCommand + ' ' + Args, ResultCode) then Result := ResultCode;
  Log('dotnet ' + Args + ' : code de sortie ' + IntToStr(Result));
end;

// Le fichier du paquet de ce nom dans {app}\sdk (la version est dans le nom : AshboardVision.Sdk.Templates.1.0.0.nupkg), ou vide.
function FindSdkPackage(const PackageId: String): String;
var
  Found: TFindRec;
begin
  Result := '';
  if FindFirst(ExpandConstant('{app}\sdk\' + PackageId + '.*.nupkg'), Found) then
  begin
    try
      Result := ExpandConstant('{app}\sdk\') + Found.Name;
    finally
      FindClose(Found);
    end;
  end;
end;

// --- Le SDK .NET 10 : téléchargé et installé quand il manque ---------------------------------------------------------------
// Seulement si la case « sdk » est cochée, qu'aucun SDK .NET 10 n'est là et que Setup n'est pas silencieux (une mise à jour lancée par le logiciel
// ne télécharge jamais plus de 200 Mo : le journal seul le dit). Inno Setup 7.1 ne lit pas le JSON, ne vérifie que des empreintes SHA-256 et ne
// contrôle pas les signatures Authenticode : l'aide installer/dotnet-sdk.ps1 (Windows PowerShell 5.1) lit releases.json, puis vérifie le fichier
// (SHA-512 de releases.json et signature de Microsoft). Le téléchargement lui-même est fait par Inno Setup (page de progression, bouton Annuler).
// Un échec à n'importe quelle étape ne lance rien, est journalisé et se dit par une phrase (SdkFailure) ; l'installation continue. Le SDK .NET n'est
// jamais retiré à la désinstallation : c'est un programme à part (« Applications installées »).

function OnDotnetSdkProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

// Lance l'aide PowerShell avec <Arguments> ; rend son code de sortie (-1 si elle n'a pas pu être lancée) et les lignes de son fichier de résultat.
function RunSdkHelper(const Arguments, ResultFile: String; var Lines: TArrayOfString): Integer;
var
  ResultCode: Integer;
  ScriptPath, ResultPath: String;
begin
  Result := -1;
  SetArrayLength(Lines, 0);
  ExtractTemporaryFile(DotnetSdkScript);
  ScriptPath := ExpandConstant('{tmp}\' + DotnetSdkScript);
  ResultPath := ExpandConstant('{tmp}\' + ResultFile);
  DeleteFile(ResultPath);
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" ' + Arguments + ' -Output "' + ResultPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Windows PowerShell n''a pas pu être lancé : ' + SysErrorMessage(ResultCode));
    Exit;
  end;
  Result := ResultCode;
  if not LoadStringsFromFile(ResultPath, Lines) then SetArrayLength(Lines, 0);
end;

// La valeur de la ligne « <Name>=… » du fichier de résultat, ou vide.
function SdkHelperValue(const Lines: TArrayOfString; const Name: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Pos(Name + '=', Lines[I]) = 1 then
    begin
      Result := Trim(Copy(Lines[I], Length(Name) + 2, Length(Lines[I])));
      Exit;
    end;
end;

// Pourquoi l'aide a refusé, pour le journal : sa ligne « error=… » (sinon le code de sortie).
function SdkHelperFailure(const Lines: TArrayOfString; const Code: Integer): String;
begin
  Result := SdkHelperValue(Lines, 'error');
  if Result = '' then Result := 'code de sortie ' + IntToStr(Code);
end;

// Cherche la dernière version du SDK .NET 10, la télécharge dans {tmp} et la vérifie. En cas de réussite, SdkSetupPath est le fichier à lancer ;
// sinon SdkSetupPath est vide et SdkFailure dit pourquoi, en une phrase.
procedure DownloadDotnetSdk;
var
  Page: TDownloadWizardPage;
  Lines: TArrayOfString;
  Url, Sha512, Version, FilePath: String;
  Code: Integer;
begin
  SdkSetupPath := '';
  SdkSetupSha512 := '';
  SdkFailure := '';
  if not WizardIsTaskSelected('sdk') then Exit;
  if WizardSilent() then
  begin
    Log('Mode silencieux : le SDK .NET 10 n''est ni cherché ni téléchargé.');
    Exit;
  end;
  DotnetAsOriginalUser := True;
  if FindDotnet(True) then
  begin
    Log('Un SDK .NET 10 est déjà installé : rien à télécharger.');
    Exit;
  end;

  Log('Pas de SDK .NET 10 : recherche de la dernière version chez Microsoft.');
  FilePath := ExpandConstant('{tmp}\' + DotnetSdkSetupName);
  Page := CreateDownloadPage('Téléchargement du SDK .NET 10', 'Ashboard Vision télécharge le SDK .NET 10 depuis le site de Microsoft (environ 220 Mo).', @OnDotnetSdkProgress);
  Page.ShowBaseNameInsteadOfUrl := True;
  try
    Page.Clear;
    Page.Show;
    try
      // 1. L'adresse et l'empreinte de la dernière version (releases.json de Microsoft).
      Page.SetText('Recherche de la dernière version du SDK .NET 10…', '');
      Page.ProgressBar.Style := npbstMarquee;
      Code := RunSdkHelper('-Resolve', DotnetSdkResolveFile, Lines);
      Page.ProgressBar.Style := npbstNormal;
      Url := SdkHelperValue(Lines, 'url');
      Sha512 := SdkHelperValue(Lines, 'sha512');
      Version := SdkHelperValue(Lines, 'version');
      if (Code <> 0) or (Url = '') or (Sha512 = '') then
      begin
        SdkFailure := 'Le SDK .NET 10 n''a pas pu être téléchargé.';
        RaiseException('La version à télécharger n''a pas pu être déterminée : ' + SdkHelperFailure(Lines, Code));
      end;
      Log('SDK .NET ' + Version + ' : ' + Url);

      // 2. Le téléchargement (pas d'empreinte SHA-256 à donner à Inno Setup : la SHA-512 est vérifiée à l'étape suivante).
      Page.SetText('Téléchargement du SDK .NET 10 ' + Version + '…', '');
      Page.Clear;
      Page.Add(Url, DotnetSdkSetupName, '');
      try
        Page.Download;
      except
        // Le bouton Annuler de la page lève aussi une exception : ce n'est pas une panne de réseau.
        if Page.AbortedByUser then
          SdkFailure := 'Le téléchargement du SDK .NET 10 a été annulé.'
        else
          SdkFailure := 'Le SDK .NET 10 n''a pas pu être téléchargé.';
        RaiseException(GetExceptionMessage);
      end;

      // 3. La vérification, avant tout lancement : l'empreinte vient du même serveur que le fichier, la signature de Microsoft est la vraie protection.
      Page.SetText('Vérification du fichier téléchargé…', '');
      Page.ProgressBar.Style := npbstMarquee;
      Code := RunSdkHelper('-Verify "' + FilePath + '" -Sha512 ' + Sha512, DotnetSdkVerifyFile, Lines);
      Page.ProgressBar.Style := npbstNormal;
      if Code <> 0 then
      begin
        DeleteFile(FilePath);
        SdkFailure := 'Le fichier du SDK .NET 10 téléchargé n''a pas passé la vérification (empreinte et signature de Microsoft) : il n''a pas été lancé.';
        RaiseException('Le fichier du SDK .NET 10 est refusé : ' + SdkHelperFailure(Lines, Code));
      end;
      SdkSetupPath := FilePath;
      SdkSetupSha512 := Sha512;
      Log('SDK .NET 10 téléchargé et vérifié (empreinte SHA-512 et signature de Microsoft).');
    finally
      Page.Hide;
    end;
  except
    SdkSetupPath := '';
    if SdkFailure = '' then SdkFailure := 'Le SDK .NET 10 n''a pas pu être téléchargé.';
    Log('Le SDK .NET 10 n''a pas été obtenu : ' + GetExceptionMessage);
  end;
end;

// Installe le SDK .NET 10 téléchargé, pour toute la machine (Setup est déjà administrateur) : codes 0, 3010 et 1641 = réussi (les deux derniers
// demandent un redémarrage, sans importance pour « dotnet »), tout autre code = échec. Rend Vrai si c'est réussi ; sinon SdkFailure le dit.
// Le fichier est revérifié juste avant d'être lancé : il est resté dans {tmp} pendant la copie des fichiers, et rien ne doit pouvoir le remplacer entre-temps.
function InstallDotnetSdk: Boolean;
var
  ResultCode: Integer;
  Lines: TArrayOfString;
begin
  Result := False;
  if not WizardSilent() then WizardForm.StatusLabel.Caption := 'Installation du SDK .NET 10…';
  try
    ResultCode := RunSdkHelper('-Verify "' + SdkSetupPath + '" -Sha512 ' + SdkSetupSha512, DotnetSdkVerifyFile, Lines);
    if ResultCode <> 0 then
    begin
      Log('Le fichier du SDK .NET 10 est refusé avant son lancement : ' + SdkHelperFailure(Lines, ResultCode));
      SdkFailure := 'Le fichier du SDK .NET 10 téléchargé n''a pas passé la vérification (empreinte et signature de Microsoft) : il n''a pas été lancé.';
      Exit;
    end;
    if not Exec(SdkSetupPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Log('Le setup du SDK .NET 10 n''a pas pu être lancé : ' + SysErrorMessage(ResultCode));
      SdkFailure := 'Le SDK .NET 10 n''a pas pu s''installer.';
      Exit;
    end;
  except
    Log('Le setup du SDK .NET 10 n''a pas pu être lancé : ' + GetExceptionMessage);
    SdkFailure := 'Le SDK .NET 10 n''a pas pu s''installer.';
    Exit;
  end;
  Log('Setup du SDK .NET 10 : code de sortie ' + IntToStr(ResultCode));
  Result := (ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1641);
  if not Result then SdkFailure := 'Le SDK .NET 10 n''a pas pu s''installer (code ' + IntToStr(ResultCode) + ').';
end;

// Branche les paquets du SDK sur le compte de l'utilisateur. Sans SDK .NET 10, rien n'est branché : seuls les fichiers sont copiés (un
// message le dit, sauf en mode silencieux où le journal suffit), et la prochaine installation ou mise à jour le fera.
// Rejouable sans erreur (mise à jour, même version déjà là, source qui existe déjà ou qui pointait un autre dossier) :
//   - source NuGet : retirée puis ajoutée, pour qu'elle pointe vers le {app}\sdk d'aujourd'hui ;
//   - modèle : installé depuis son fichier (rien à chercher sur Internet), avec --force pour remplacer la même version ;
//   - outil : « tool update -g », qui installe s'il est absent et ne fait rien s'il est déjà à jour ; --source (et non --add-source) le limite au
//     dossier du SDK : rien n'est demandé à nuget.org, donc rien à craindre hors ligne (essayé : avec --add-source, nuget.org injoignable
//     fait échouer la commande même avec --ignore-failed-sources).
procedure InstallSdkTools;
var
  SdkDirectory, Templates, Message: String;
  Found: Boolean;
begin
  DotnetAsOriginalUser := True;
  Found := FindDotnet(True);
  if (not Found) and (SdkSetupPath <> '') then
  begin
    // Le setup du SDK .NET 10 a été téléchargé et vérifié par PrepareToInstall : on l'installe, puis on cherche « dotnet » à nouveau. Le PATH de
    // Setup n'a pas changé : c'est le chemin Program Files\dotnet\dotnet.exe de FindDotnet qui répond.
    if InstallDotnetSdk then
    begin
      Found := FindDotnet(True);
      if Found then
        Log('SDK .NET 10 installé par Ashboard Vision (« dotnet » : ' + DotnetCommand + '). Il reste installé à la désinstallation.')
      else
      begin
        SdkFailure := 'Le SDK .NET 10 a été installé, mais « dotnet » ne le trouve pas encore : Windows demande peut-être de redémarrer.';
        Log('Le SDK .NET 10 est installé, mais « dotnet --list-sdks » n''a pas de ligne 10.x ensuite.');
      end;
    end;
  end;

  if not Found then
  begin
    Log('Pas de SDK .NET 10 (dotnet absent, ou « dotnet --list-sdks » sans ligne 10.x) : le modèle et l''outil du SDK ne sont pas installés.');
    Message := 'Installe le SDK .NET 10 pour créer des modules : ' + DotnetDownloadUrl + #13#10#13#10
      + 'Les paquets du SDK sont copiés dans ' + ExpandConstant('{app}\sdk') + '. Relance l''installateur une fois le SDK .NET 10 installé : '
      + 'il installera le modèle de projet et l''outil.';
    if SdkFailure <> '' then Message := SdkFailure + ' ' + Message;
    if not WizardSilent() then MsgBox(Message, mbInformation, MB_OK);
    Exit;
  end;

  Log('SDK .NET 10 trouvé (' + DotnetCommand + ') : installation du modèle et de l''outil des modules.');
  if not WizardSilent() then WizardForm.StatusLabel.Caption := 'Installation des outils pour créer des modules…';
  SdkDirectory := ExpandConstant('{app}\sdk');

  RunDotnet('nuget remove source ' + SdkSourceName); // échoue (sans importance) quand la source n'existe pas encore
  RunDotnet('nuget add source "' + SdkDirectory + '" --name ' + SdkSourceName);

  Templates := FindSdkPackage(SdkTemplatesId);
  if Templates = '' then
    Log('Le paquet ' + SdkTemplatesId + ' est introuvable dans ' + SdkDirectory + ' : le modèle n''est pas installé.')
  else
    RunDotnet('new install "' + Templates + '" --force');

  RunDotnet('tool update -g ' + SdkToolId + ' --source "' + SdkDirectory + '"');
end;

// Retire ce que InstallSdkTools a branché : l'outil, le modèle, la source. Tout est tenté, aucun échec ne compte (déjà retiré, « dotnet »
// absent, SDK parti depuis). Sert à la désinstallation (AtUninstall : Exec, voir plus haut) et à la mise à jour qui décoche la case « sdk »
// alors qu'elle était cochée (utilisateur d'origine).
procedure RemoveSdkTools(const AtUninstall: Boolean);
begin
  DotnetAsOriginalUser := not AtUninstall;
  if not FindDotnet(False) then
  begin
    Log('Pas de « dotnet » avec un SDK pour cet utilisateur : l''outil, le modèle et la source du SDK ne sont pas retirés.');
    Exit;
  end;
  RunDotnet('tool uninstall -g ' + SdkToolId);
  RunDotnet('new uninstall ' + SdkTemplatesId);
  RunDotnet('nuget remove source ' + SdkSourceName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;
  InstallSensorHelper;
  if WizardIsTaskSelected('sdk') then
    InstallSdkTools
  else if SdkWasSelected then
  begin
    Log('La tâche « sdk » était cochée à l''installation d''avant et ne l''est plus : retrait du modèle, de l''outil et de la source.');
    RemoveSdkTools(False);
  end;
end;

// Désinstallation : on arrête d'abord tout (le nettoyage de l'utilisateur ne fait rien si le service tient son mutex), puis le
// nettoyage propre à l'utilisateur (valeur Run, mod des quotas, modules complémentaires, question sur les réglages), puis l'assistant des capteurs
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
  else
    Log('Nettoyage de l''utilisateur (' + Parameters + ') : code de sortie ' + IntToStr(ResultCode));

  if not Exec(ExpandConstant('{app}\' + HelperExe), 'uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Le retrait de l''assistant des capteurs n''a pas pu être lancé : ' + SysErrorMessage(ResultCode))
  else
    Log('Retrait de l''assistant des capteurs : code de sortie ' + IntToStr(ResultCode));

  // Le SDK des modules : seulement les retraits du profil de l'utilisateur (les paquets de {app}\sdk partent avec les fichiers), et seulement
  // si le SDK a été installé : la case « sdk » est dans les choix que Setup a retenus (clé de désinstallation, encore là à ce stade), ou le
  // dossier {app}\sdk existe (clé illisible). Sans l'un ni l'autre, « dotnet » n'est pas lancé : une désinstallation ordinaire n'y touche pas.
  if SdkTaskWasSelected or DirExists(ExpandConstant('{app}\sdk')) then
    RemoveSdkTools(True)
  else
    Log('Le SDK des modules n''a pas été installé (ni case « sdk » retenue, ni dossier sdk) : aucun retrait.');
end;
