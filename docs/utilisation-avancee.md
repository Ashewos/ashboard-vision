# Utilisation avancée

Cette page s'adresse à ceux qui veulent aller plus loin que la fenêtre de réglage : régler les modules dans `layout.json`, choisir l'apparence en ligne de commande, brancher les quotas de Claude Code à la main, ou compiler le projet eux-mêmes. Pour installer et utiliser Ashboard Vision, le [README](../README.md) suffit.

Les réglages de l'utilisateur sont dans `%AppData%\ashboard-vision` : `layout.json` (disposition, thème, raccourcis) et `logs\display.log` (le journal). Pour retrouver la disposition par défaut, supprime `layout.json` pendant que le service est arrêté : il est recréé au prochain lancement (tes réglages de météo, de réseau ou de raccourcis sont alors à refaire). Le service s'arrête avec « Quitter », dans le menu de l'icône de la zone de notification.

## Régler les modules à la main

La fenêtre de réglage fait tout ce qui suit. Cette section sert à ceux qui préfèrent le fichier `layout.json`, service arrêté :

- **Météo** : dans les réglages du module Heure, `"location":{"name":"Lyon","latitude":45.76,"longitude":4.84}`, avec les coordonnées données par `--city`.
- **Codex à la place de Claude Code** : dans `Placements`, supprime l'objet dont `TypeId` vaut `"claudecode"` et ajoute celui-ci (sépare-le des autres objets par une virgule ; `InstanceId` est un GUID quelconque, par exemple celui que donne `[guid]::NewGuid()` dans PowerShell). Sans cette suppression, les deux modules se chevauchent et le service retombe sur la disposition par défaut, en gardant une copie `layout.json.bad`.

  ```json
  {
    "InstanceId": "7e0b1c52-3d4a-4f6e-8a19-5c2d9b3e6f40",
    "TypeId": "codex",
    "Column": 0,
    "Row": 0,
    "Columns": 8,
    "Rows": 4,
    "TextSize": "Normal",
    "Settings": { "view": "feed", "autoFollow": true }
  }
  ```
  Réglages du module Claude Code / Codex (tous facultatifs) :

  - `view` : `feed` (le fil d'activité, par défaut) ou `overview` (l'action en cours et les tâches) ;
  - `autoFollow` : `true` (par défaut) pour que l'onglet affiché passe à la session qui se met à t'attendre ;
  - `feedFade` : `true` pour que les lignes du fil pâlissent en vieillissant, `false` (par défaut) pour les garder toutes à pleine intensité ;
  - `feedNewest` : `top` (par défaut) pour que la ligne la plus récente soit en haut et que le fil descende, `bottom` pour qu'elle soit en bas et que le fil monte, comme un terminal.

  Le module Codex exclut les sessions techniques de sous-agents (dont `guardian`). Les onglets utilisent les titres de
  `~/.codex/session_index.jsonl` quand cet index existe ; sinon, la première demande lisible ou le dossier de travail.
  Les messages Desktop utilisateur et assistant apparaissent dans le fil, sous forme d'extraits d'une ligne, en plus des
  appels d'outils. Les instructions injectées et le raisonnement interne n'y sont pas affichés. La durée mesure le
  traitement de la demande et reste figée à sa fin ou à son interruption (ce n'est pas la durée d'une commande shell).
  Le module reste une vue des sessions locales récentes : au plus 12 conversations actives depuis moins de 2 h.
  Les dossiers des 3 derniers jours sont relevés chaque seconde ; tous les dossiers de dates sont parcourus une fois
  par minute environ pour retrouver une ancienne conversation reprise, qui est ensuite suivie chaque seconde. La fraîcheur
  combine la date de modification Windows et les horodatages JSONL ; la taille réelle du fichier est contrôlée pour
  détecter les ajouts quand Windows conserve une ancienne date. La lecture de fraîcheur est bornée aux 64 Kio de fin et
  mise en cache ; les liens de répertoire ne sont pas suivis. Ce n'est pas l'historique complet.
  Les commandes littérales encapsulées dans l'outil Desktop `exec` sont également lues (PowerShell, Bash, CMD et
  modifications de fichiers avec compteurs de lignes). Cette lecture ne réexécute jamais le JavaScript ni les commandes.
  Au démarrage, si le modèle est absent de la portion relue, le module recherche le dernier `turn_context` du même
  fichier (16 Mio au plus), sans rejouer son fil ni emprunter le modèle d'une autre conversation.
  Si les 512 Kio initiaux ne contiennent aucun message ni outil (par exemple après une grosse compression), le module
  récupère les derniers extraits du même fichier dans une fenêtre de 16 Mio au plus. Les lignes de plus de 1 Mio,
  les entrées de compression et le raisonnement ne sont pas affichés. Cette récupération unique ne modifie ni
  l'activité, ni la durée, ni les quotas ; les ajouts suivants restent incrémentaux, sans duplication du fil.
  Le contexte est calculé à partir de `last_token_usage.total_tokens` quand il est disponible (sinon `input_tokens`
  pour les anciens formats), rapporté à `model_context_window`. Après compression, le total conservé reste exploitable
  même si `input_tokens` vaut zéro ; le cumul de toute la conversation n'est jamais utilisé pour ce calcul.
  Pour Codex, « Contexte » affiche la part **utilisée** ; seuls les quotas « 5 h » / « Hebdo » affichent la part
  **restante**, barres comprises (30 % consommés donnent 70 % restants). Les heures de remise à zéro ne changent pas ; une valeur inconnue ou
  un quota périmé reste « — ». Le module Claude Code conserve son affichage de la part consommée.

- **Freebox** : dans les réglages du module Réseau, `"source":"freebox"`. Au premier lancement, le module demande d'appuyer sur la flèche de la Freebox pour autoriser Ashboard Vision.
- **UniFi** : `"source":"unifi","unifi":{"host":"192.168.1.1"}`, puis la clé API de la console avec `--set-secret network unifi.apiKey`.

## Raccourcis clavier

Des raccourcis globaux, actifs même quand une autre application a le focus, changent l'onglet affiché dans les modules Claude Code et Codex :

- `Ctrl+Alt+←` et `Ctrl+Alt+→` : l'onglet précédent ou suivant ;
- `Ctrl+Alt+Maj+1` à `Ctrl+Alt+Maj+9` : aller directement à l'onglet 1 à 9.

Maj est là exprès : sur un clavier AZERTY, `Ctrl+Alt+chiffre` est `AltGr+chiffre` et empêcherait de taper `~`, `#`, `{`, `[`, `|`, `` ` ``, `\` et `^`. Une combinaison déjà prise par une autre application est simplement laissée de côté, et notée dans le journal (`logs\display.log`). Tu peux changer les raccourcis, ou revenir à ceux-ci, dans l'onglet Raccourcis de la fenêtre de réglage.

## Vérifier l'écran en ligne de commande

L'outil `tools/Trofeo.HardwareCheck` sert à vérifier un écran, à régler l'apparence et à enregistrer des secrets sans la fenêtre. Il se construit avec `dotnet build -c Release` (voir « Compiler et lancer »). Ferme TRCC avant : un seul programme peut parler à l'écran.

- `--orientation 0` ou `--orientation 180` : affiche une mire pour trouver le bon sens ;
- `--fps` : mesure le débit réel ;
- `--recovery` : teste la reprise d'un écran figé ;
- `--set-secret <portée> <clé>` : enregistre un secret (mot de passe, jeton) dans le coffre du service. La valeur se lit sur l'entrée standard, saisie masquée au clavier, et n'est jamais affichée. Une valeur vide ou faite d'espaces est refusée (code de sortie 1) ;
- `--clear-secret <portée> <clé>` : efface un secret du coffre et affiche le nom de la clé effacée, jamais une valeur. Pour faire de nouveau confiance à une console UniFi dont le certificat a été renouvelé, efface son empreinte mémorisée avec `--clear-secret network unifi.certSha256.<hôte en minuscules>` (l'hôte de `layout.json` écrit en minuscules, avec son port s'il y en a un), puis relance le service : la prochaine connexion mémorise le nouveau certificat ;
- `--city <nom>` : affiche les cinq villes proposées par Open-Meteo, avec leurs coordonnées, pour régler la météo ;
- `--appearance [theme=<id>] [opacity=<0..1|theme>] [font=<famille|theme>] [background=theme|color:#hex|image:<chemin>|gif:<chemin>|video:<chemin>] [legibility=on|off|theme]` : choisit le thème, la transparence, la police, le fond d'écran et la garde de lisibilité (voir « Apparence »).

## Apparence

Trois thèmes sont fournis : `classique` (le thème par défaut), `liquid-glass` (panneaux de verre dépoli sur un fond « Aurore », un dégradé de halos colorés) et `pixel` (panneaux en pixel art, avec VT323, une police pixel lisible ; l'ancienne, Pixelify Sans, se choisit avec `font=Pixelify Sans`). Les réglages se font avec `Trofeo.HardwareCheck.exe --appearance`, que tu peux relancer autant de fois que tu veux : un réglage que tu ne cites pas reste comme il était. Après `dotnet build -c Release`, depuis la racine du dépôt, l'outil se trouve ici : `tools\Trofeo.HardwareCheck\bin\Release\net10.0-windows10.0.19041.0\Trofeo.HardwareCheck.exe`.

```powershell
Trofeo.HardwareCheck.exe --appearance theme=liquid-glass opacity=0.3 font="Segoe UI"
Trofeo.HardwareCheck.exe --appearance background=video:C:\Videos\fond.mp4
Trofeo.HardwareCheck.exe --appearance
```

- `theme=<id>` : `classique`, `liquid-glass` ou `pixel` ;
- `opacity=<0 à 1>` : l'opacité du fond des modules (0 : transparent, 1 : plein), ou `opacity=theme` pour celle du thème. **Écris le nombre avec un point** (`opacity=0.3`) : la commande accepte aussi la virgule (`opacity=0,3`) quand elle lui arrive entière, mais un terminal peut la couper en deux. Constaté avec Windows PowerShell 5.1 : un appel direct à l'exe (`Trofeo.HardwareCheck.exe --appearance opacity=0,3`) transmet `opacity=0,3` en un seul morceau et marche, mais `Start-Process -ArgumentList opacity=0,3` le coupe en `opacity=0` puis `3`. La commande refuse alors le `3` seul (code de sortie 2, rien n'est écrit) en te rappelant d'écrire un point ;
- `font=<famille>` : une police installée sur ton PC (par exemple `Segoe UI`), ou `font=theme` pour celle du thème. Si la police n'est pas installée, la commande te prévient et le service garde la police du thème, jusqu'à ce que tu l'installes ;
- `background=` : `theme` (le fond du thème), `color:#1A2B3C` (une couleur), `image:<chemin>` (une photo), `gif:<chemin>` (un GIF animé) ou `video:<chemin>` (une vidéo). Le fichier doit exister, son chemin est enregistré en entier ;
- `legibility=<on|off|theme>` : la garde de lisibilité (voir plus bas). `on` la garde active, `off` la coupe, `theme` remet le défaut (active) ;
- sans réglage, la commande affiche les réglages actuels (garde de lisibilité comprise), puis les polices installées.

Les réglages sont écrits dans `layout.json` (clé `Appearance`), sans toucher aux modules ni aux raccourcis. Une valeur refusée (thème inconnu, opacité hors de 0 à 1, fond inconnu…) donne un message et un code de sortie 2, sans rien écrire.

Deux choses à savoir :

- **Relance le service** après un changement : les réglages ne sont lus qu'au démarrage.
- **Lance la commande dans ton propre terminal**, pas dans celui d'un assistant : l'appli Claude installée depuis le Microsoft Store écrit `%AppData%` dans une copie privée, et le service ne verrait jamais tes réglages.

Un réglage faux ne bloque jamais le service : il retombe sur le thème Classique, la police ou le fond du thème, et le dit dans le journal.

### Garde de lisibilité

Le texte des modules est clair. Avec un fond clair (une photo lumineuse, un GIF ou une vidéo blanche) et une opacité basse, il devenait difficile à lire. La garde mesure la zone la plus claire du fond derrière chaque module et, si elle est trop claire, rend le panneau plus opaque, juste assez pour que le texte reste lisible. Elle n'abaisse jamais l'opacité que tu as choisie : sur un fond sombre, elle ne change rien.

Sur un fond animé (GIF, vidéo), l'opacité de chaque panneau change en douceur : elle monte en environ 1 seconde quand une zone claire arrive, et redescend en environ 15 secondes quand elle s'éloigne. Les panneaux ne clignotent donc pas au passage d'une tache claire.

Coupe-la avec `legibility=off` si le rendu ne te plaît pas : chaque panneau garde alors exactement l'opacité choisie (`opacity=`), quel que soit le fond. C'est le bon réglage si ton fond reste sombre, ou si tu préfères la transparence au texte toujours lisible (il peut alors se perdre sur les passages clairs). `legibility=on` ou `legibility=theme` la remet. Comme les autres réglages, il est lu au démarrage du service.

```powershell
Trofeo.HardwareCheck.exe --appearance legibility=off
```

### Fonds animés

- **Vidéo** : préfère une vidéo **à la taille de l'écran (1920 × 462)**, 30 images par seconde au plus : elle coûte jusqu'à deux fois moins de processeur et 40 % de mémoire en moins qu'une vidéo 1080p. Avec la mémoire du service (environ 40 à 50 Mo), une vidéo à la taille de l'écran reste sous les 100 Mo, une vidéo 1080p les dépasse (environ 110 à 125 Mo). Le décodeur lit chaque image de la vidéo, sans carte graphique (décodage logiciel) : plus la vidéo est lourde, plus le processeur travaille. La lecture boucle et n'a pas de son.
- **Coût du décodeur vidéo** : le décodeur n'utilise qu'un seul fil de travail (Windows en prend un par cœur logique par défaut, avec leurs tampons d'images : trois fois plus de mémoire pour le même débit de décodage). Mesuré par `tools/Trofeo.VideoBench` (le processus de lecture seul, 12 images par seconde montrées, 30 i/s lues, 60 s après 10 s de chauffe, sur 20 cœurs logiques) :

  | Vidéo (H.264, 30 i/s) | Mémoire propre | Processeur (un cœur) | Processeur (machine) |
  |---|---|---|---|
  | 1920 × 462 | 43 à 46 Mo | environ 20 à 28 % | environ 1,0 à 1,4 % |
  | 1920 × 1080 | 71 à 74 Mo | environ 42 % | environ 2,1 % |

  Une vidéo 1080p à 60 i/s et 40 Mb/s est encore lue en temps réel (marge d'environ 1,4), mais pas une vidéo 4K. Ces chiffres s'ajoutent à ceux du service sans vidéo.
- Quand l'écran revient après une absence (câble débranché, veille), la vidéo reprend là où elle en était, comme après une pause : elle ne rattrape pas le temps perdu.
- **GIF** : il boucle aussi. L'écran ne reçoit jamais plus de 12 images par seconde, quel que soit le fond. Une vidéo d'une seule image est traitée comme une image fixe.
- **Coût** : toutes les 100 images, le journal (`display.log`) indique le temps moyen de rendu et d'envoi, par exemple `Image 200 envoyée (164573 octets) ; rendu 41 ms, envoi 58 ms en moyenne`. Au-delà de 83 ms au total, 12 images par seconde ne sont plus tenues.
- **Remplacer un fichier pendant que le service le joue** :
  - **GIF** : le copier par-dessus ou le supprimer marche. Le déplacer ou le renommer par-dessus est refusé par Windows tant que le service tourne.
  - **Vidéo** : copier par-dessus une vidéo en cours de lecture est refusé par Windows (« le processus ne peut pas accéder au fichier »). Pour la remplacer, quitte le service (« Quitter » dans le menu de l'icône de la zone de notification), copie la nouvelle vidéo, puis relance le service. Seule la suppression marche pendant la lecture.
  - Dans les deux cas, le nouveau fichier n'est repris qu'au prochain démarrage.

## Quotas de Claude Code

Le module lit les sessions Claude Code sans rien installer. Pour afficher en plus les quotas (fenêtre de 5 h et hebdomadaire) et le remplissage exact du contexte, il faut que Claude Code écrive ses chiffres dans `~/.claude/ashboard-vision/statusline`, un fichier par session, que le module relit. Tant que rien n'y est écrit, le module affiche « Quotas indisponibles ».

C'est le rôle du mod `ashboard-quotas` (dans `mods/ashboard-quotas`). Il marche dans l'appli desktop **et** dans le terminal. À chaque mesure de Claude Code (après chaque réponse, ou quand un quota bouge d'un point), il écrit les chiffres de la session : rien d'autre n'est lu, aucune connexion n'est ouverte.

- **Avec le logiciel installé**, le mod s'installe depuis les réglages du module Claude Code (ou l'assistant du premier lancement), et il est réinstallé après chaque mise à jour. Il faut que la commande `claude` soit accessible depuis ton compte.
- **À la main**, depuis la racine du dépôt, dans **ton propre terminal** (c'est une modification des réglages de Claude Code, ce n'est pas à un assistant de la faire à ta place) :

```powershell
claude plugin marketplace add "$PWD\mods"
claude plugin install ashboard-quotas@ashboard-vision
```

La portée par défaut est « user » : le mod se charge alors dans toutes les nouvelles sessions, celles de l'appli desktop comprises. Il est lu directement dans `mods/ashboard-quotas` : après un `git pull`, `/reload-plugins` suffit. Pour le retirer : `claude plugin uninstall ashboard-quotas@ashboard-vision`.

Le mod ne voit que les réponses reçues sur ce PC : ce qui est consommé ailleurs (le chat sur le web, par exemple) n'apparaît qu'à la prochaine réponse locale.

L'ancienne barre d'état `Trofeo.StatusLine.exe` n'existe plus. Si tu l'avais branchée, le logiciel la retire au premier lancement et remet ta configuration précédente. L'ancien mod `trofeo-quotas` est remplacé de la même façon.

## Températures du processeur

Sans pilote, le module Monitoring affiche la ligne « Temp. CPU : installe le pilote ». Windows ne laisse lire ces températures qu'à un programme élevé, grâce à un pilote.

- **Avec l'installateur**, l'option « installer le pilote des températures (PawnIO) » installe PawnIO et l'assistant des capteurs. L'installateur les réinstalle à chaque mise à jour.
- **À la main** :
  1. installer PawnIO : `winget install --id namazso.PawnIO` ;
  2. ouvrir un terminal en administrateur et lancer `Trofeo.SensorHelper.exe install`. Après `dotnet build -c Release`, l'exécutable se trouve dans `src/Trofeo.SensorHelper/bin/Release/`, sous le dossier de la version de Windows ciblée ; l'option `--pawnio <installateur>` installe PawnIO au passage. Cela crée la tâche planifiée `\Ashboard Vision\Capteurs`, qui relève les capteurs à chaque ouverture de session et les transmet au service. Le service lui-même reste sans droits d'administrateur.

La ligne d'aide disparaît dès que la température arrive, et ne s'affiche pas quand l'assistant tourne déjà.

## Compiler et lancer

Il faut le [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0). La version exacte est fixée dans `global.json`. Le projet vise Windows (WinForms, WinRT, WinUSB) ; sous Linux, la plupart des projets de tests se construisent et tournent quand même (`dotnet test tests/<projet>`).

```bash
dotnet build -c Release
dotnet test
dotnet run -c Release --project src/Trofeo.Display
```

Pour travailler sur le code pendant que le service tourne, construis en `Debug` : le service installé verrouille ses fichiers de `Release`. Construire en `Release` demande d'arrêter le service et de fermer la fenêtre de réglage.

La branche de travail est `Canary` : les contributions (pull requests comprises) la visent. `main` ne reçoit que les versions stables.

### Les outils du dépôt

| Outil | Rôle | Commande |
|---|---|---|
| `tools/Trofeo.HardwareCheck` | Vérifie l'écran, règle l'apparence, enregistre des secrets | voir plus haut |
| `tools/Trofeo.ThemePreview` | Aperçus PNG de l'écran, un par thème, et une capture par module | `dotnet run --project tools/Trofeo.ThemePreview -- --out apercus` ; `-- --modules docs/images/modules` |
| `tools/Trofeo.SettingsPreview` | Aperçus de la fenêtre de réglage | `dotnet run --project tools/Trofeo.SettingsPreview -- --out apercus-reglages` |
| `tools/Trofeo.VideoBench` | Mesure le coût du décodeur vidéo des fonds | `dotnet run -c Debug --project tools/Trofeo.VideoBench -- <fichier> [--seconds 60] [--warmup 10]` |
| `tools/Trofeo.BrandAssets` | Fabrique les icônes, les images de l'installateur et le logo avec le nom en tracés dans `assets/brand` | `dotnet run --project tools/Trofeo.BrandAssets -- --logo docs/superpowers/logo --font src/Trofeo.Core/Assets/Fonts/Manrope-Medium.ttf --out assets/brand` |
| `tools/Trofeo.ReadmeHero` | Fabrique l'image d'accueil animée du README | `dotnet run --project tools/Trofeo.ReadmeHero -- --name "Ashboard Vision" --tagline "Le tableau de bord de ton écran Thermalright Trofeo Vision." --logo assets/brand/ashboard-vision-horizontal-sombre.svg --out apercus-accueil`, puis les commandes `ffmpeg` de `apercus-accueil/encode.txt` |

Le script de l'installateur (Inno Setup) et la recette GitHub Actions qui publie les versions sont décrits dans [le plan 6](superpowers/plans/2026-10-29-plan-6-distribution.md).

## Organisation du code

| Dossier | Rôle |
|---|---|
| `src/Trofeo.ModuleKit` | Le contrat public des modules |
| `src/Trofeo.Core` | Damier, disposition, rendu SkiaSharp, encodage JPEG |
| `src/Trofeo.Device` | Protocole USB de l'écran (WinUSB) |
| `src/Trofeo.Display` | Le service d'affichage, dans la zone de notification |
| `src/Trofeo.Service` | La liaison entre le service et la fenêtre de réglage |
| `src/Trofeo.Control` | Le canal de contrôle (tube nommé) entre la fenêtre et le service |
| `src/Trofeo.Settings` | La fenêtre de réglage (Avalonia) |
| `src/Trofeo.Distribution` | Versions, mises à jour depuis GitHub, déménagement des réglages, nettoyage |
| `src/Trofeo.Sensors`, `src/Trofeo.SensorHelper` | Les capteurs et leur assistant, qui tourne avec les droits d'administrateur |
| `src/Trofeo.Modules.*` | Les modules |
| `mods/ashboard-quotas` | Le mod Claude Code qui écrit les quotas, appli desktop comprise |
| `assets/brand` | Le logo, les icônes et les images de l'installateur |
| `docs/superpowers` | Conception, plans de développement et notes |

Les modules sont intégrés au logiciel. Pour en proposer un nouveau, ouvre une pull request vers `Canary` avec un projet `Trofeo.Modules.<Nom>` qui implémente `IModuleFactory` et `IModule`.
