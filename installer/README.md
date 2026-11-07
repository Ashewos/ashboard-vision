# Installateur d'Ashboard Vision

Un assistant Inno Setup 7.1 (64 bits, en administrateur) : `Ashboard-Vision-Setup-<version>.exe`, avec son empreinte `.sha256`.

## Construire

```powershell
pwsh installer/build.ps1 -Version 1.0.0-beta.1
```

Le script cherche `ISCC.exe` (paramètre `-Iscc`, sinon `C:\Program Files\Inno Setup 7\ISCC.exe`, sinon le `PATH`) et refuse une version
inférieure à 7.1. Il écrit dans `installer/out/` (l'installateur, son `.sha256` et le journal `iscc.log`).

Il publie `Trofeo.Display`, `Trofeo.Settings`, `Trofeo.SensorHelper` et `Trofeo.ModuleHost` (l'hôte des modules complémentaires ; Release, `win-x64`,
autonomes) dans `installer/publish/`, puis fabrique les quatre paquets du SDK des modules complémentaires (voir plus bas) avant de compiler, en passant
par `installer/artifacts/` (`--artifacts-path`) : les dossiers `bin` et `obj` des projets ne sont pas touchés, donc le service lancé depuis
`src/Trofeo.Display/bin/Release` n'est pas dérangé. Il y copie `mods/` (la version du mod ashboard-quotas y prend celle du logiciel : le
fichier du dépôt ne change pas), `LICENSE`, `LICENCES-TIERCES.txt` et les textes de licence des paquets. Il échoue si l'un des quatre `.exe`
(dont `Trofeo.ModuleHost.exe`) manque après la publication. `installer/out/`,
`installer/publish/`, `installer/artifacts/` ne sont pas versionnés.

**Le SDK des modules** : `build.ps1` lance `dotnet pack -c Release` (avec restauration : le `.nuspec` de `AshboardVision.Sdk` n'est sans dépendance
`Trofeo.ModuleKit` que si `pack` restaure) sur `AshboardVision.Sdk`, `AshboardVision.Sdk.Testing`, `AshboardVision.Sdk.Tool` et
`AshboardVision.Sdk.Templates`, dans `installer/publish/sdk/`. Il refuse de continuer s'il n'en sort pas exactement quatre `.nupkg` (un par
identifiant). Ces paquets portent **la version du SDK** (`1.0.0`, fixée dans leurs `.csproj`, à part de `Directory.Build.props`) : `-Version` ne leur est
jamais passé, et les fichiers s'appellent `AshboardVision.Sdk.1.0.0.nupkg`, etc. Une fois l'installateur compilé (`/DSdkDir=<ce dossier>`), le script
copie les quatre `.nupkg` à côté de lui, dans `installer/out/`. Ils ne sont **jamais poussés sur nuget.org** : la recette GitHub (`release.yml`) les joint à
la version, avec l'installateur et son `.sha256` (elle échoue s'il n'y en a pas quatre), et l'installateur les embarque pour la case « SDK ».

## Ce que fait l'installateur

- **Pages** : bienvenue, licence GPL-3.0, dossier (`Program Files\Ashboard Vision`), options, installation, fin avec « Lancer Ashboard Vision ».
  Les textes standard viennent de `French.isl`.
- **Options** : raccourci sur le bureau (décoché), démarrer avec Windows (coché), installer PawnIO (coché ; téléchargé pendant l'installation, connexion Internet requise),
  **outils pour créer des modules (SDK)** (décoché ; voir « Le SDK des modules » plus bas).
- **Avant la copie** : arrête `Trofeo.Settings.exe` (d'abord : elle sait relancer le service), `Trofeo.Display.exe`, `Trofeo.ModuleHost.exe` (l'hôte d'un module
  complémentaire, qui s'arrête normalement avec le service) et `Trofeo.SensorHelper.exe` (même ceux lancés depuis le dépôt), puis
  écrit `{app}\options-installation.json` (`{ "demarrerAvecWindows": true|false }`), que le service lit à son premier lancement.
- **Après la copie** (`CurStepChanged`, `ssPostInstall`) : `Trofeo.SensorHelper.exe install [--pawnio <PawnIO_setup.exe>]`, dont le code de sortie est journalisé (un message sobre signale un échec, sauf en mode silencieux ; le code 3 sans PawnIO est normal) (copie l'assistant dans
  `Program Files\ashboard-vision\sensors`, crée la tâche, retire les anciens emplacements). Puis, **sans élévation** (`runasoriginaluser`),
  la case « Lancer » ouvre la fenêtre de réglage, qui démarre le service s'il ne tourne pas (et, la première fois, son assistant), comme
  les raccourcis du menu Démarrer et du bureau. En mode silencieux (mise à jour lancée par le logiciel), les options de la première
  installation sont gardées et seul le service est relancé, sans question ; il rouvre la fenêtre si la mise à jour est partie d'elle.
- **Fichiers `.avmod`** : l'installateur associe l'extension `.avmod` (`ChangesAssociations=yes`, section `[Registry]`, clés sous `HKA\Software\Classes` : `.avmod`,
  `AshboardVision.Module` « Module Ashboard Vision », son icône, sa commande `"{app}\Trofeo.Settings.exe" "%1"`) ; un double-clic ouvre la fenêtre de réglage sur
  la boîte d'installation du module, qui montre ce qu'il demande, et rien ne s'installe sans le clic sur « Installer ». Les clés sont retirées à la désinstallation
  (`uninsdeletekey`). Aucune autre extension n'est associée.
- **Le SDK des modules** (tâche `sdk`, « Outils pour créer des modules (SDK) », décochée par défaut) :
  - **Fichiers** : les quatre `.nupkg` partent dans `{app}\sdk` (`[Files]`, `Tasks: sdk` ; la ligne générale écarte le dossier `sdk` de la publication avec
    `Excludes: "\sdk\*"`). Chaque installation efface d'abord les `.nupkg` d'avant (`[InstallDelete]`), et le dossier `sdk` s'il est vide.
  - **Branchement** (`InstallSdkTools`, à `ssPostInstall`), seulement si la case est cochée : `dotnet` est lancé **avec les droits de l'utilisateur d'origine**
    (`ExecAsOriginalUser`, jamais ceux de l'administrateur : NuGet.Config, modèles et outils sont dans son profil), à travers `cmd /s /c` dans `System32`. Inno Setup ne
    rend la sortie d'un programme qu'avec les droits de Setup (`ExecAndCaptureOutput`) ; on lit donc le code de sortie de
    `dotnet --list-sdks | findstr /B /C:10.` (un SDK `10.x` suffit). `dotnet` du `PATH` d'abord, sinon `Program Files\dotnet\dotnet.exe`. Absent, ou
    `--list-sdks` en échec : même chose que « pas de SDK .NET 10 ».
  - **Avec un SDK .NET 10**, dans cet ordre, chaque code de sortie journalisé : `dotnet nuget remove source AshboardVision` (échoue sans importance quand
    la source n'existe pas), `dotnet nuget add source "{app}\sdk" --name AshboardVision` (la source pointe donc toujours vers le dossier d'aujourd'hui, même
    après un changement de dossier), `dotnet new install "{app}\sdk\AshboardVision.Sdk.Templates.<version>.nupkg" --force` (depuis le fichier, rien à chercher sur Internet ;
    `--force` pour que la même version se réinstalle sans erreur), `dotnet tool update -g AshboardVision.Sdk.Tool --source "{app}\sdk"` (installe l'outil
    s'il est absent, ne fait rien s'il est déjà à jour ; `--source` le limite au dossier du SDK, donc rien n'est demandé à nuget.org et cela marche hors ligne :
    essayé, `--add-source` échoue quand nuget.org est injoignable, même avec `--ignore-failed-sources`).
  - **Sans SDK .NET 10** : seuls les fichiers sont copiés. Hors mode silencieux, une boîte dit « Installe le SDK .NET 10 pour créer des modules :
    https://dotnet.microsoft.com/download » et de relancer l'installateur ensuite ; en mode silencieux (mise à jour lancée par le logiciel), **aucune boîte**, le journal seul le dit.
  - **Mise à jour** : Inno Setup reprend les cases de l'installation d'avant (`UsePreviousTasks`), y compris en silencieux ; la case est donc cochée si le SDK était
    installé. Pour savoir si elle l'était, `PrepareToInstall` lit la valeur `Inno Setup: Selected Tasks` de la clé de désinstallation d'Inno Setup (avant que cette
    clé soit réécrite) ; si `sdk` y est et que la case est maintenant décochée, les mêmes retraits qu'à la désinstallation sont faits.
  - **Désinstallation** (`CurUninstallStepChanged`, après le nettoyage de l'utilisateur et le retrait de l'assistant des capteurs) : `dotnet tool uninstall -g AshboardVision.Sdk.Tool`,
    `dotnet new uninstall AshboardVision.Sdk.Templates`, `dotnet nuget remove source AshboardVision`, chacun tenté même si le précédent échoue, tout échec journalisé et jamais
    bloquant. **Limite** : `ExecAsOriginalUser` n'est pas pris en charge à la désinstallation (documentation d'Inno Setup 7.1) : les retraits sont lancés avec `Exec`, donc avec les
    droits de l'administrateur qui a validé l'élévation. C'est le même profil quand c'est le compte de l'utilisateur ; si un autre administrateur a saisi son mot de passe,
    l'outil, le modèle et la source restent dans le profil de l'utilisateur (à retirer à la main avec les trois commandes ci-dessus).
- **Journal** : l'installation note le téléchargement de PawnIO, le code de l'assistant des capteurs et, pour le SDK, la recherche de `dotnet` et le code de chaque commande ; la désinstallation, les codes de
  ses étapes (dont les trois retraits du SDK). Pour l'avoir : `Ashboard-Vision-Setup-<version>.exe /LOG="<fichier>"`.
- **Désinstallation** : arrête les programmes (dont les hôtes de modules), lance `Trofeo.Display.exe --desinstaller-utilisateur` (avec `--silencieux` si la
  désinstallation l'est : valeur `Run`, mod des quotas, modules complémentaires, question sur les réglages), puis `Trofeo.SensorHelper.exe uninstall` (tâche et
  dossier de l'assistant), puis supprime `options-installation.json` (le seul fichier que Setup ne suit pas) et le dossier d'installation s'il est vide (un dossier partagé n'est jamais effacé en bloc). PawnIO reste installé. Ces étapes sont dans le code de l'installateur
  (`CurUninstallStepChanged`), pour garantir l'ordre : arrêter d'abord, car le nettoyage ne fait rien si le service tient son mutex.
- **Modules complémentaires, à la désinstallation** (`CommunityCleanup`, appelé par le nettoyage de l'utilisateur avant l'effacement des réglages) :
  - toujours : le conteneur (`ashboard-vision.mod.<id>`) et la portée du coffre à secrets de chaque module connu de la liste scellée
    (`modules.json`) et de chaque dossier de développement (son identifiant est lu dans son manifeste, sans rien exécuter) ;
  - si tu choisis d'effacer tes réglages : aussi `modules.json`, `modules.key` et le dossier des modules **seulement s'il est le dossier par défaut**
    (`Documents\Ashboard Vision\Modules`, puis `Documents\Ashboard Vision` s'il est resté vide). Un dossier des modules que tu as choisi, même vide,
    et les dossiers de développement ne sont jamais supprimés. Un lien (jonction) dans ce dossier n'est pas suivi : seul le lien est retiré ;
  - la liste est lue **avant** toute suppression et n'est crue que si son sceau tient. Sans liste, le dossier des modules est le dossier par défaut. Avec un sceau
    faux ou une liste illisible, seuls les identifiants conformes à la règle d'identifiant servent (jamais un type d'origine ni un chemin) et le dossier des modules
    est laissé : son emplacement n'est pas sûr, à toi de le supprimer à la main ;
  - chaque étape est tentée même si la précédente échoue (un conteneur déjà parti ne bloque rien).

## Points vérifiés avant d'écrire (31/10/2026)

### 1. Licence de PawnIO : l'installeur n'est PAS embarqué, il est téléchargé pendant l'installation

Ce que disent les sources officielles :

- [github.com/namazso/PawnIO](https://github.com/namazso/PawnIO) (le code du pilote) : **GPL-2.0 ou ultérieure**, avec une exception qui
  autorise la combinaison avec des programmes libres et des modules qui ne parlent au pilote que par ses ioctl.
- [github.com/namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) : LGPL-2.1.
- [github.com/namazso/PawnIO.Setup](https://github.com/namazso/PawnIO.Setup), qui héberge `PawnIO_setup.exe` : **aucun fichier de licence**,
  seulement « This repository hosts PawnIO official releases ». C'est l'installeur signé.
- [pawnio.eu](https://pawnio.eu/) : l'édition officielle est « identique à la version open source, mais signée numériquement » ; le pied de
  page dit « © 2025 namazso. All rights reserved » ; rien ne dit si d'autres logiciels peuvent embarquer l'installeur ; une « licence
  commerciale » est proposée sur contact.

Conclusion : le code est libre (GPL-2.0+), mais l'**installeur signé** n'a pas de licence écrite, et le site réserve « tous droits ». La
redistribution de ce binaire précis est donc **douteuse, pas interdite**.

**Décision d'Alex (31/10/2026) : l'installateur ne l'embarque jamais ; il le télécharge pendant l'installation**, depuis l'adresse
officielle, et l'assistant dit qu'une connexion Internet est requise (« Installer le pilote des températures (PawnIO), connexion
Internet requise »). C'est une exception à « tout embarqué ».

Comment ça marche :

- `pawnio.json` donne la version (2.2.0), l'adresse officielle (`PawnIO_setup.exe` de cette version sur PawnIO.Setup) et l'empreinte SHA-256
  (`1f519a22…3032`, calculée sur le fichier téléchargé et identique à celle que GitHub affiche). `build.ps1` les lit, vérifie leur forme et les
  passe à ISCC (`/DPawnIoUrl`, `/DPawnIoSha256`). Rien n'est téléchargé à la construction.
- Si la case est cochée et que PawnIO n'est pas déjà installé (clé `UninstallPawnIO` du registre 64 bits, comme `PawnIoDetector`), Setup le
  télécharge dans son dossier temporaire avec `CreateDownloadPage` (`DownloadTemporaryFile` en mode silencieux). L'empreinte attendue est
  passée à l'appel : Inno refuse un fichier différent. Puis `Trofeo.SensorHelper.exe install --pawnio <fichier>`.
- Une mise à jour silencieuse sur une machine qui a déjà PawnIO ne retélécharge rien.
- **Échec** du téléchargement (pas d'Internet, empreinte différente) : l'installation continue sans PawnIO. Hors mode silencieux, un message
  dit de vérifier la connexion et renvoie vers la fenêtre de réglage (Paramètres, Températures du processeur, bouton « Installer le pilote
  des températures », qui demande l'installeur de PawnIO). En mode silencieux, journal seulement.
- Pour changer de version : modifier `pawnio.json` (version, adresse, empreinte).

### 2. Inno Setup suit le thème de Windows

Oui, depuis Inno Setup 6.6 (novembre 2025) ; la 7.1.1 est installée et fait l'affaire. Réglages utilisés :

- `WizardStyle=modern dynamic` : pages claires ou sombres selon le thème de Windows, lu une fois au lancement ;
- `WizardImageFile` / `WizardImageFileDynamicDark` et `WizardSmallImageFile` / `WizardSmallImageFileDynamicDark` : les images claires et
  sombres de `assets/brand/`, chacune avec sa version 2x (liste séparée par des virgules : Inno prend la taille qui convient).

Sources : `whatsnew.htm` et l'aide installée avec Inno Setup 7 (`ISetup.chm`), [jrsoftware.org/ishelp](https://jrsoftware.org/ishelp/).

À savoir sur Inno Setup 7 : le compilateur affiche « Non-commercial use only », et ses auteurs demandent d'acheter une licence
commerciale à qui s'en sert dans un cadre commercial (sa licence, `license.txt`, autorise tout usage). Ashboard Vision est un logiciel libre et
gratuit, donc sans objet, mais cela vaut d'être su.

## Points qui restent à essayer sur Windows

Dans le Bac à sable Windows, puis sur le PC d'Alex (jamais lancé depuis ce script) : **la case SDK** (décochée par défaut ; cochée sans SDK .NET → message ; cochée avec
SDK .NET → `dotnet new ashboard-module` et `ashboard-module` disponibles ; mise à jour silencieuse → case reprise ; désinstallation → outil, modèle et source retirés, avec
`Exec` à la place de `ExecAsOriginalUser`), pages en français avec le thème suivi, entrée dans
« Applications installées » (icône, éditeur Ashewos), service « déconnecté » sans écran, mise à jour silencieuse
(`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`), désinstallation sans tâche, sans valeur `Run` et sans
`Program Files\Ashboard Vision` ni `Program Files\ashboard-vision`.

Si Setup est lancé en « Exécuter en tant qu'administrateur » (clic droit), Windows ne peut pas revenir à l'utilisateur d'origine : le service
est alors lancé avec les droits élevés. Il faut lancer l'installateur normalement (double-clic).
