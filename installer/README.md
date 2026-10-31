# Installateur d'Ashboard Vision

Un assistant Inno Setup 7.1 (64 bits, en administrateur) : `Ashboard-Vision-Setup-<version>.exe`, avec son empreinte `.sha256`.

## Construire

```powershell
pwsh installer/build.ps1 -Version 1.0.0-beta.1
```

Le script cherche `ISCC.exe` (paramètre `-Iscc`, sinon `C:\Program Files\Inno Setup 7\ISCC.exe`, sinon le `PATH`) et refuse une version
inférieure à 7.1. Il écrit dans `installer/out/` (l'installateur, son `.sha256` et le journal `iscc.log`).

Il publie `Trofeo.Display`, `Trofeo.Settings` et `Trofeo.SensorHelper` (Release, `win-x64`, autonomes) dans `installer/publish/`, en passant
par `installer/artifacts/` (`--artifacts-path`) : les dossiers `bin` et `obj` des projets ne sont pas touchés, donc le service lancé depuis
`src/Trofeo.Display/bin/Release` n'est pas dérangé. Il y copie `mods/` (la version du mod ashboard-quotas y prend celle du logiciel : le
fichier du dépôt ne change pas), `LICENSE`, `LICENCES-TIERCES.txt` et les textes de licence des paquets. `installer/out/`,
`installer/publish/`, `installer/artifacts/` ne sont pas versionnés.

## Ce que fait l'installateur

- **Pages** : bienvenue, licence GPL-3.0, dossier (`Program Files\Ashboard Vision`), options, installation, fin avec « Lancer Ashboard Vision ».
  Les textes standard viennent de `French.isl`.
- **Options** : raccourci sur le bureau (décoché), démarrer avec Windows (coché), installer PawnIO (coché ; téléchargé pendant l'installation, connexion Internet requise).
- **Avant la copie** : arrête `Trofeo.Settings.exe` (d'abord : elle sait relancer le service), `Trofeo.Display.exe` et `Trofeo.SensorHelper.exe` (même ceux lancés depuis le dépôt), puis
  écrit `{app}\options-installation.json` (`{ "demarrerAvecWindows": true|false }`), que le service lit à son premier lancement.
- **Après la copie** (`CurStepChanged`, `ssPostInstall`) : `Trofeo.SensorHelper.exe install [--pawnio <PawnIO_setup.exe>]`, dont le code de sortie est journalisé (un message sobre signale un échec, sauf en mode silencieux ; le code 3 sans PawnIO est normal) (copie l'assistant dans
  `Program Files\ashboard-vision\sensors`, crée la tâche, retire les anciens emplacements), puis le service est lancé **sans élévation**
  (`runasoriginaluser`). En mode silencieux (mise à jour lancée par le logiciel), les options de la première installation sont gardées et le
  service est relancé sans question.
- **Désinstallation** : arrête les trois programmes, lance `Trofeo.Display.exe --desinstaller-utilisateur` (avec `--silencieux` si la
  désinstallation l'est : valeur `Run`, mod des quotas, question sur les réglages), puis `Trofeo.SensorHelper.exe uninstall` (tâche et
  dossier de l'assistant), puis supprime `options-installation.json` (le seul fichier que Setup ne suit pas) et le dossier d'installation s'il est vide (un dossier partagé n'est jamais effacé en bloc). PawnIO reste installé. Ces étapes sont dans le code de l'installateur
  (`CurUninstallStepChanged`), pour garantir l'ordre : arrêter d'abord, car le nettoyage ne fait rien si le service tient son mutex.

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

Dans le Bac à sable Windows, puis sur le PC d'Alex (jamais lancé depuis ce script) : pages en français avec le thème suivi, entrée dans
« Applications installées » (icône, éditeur Ashewos), service « déconnecté » sans écran, mise à jour silencieuse
(`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`), désinstallation sans tâche, sans valeur `Run` et sans
`Program Files\Ashboard Vision` ni `Program Files\ashboard-vision`.

Si Setup est lancé en « Exécuter en tant qu'administrateur » (clic droit), Windows ne peut pas revenir à l'utilisateur d'origine : le service
est alors lancé avec les droits élevés. Il faut lancer l'installateur normalement (double-clic).
