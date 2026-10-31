# Trofeo Dashboard — document de conception

*24 octobre 2026. Rédigé avec Alex pendant une séance de conception. Les décisions d'Alex sont écrites telles quelles ; celles que j'ai prises sont marquées **(choix de Claude)** et peuvent être remises en question.*

## 1. Intention

Alex a un écran **Thermalright Trofeo Vision 9.16"** dans son PC. Il veut s'en servir comme **écran d'ambiance** : un coup d'œil suffit pour savoir ce que fait Claude Code, comment va le PC, quelle heure et quel temps il fait, ce qui passe sur la connexion et ce qu'il écoute. Le but premier est de **ne plus avoir à basculer vers l'appli Claude Code** pour savoir où en sont ses sessions.

Des amis d'Alex ont le même écran et veulent le projet. Ils n'utilisent pas forcément les mêmes outils (Codex au lieu de Claude Code, routeur Ubiquiti au lieu d'une Freebox). D'où un dashboard **modulaire et très personnalisable**, publié sur **GitHub** avec une belle présentation, et mis à jour en un clic.

Le logiciel remplace le logiciel officiel de Thermalright (TRCC) pour piloter l'écran.

## 2. Objectifs, non-objectifs, anti-objectifs

**Objectifs**
- Un seul `.exe` d'installation pour Windows, **sans aucune dépendance à installer à la main**.
- Un **logiciel de réglage** avec un **mode émulé** qui montre l'écran même s'il n'est pas branché.
- Des **modules** qu'on ajoute, retire, déplace et redimensionne librement sur un damier.
- Des modules prévus dès le départ : Claude Code, Codex, Monitoring PC, Heure/date/météo, Réseau, En écoute.
- Un **état visible pour chaque module** dans le logiciel : point gris = non configuré, vert = en ligne, rouge = hors ligne.
- Trois thèmes (Classique, Liquid Glass, Pixel) et un fond d'écran au choix : image, GIF ou vidéo.
- Une mascotte animée et variée pour Claude Code, qui montre son état.
- Une **mise à jour en un clic** depuis les releases GitHub.
- Ajouter un nouveau type de module plus tard ne doit pas toucher aux autres.

**Non-objectifs**
- Pas de commande tactile : l'écran n'est pas tactile. On interagit au clavier (raccourcis) et dans le logiciel.
- Pas de Linux ni de macOS.
- Pas d'autres modèles d'écran Thermalright pour l'instant. Le pilote doit quand même être isolé pour en ajouter plus tard **(choix de Claude)**.
- Pas de service en ligne ni de compte : tout tourne sur le PC.

**Anti-objectifs** (ce qui serait un échec même si « ça marche »)
- **Gourmand en ressources.** Alex a explicitement refusé une base Electron/navigateur pour cette raison. L'appli tourne toute la journée.
- **Un écran qui se fige** sans que le logiciel ne s'en remette tout seul.
- **Du texte illisible d'un coup d'œil.** L'écran est petit, haut de 462 px et vu de biais. La première maquette avait un texte trop petit dans le module Claude Code.
- **Une mascotte répétitive.** Il faut plusieurs animations par état.
- **Des étapes manuelles d'installation** (pilote à télécharger, runtime à installer…).

## 3. Contraintes matérielles (mesurées sur l'écran d'Alex)

| Élément | Valeur |
|---|---|
| Identifiant USB | `0416:5408` (« USBDISPLAY », Winbond) |
| Pilote Windows | **WinUSB, attaché automatiquement par Windows.** L'écran déclare lui-même l'identifiant compatible `USB\MS_COMP_WINUSB` (descripteur Microsoft OS), donc tout PC sous Windows 10/11 le prend en charge sans pilote à installer et sans Zadig. Vérifié sur l'écran d'Alex. |
| Taille réelle des images | **1920 × 462** (la fiche produit annonce 1920 × 480, à tort) |
| Format des images | JPEG |
| Débit maximum | **~12,4 images/s**. La limite vient de l'écran (~73 ms pour valider une image), pas de l'USB (~7 ms pour l'envoyer). Ce plafond ne dépend pas de la taille de l'image (32 Ko à 700 Ko). |
| Orientation | Sur le montage d'Alex, l'image doit être **tournée à 180°**. |
| Persistance | L'écran **repasse au logo Thermalright environ 3 s** après la dernière image reçue (constaté par Alex le 24/10/2026, après le plan 1). Il faut donc lui renvoyer une image au moins toutes les ~3 s, même si rien n'a changé. |
| Gel | Envoyer une image **sans attendre la validation** de la précédente fige l'écran. On l'a débloqué avec : réinitialisation USB, lecture des réponses en attente, `clear halt` sur l'endpoint de sortie, puis une nouvelle poignée de main. |
| Logiciel Thermalright | Il ne doit pas tourner en même temps : un seul programme à la fois peut prendre l'écran. |

Le protocole complet, vérifié sur l'écran d'Alex, est en annexe A.

Matériel d'Alex : Intel Core Ultra 7 265K, NVIDIA RTX 5070, Freebox. Ses amis ont un routeur Ubiquiti (UniFi).

## 4. Fonctionnement général

### 4.1 Base technique (choix de Claude, validé dans son principe par Alex)

- **Application native .NET** (version LTS actuelle), publiée en **self-contained** : aucun runtime à installer.
- **Rendu de l'écran avec Skia** (SkiaSharp), sans navigateur. Le même moteur dessine l'aperçu du logiciel : **l'aperçu émulé est exactement ce que l'écran affiche**, au pixel près.
- **Fenêtre de réglage en Avalonia**, qui repose aussi sur Skia.
- **Un seul produit installé**, tout en .NET : rendu, USB, capteurs (LibreHardwareMonitorLib), musique Windows (API WinRT `GlobalSystemMediaTransportControlsSessionManager`), réseau, météo. Pas de runtime ni de programme tiers à installer.
- Alex a refusé Electron parce qu'il embarque un navigateur, trop gourmand pour une appli qui tourne en permanence.

### 4.2 Les processus (choix de Claude, après relecture)

Pour l'utilisateur, il n'y a qu'un logiciel. En interne, il tourne en **trois processus** :

- **Le service d'affichage** (en fond, **sans droits administrateur**) :
  - une icône dans la zone de notification, à côté de l'horloge, et aucune fenêtre ;
  - il démarre avec Windows et gère le rendu, l'USB, les sources de données et les raccourcis clavier globaux.
- **Le logiciel de réglage** : un processus séparé, lancé depuis l'icône et fermé avec sa fenêtre. Sa mémoire est donc vraiment libérée à la fermeture, ce qu'Avalonia ne permet pas à l'intérieur d'un processus qui continue de tourner. **L'écran continue de fonctionner quand il est fermé.** Il récupère l'aperçu et l'état auprès du service d'affichage.
- **L'assistant capteurs** (uniquement si PawnIO est installé) :
  - un tout petit processus **avec droits administrateur**, qui ne fait que lire les capteurs et les transmettre au service d'affichage ;
  - il est **installé dans un dossier protégé** (Program Files), et c'est lui seul que lance la tâche planifiée élevée ;
  - **Raison :** lancer avec droits élevés un `.exe` que l'utilisateur peut modifier serait une faille. L'installateur (en administrateur, voir 9.1) le place dans `Program Files`, où seul un administrateur peut y toucher.
  - Sans PawnIO, rien ne tourne avec des droits élevés.

Séparer l'affichage du reste a un autre avantage : un plantage du logiciel de réglage ne fige jamais l'écran.

### 4.3 Sobriété (objectifs à mesurer, pas des promesses)
- Moins de **100 Mo** de mémoire et environ **1 % de CPU** en moyenne, en fond.
- **On ne redessine que ce qui change.** Sans changement, la dernière image (déjà encodée) est simplement **renvoyée une fois par seconde**, parce que l'écran repasse au logo au bout d'environ 3 s. Les 12 images/s ne servent que pendant les animations.
- Prévoir une mesure de mémoire et de CPU dans le logiciel (onglet Paramètres) pour vérifier ces objectifs **(choix de Claude)**.

### 4.4 Droits administrateur

Les températures du CPU demandent les droits administrateur et le pilote PawnIO (voir 9.2). Seul l'**assistant capteurs** (4.2) est lancé avec les droits les plus élevés, par une tâche planifiée à l'ouverture de session. Il n'y a qu'une validation, à l'installation du pilote, et aucune fenêtre « Autoriser » à chaque démarrage **(choix de Claude)**.

## 5. L'écran : damier et disposition

- L'écran est découpé en un **damier de 16 × 4 cases**, de 120 px de large sur environ 115 px de haut (462 / 4 = 115,5 : le découpage exact est laissé au constructeur).
- Un module occupe toujours un **rectangle de cases entières**.
- **Disposition d'Alex** :
  - Claude Code : 8 × 4, colonnes 1 à 8.
  - Monitoring PC : 4 × 2, colonnes 9 à 12, en haut.
  - En écoute : 4 × 2, colonnes 9 à 12, en bas.
  - Heure/date/météo : 4 × 2, colonnes 13 à 16, en haut.
  - Réseau : 4 × 2, colonnes 13 à 16, en bas.
- Les cases vides sont transparentes et laissent voir le fond d'écran.
- Le même module peut être placé deux fois : par exemple deux modules Claude Code, ou un Claude Code et un Codex **(choix de Claude)**.

## 6. Les modules

### 6.1 Règles communes

- **Bibliothèque de modules.** On en ajoute à l'écran, on en retire. Un module retiré disparaît de l'écran mais garde ses réglages.
- **Architecture en modules.** Chaque type de module est une unité indépendante : ses données, ses réglages, son dessin selon sa taille, sa taille minimum, ses tailles toutes faites et son état. Ajouter un type de module ne modifie pas les autres.
  - **Ajout de modules (recommandation de Claude, validée par Alex)** :
    - Les modules sont **intégrés à l'appli**, mais écrits contre une **interface de module publique**, rangée dans un projet séparé (un petit « kit de module »).
    - Les amis ajoutent un module en le proposant sur le dépôt GitHub. Après relecture, il arrive chez tout le monde avec la mise à jour en un clic.
    - Le chargement de modules externes (fichiers `.dll` déposés dans un dossier) est **prévu par cette interface mais pas construit** dans la première version.
    - Raisons :
      - un module externe est du code exécuté sans relecture sur le PC ;
      - il casserait à chaque changement interne de l'appli ;
      - il demande un système de chargement et de compatibilité.
    - Avec l'interface en place dès le départ, on pourra l'ajouter plus tard sans réécrire les modules.
- **Redimensionnement** en tirant un bord ou un coin, avec calage sur les cases. Le module se déplace en le glissant.
- **Tailles toutes faites** : Compact, Idéal, Large. Chaque module a une **taille minimum** sous laquelle on ne peut pas descendre.
- **Pas de chevauchement.** Un module ne peut pas être agrandi ni déplacé sur un autre : la zone s'affiche en rouge et c'est refusé **(choix de Claude ; la maquette montrait un module qui pousse son voisin, mais un refus est plus simple et plus prévisible)**.
- **Contenu au choix.** Chaque module propose la liste de ce qu'il peut afficher, et on coche ce qu'on veut. Si on coche plus que la taille ne permet d'afficher, le logiciel le signale.
- **Le contenu s'adapte à la taille.** Plus petit, on garde l'essentiel ; plus grand, on en montre plus.
- **Taille du texte réglable pour chaque module** (−, normal, +). Les valeurs par défaut doivent être lisibles d'un coup d'œil.
- **État** : non configuré (gris), en ligne (vert), hors ligne (rouge). Visible dans la liste des modules du logiciel.
- **Action à faire affichée sur l'écran.** Quand un module attend une action pour être configuré, il l'écrit lui-même dans sa zone. Exemple : « Appuie sur ► sur ta Freebox pour autoriser le dashboard ».

### 6.2 Claude Code

C'est le module le plus important. Il affiche **les infos du harness** de Claude Code.

**Disposition (validée sur maquette)**
- **Onglets ancrés à gauche**, un par session Claude Code. Chaque onglet montre :
  - le **nom du projet** (le nom du dossier de travail), tronqué avec « … » s'il est trop long pour ne pas prendre de place ;
  - un **point d'état** : orange = travaille, jaune = attend l'utilisateur, gris = inactif.
- **Raccourcis clavier globaux et personnalisables** pour changer d'onglet. Par défaut : Ctrl+Alt+← / → et Ctrl+Alt+Maj+1…9 **(choix de Claude ; Ctrl+Alt+chiffre est AltGr+chiffre sur un clavier AZERTY, il volerait ~ # { [ | ` \ ^)**. L'onglet affiché n'a pas d'effet sur l'appli Claude Code elle-même.
- **Flux en direct** (mode par défaut) : en haut, une ligne de titre avec la tâche en cours, puis les dernières actions (outil + cible : Read, Edit avec lignes ajoutées/retirées, Bash, Grep…). Les plus anciennes s'estompent progressivement.
- **Barre du bas** : la **mascotte en petit**, **sans texte d'état à côté** (Alex l'a demandé ; l'état se lit à son animation et au point de l'onglet). Puis Contexte (%), Durée, Modèle, et à droite les quotas **5 h** et **hebdo**, chacun avec une barre et l'heure du prochain reset. Les quotas sont liés au compte, ils ne changent pas d'un onglet à l'autre.
- **Option « Vue d'ensemble »** dans les réglages du module : à la place du flux, l'action en cours sur une ligne, la liste de tâches avec la progression, puis la même barre du bas.
- La taille du texte a été augmentée d'environ 35 % par rapport à la première maquette, et Alex a validé le résultat sur l'écran. La maquette de référence est `.superpowers/brainstorm/…/content/themes-v3.html`.

**La mascotte**
- C'est le personnage pixel de Claude, fait uniquement de rectangles. Elle est animée par un **petit moteur d'animation maison** qui combine :
  - des **images successives**, chacune avec sa durée d'affichage ;
  - des **mouvements interpolés** avec courbes d'accélération.
- Référence : l'article Codrops « Reverse-Engineering Claude AI's Mascot Animations with SVG and GSAP ». On ne prend pas GSAP (navigateur), mais le principe de l'article.
- **Correspondance avec les états** (proposée par Claude, non contestée par Alex) :

  | État | Animation |
  |---|---|
  | Inactif | Elle se balade et regarde autour d'elle |
  | Travaille | Salle de sport (haltère) |
  | Attend l'utilisateur | Elle agite un **drapeau jaune** (pas le damier de l'original, pour rester cohérent avec le point jaune) |
  | Tâche terminée | Confettis, joués une fois |

- **Plusieurs variantes par état**, tirées au hasard, avec des pauses de durée variable pour éviter la répétition.
- Les animations sont conçues pour **≤ 12 images/s**. Les animations de l'article tournent déjà entre 8 et 12 images/s (85 à 125 ms par image), elles collent donc bien à l'écran.

**Sources de données** (vérifiées sur le PC d'Alex, Claude Code 2.1.308)
- **Alex utilise surtout l'appli desktop de Claude Code.** Ses sessions apparaissent bien dans les fichiers ci-dessous (`"entrypoint": "claude-desktop"`). Le module doit marcher pour l'appli desktop **et** pour le CLI dans un terminal.
- `~/.claude/sessions/<pid>.json` : sessions en cours, avec `sessionId`, `cwd`, `name`, `status` (par exemple `busy`), `startedAt`, `updatedAt`, `entrypoint`.
- **Nom du projet dans l'onglet** : le nom du dossier du dépôt. Si la session tourne dans un *worktree*, prendre le nom du dépôt principal, pas celui du dossier temporaire du worktree **(choix de Claude)**.
- `~/.claude/projects/<dossier>/<sessionId>.jsonl` : la transcription, avec les appels d'outils, les messages et l'usage de tokens. On en tire le flux, la tâche en cours et le contexte utilisé.
- **Quotas 5 h / hebdo** : Claude Code les transmet à la commande de barre d'état (`statusLine`) dans un objet `rate_limits` (`five_hour`, `seven_day`, `used_percentage`, `resets_at`), pour les abonnés Pro/Max seulement. Ce n'est pas documenté officiellement et le champ manque parfois. Pour les récupérer, l'appli doit installer une petite commande de barre d'état qui écrit ces données dans un fichier. Si l'utilisateur a déjà une barre d'état, elle doit l'appeler à la suite. **Cette installation modifie les réglages Claude Code de l'utilisateur : elle doit être proposée et acceptée dans l'assistant, et réversible (choix de Claude).**
  - **Risque à lever en premier** : rien ne garantit que l'appli desktop exécute la commande de barre d'état. Le constructeur le vérifie au tout début du projet, avant de construire le module.
  - **Repli décidé par Alex** : si la barre d'état ne tourne pas, les quotas ne s'affichent que pour les sessions qui la font tourner, par exemple le CLI dans un terminal. Pour les autres, la zone des quotas indique « indisponible ».
  - **Interdit** : lire le jeton de connexion Claude Code pour interroger une adresse d'Anthropic. Alex ne voulait de cette option que si les conditions d'utilisation l'autorisent. Or la documentation légale de Claude Code réserve les jetons des abonnements Free/Pro/Max à Claude Code et Claude.ai : leur usage par un autre outil viole les conditions d'utilisation grand public.
  - **Quotas de la dernière valeur connue** : on les garde avec leur heure de reset, puisqu'ils sont liés au compte et pas à la session. Une session terminal suffit donc à les rafraîchir pour tous les onglets **(choix de Claude)**.
- **État « attend l'utilisateur »** : à déduire du `status` de la session si celui-ci le distingue. Sinon, passer par un hook `Notification` de Claude Code, installé avec le même consentement que ci-dessus. C'est au constructeur de vérifier les valeurs réelles de `status`.
- **Durée** : temps écoulé depuis le dernier message de l'utilisateur dans la session, c'est-à-dire la tâche en cours **(choix de Claude)**.

### 6.3 Codex

- Le **même module que Claude Code**, avec la même disposition, la même taille idéale (8 × 4) et les mêmes options, mais pour les sessions **Codex**.
- Source : `~/.codex/sessions/` (transcriptions JSONL). Les quotas se trouvent dans les événements `token_count` → `rate_limits.primary` / `secondary` (`used_percent`, `window_minutes` 300 ou 10080, `resets_at`). Ces infos viennent de la communauté : le constructeur les vérifiera sur une vraie installation Codex chez un ami d'Alex, puisqu'Alex n'a pas Codex.
- **Mascotte de Codex** : le « pet » officiel de Codex, choisi par Alex. C'est un petit robot bleu en forme de nuage, dont le visage est un écran sombre avec `>_`. La référence visuelle est l'article note.com « Are Codex Pets a Revival of 2000s Desktop Mascots? ». On le **redessine en pixel art**, avec des rectangles, dans le même style et avec le même moteur que la mascotte de Claude. Correspondance des états, d'après les poses officielles **(choix de Claude)** :

  | État | Animation |
  |---|---|
  | Inactif | Il dort (zzz) ou fredonne (note de musique) |
  | Travaille | Il tape sur un ordinateur portable |
  | Attend l'utilisateur | Un point d'interrogation ou d'exclamation **jaune** au-dessus de la tête |
  | Tâche terminée | Cœurs et étincelles, joués une fois |

### 6.4 Monitoring PC

- **Titre** : texte libre. Par défaut, la vraie référence matérielle détectée, avec le nom complet du CPU. Pour Alex : « Core Ultra 7 265K · RTX 5070 ».
- **Valeurs par défaut** (taille Compact 4 × 2) : CPU %, température CPU, GPU %, température GPU, RAM utilisée / totale. Les **températures s'affichent aussi grandes que les pourcentages** (Alex les trouvait trop petites).
- **Valeurs supplémentaires à cocher** : fréquences, puissance, ventilateurs, VRAM, disques, etc. Tout ce que LibreHardwareMonitor expose de façon fiable.
- Le GPU NVIDIA se lit sans droits administrateur. Les températures du CPU demandent PawnIO et les droits administrateur. Sans eux, le module affiche les autres valeurs et explique en une ligne comment activer les températures.

### 6.5 En écoute

- Source : API média de Windows (SMTC), sans configuration. Fonctionne avec Spotify et la plupart des lecteurs.
- Affiche : pochette, titre, artiste, nom de l'appli source, progression et durées, état lecture/pause.
- Taille d'Alex : 4 × 2, sous Monitoring.
- Si rien ne joue, état discret (« Rien en lecture ») : le module reste en ligne.

### 6.6 Heure, date, météo

- Heure en grand, **format 24 h ou 12 h (AM/PM) au choix** dans les réglages du module (demande d'Alex). Par défaut 24 h. Date en toutes lettres en français (« Samedi 24 octobre ») **(choix de Claude)**.
- Météo par **Open-Meteo** : gratuit, sans compte ni clé. **La localisation se choisit dans les réglages du module** :
  - par une recherche de ville par nom, via le service de géocodage gratuit d'Open-Meteo ;
  - on enregistre le nom et les coordonnées.
- Mise à jour toutes les 15 minutes **(choix de Claude)**.
- Unités : °C par défaut, °F au choix **(choix de Claude)**.
- Affiche la température, une icône et un mot (« Éclaircies »). Les icônes suivent le thème.

### 6.7 Réseau

- **Débit descendant et montant en direct**, plus une petite courbe des dernières minutes.
- **Sources** :
  - **Freebox** : API Freebox OS, statut de connexion (`rate_down` / `rate_up` en octets/s). La première autorisation demande d'**appuyer sur un bouton de la Freebox**, et le module l'écrit sur l'écran. Le jeton obtenu est conservé.
  - **Ubiquiti UniFi** : API locale avec une clé créée dans UniFi. Débit de la passerelle via les statistiques de l'appareil (lien montant). La mise à jour est moins fréquente (quelques secondes).
  - **Carte réseau du PC** : sans configuration. Ne mesure que le trafic de ce PC. Elle sert de solution de secours pour qui n'a ni Freebox ni UniFi **(choix de Claude)**.
- Le titre du module affiche la source (« Freebox », « UniFi », « Ce PC »).

## 7. Apparence

### 7.1 Thèmes

Les trois thèmes sont validés par Alex sur l'écran réel. Les maquettes de référence sont dans `themes-v3.html`.

| Thème | Caractère |
|---|---|
| **Classique** | Panneaux mats presque opaques, coins arrondis, typo condensée de type tableau de bord (Barlow Semi Condensed), une seule couleur d'accent (vert d'eau `#7FD1C7`, avertissement `#F2B84B`). |
| **Liquid Glass** | Verre dépoli : la portion du fond sous le module est floutée et plus saturée, avec une légère teinte sombre pour garder le texte lisible et un reflet lumineux sur le bord haut. Grands arrondis, typo Manrope. |
| **Pixel** | Blocs nets sans arrondi, ombres décalées, barres en segments, typo pixel (Pixelify Sans, VT323 pour le flux). Couleurs : jaune `#FFD27A`, vert `#7CE0A3`, fond violet nuit. |

**Réglages communs à tous les thèmes**
- **Transparence des modules** : du verre presque invisible au panneau opaque.
- **Police** : choix parmi les polices embarquées et celles du système.
- **Taille du texte** : par module (voir 6.1).

Les polices embarquées sont sous licence OFL : on peut les redistribuer avec l'appli.

### 7.2 Fond d'écran
- Au choix : **couleur**, **image**, **GIF** ou **vidéo**. Le fond est recadré pour remplir l'écran.
- Les fonds animés sont lus à ≤ 12 images/s.
- Le décodage vidéo ne doit **rien demander à installer**. Utiliser ce que Windows fournit (Media Foundation) ou une bibliothèque embarquée, au choix du constructeur.

### 7.3 Mouvement

Avec 12 images/s, les mouvements doivent être **courts et nets** : barres qui se mettent à jour, chiffres qui changent, changement d'onglet. Les glissements longs et doux saccaderaient et sont à éviter.

## 8. Le logiciel de réglage

Maquette de référence : `editor-grid-v3.html`.

- **Bandeau du haut** : onglets **Disposition**, **Personnalisation**, **Modules**, **Raccourcis**, **Paramètres**. À droite : l'état de l'écran (branché / non branché) et l'indicateur **Mode émulé**.
- **Disposition** :
  - à gauche, la liste des modules avec leur point d'état ;
  - au centre, l'aperçu de l'écran avec le damier, où l'on déplace et redimensionne ;
  - à droite, les réglages du module sélectionné : taille en cases et en pixels, taille minimum, tailles toutes faites, affichage, taille du texte.
- **Mode émulé** : l'aperçu tourne même sans écran branché. Une option **« rendu réel 12 images/s »** montre l'aperçu exactement au rythme de l'écran **(choix de Claude)**.
- **Personnalisation** : thème, transparence, police, fond d'écran.
- **Modules** : la configuration de chaque module (Freebox, UniFi, ville météo, choix des valeurs…), avec l'état.
- **Raccourcis** : touches pour changer d'onglet Claude Code / Codex, toutes modifiables.
- **Paramètres** :
  - orientation : **0° ou 180°** dans la première version. Le portrait (90° / 270°) demanderait un second damier et un dessin portrait de chaque module ; il est repoussé à plus tard **(choix de Claude)** ;
  - démarrage avec Windows ;
  - plafond d'images par seconde ;
  - pilote CPU (bouton **Installer** si absent) ;
  - mises à jour ;
  - mesure de mémoire et de CPU de l'appli.
- **Partager une disposition** : export et import d'un fichier qui contient la disposition, le thème et les réglages, **sans les secrets** (jeton Freebox, clé UniFi) **(choix de Claude)**.
- Le logiciel doit suivre la même direction visuelle soignée que l'écran. Alex a demandé que le design passe par la compétence **Frontend-Design**.
- Langue : français. Les textes sont rangés à part pour pouvoir ajouter l'anglais plus tard **(choix de Claude)**.

## 9. Installation, première ouverture, mises à jour

### 9.1 Installateur
*Remplace le Setup.exe de Velopack prévu au départ ; voir `2026-10-29-plan-6-distribution-design.md`, section 4.2 et 5.1.*
- Un assistant d'installation classique, à pages, fabriqué avec **Inno Setup 7** : `Ashboard-Vision-Setup-<version>.exe`. Il tourne **en administrateur** et installe dans `Program Files\Ashboard Vision`.
- Il **embarque** le runtime .NET (publication autonome) et les polices. Il installe l'assistant des capteurs et, si la case est cochée, PawnIO, qu'il télécharge alors depuis le site officiel (empreinte vérifiée, connexion Internet requise pour ce pilote ; décision d'Alex du 31/10/2026, la licence de redistribution de PawnIO n'étant pas claire).
- Rien à télécharger ni à installer à la main.
- **Aucune signature numérique** : l'avertissement SmartScreen est accepté, et le README explique comment le passer.

### 9.2 Assistant de première ouverture
1. **Détection de l'écran.** Si le logiciel Thermalright tourne, l'assistant explique qu'il faut le fermer.
2. **Orientation.** L'assistant affiche une image test sur l'écran et demande si elle est à l'endroit. Sinon, l'utilisateur la tourne.
3. **Choix des modules** à placer : une disposition de départ est proposée.
4. **Claude Code / Codex** : détection, puis proposition (avec consentement) d'installer le mod `ashboard-quotas`, qui remplace la petite commande de barre d'état et le hook (voir 6.2 et `2026-10-29-plan-6-distribution-design.md`).
5. **Réseau** : choix de la source et lancement de l'autorisation (Freebox) ou saisie de la clé (UniFi).
6. **Météo** : ville.
7. **Températures CPU** : case à cocher **« Installer le pilote PawnIO »**. On peut aussi l'installer plus tard depuis Paramètres.
8. **Démarrage avec Windows.**

### 9.3 Mises à jour
*Plus de Velopack ; voir `2026-10-29-plan-6-distribution-design.md`, sections 4.3 et 5.3.*
- Le logiciel **vérifie les releases GitHub** du projet au démarrage (60 s après) puis une fois par jour **(fréquence : choix de Claude)**.
- Si une mise à jour existe, elle est signalée dans l'icône de notification et dans le logiciel. **Un clic télécharge la version, vérifie son empreinte SHA-256 et lance l'installateur** sans pages, après l'autorisation administrateur de Windows. Le logiciel est relancé ensuite.
- **Deux canaux**, au choix dans Paramètres : **Stable** (par défaut, versions de `main`) et **Bêta** (préversions de `Canary`). Repasser de Bêta à Stable installe tout de suite la dernière stable, après une copie des réglages et un avertissement ; la case « Attendre la prochaine version stable » évite ce retour en arrière.

## 10. Robustesse

**USB et écran**
- Une seule image en cours à la fois, et on attend toujours la validation de l'écran (lecture de 512 octets, délai 1 s).
- Plafond d'envoi à **12 images/s**, réglable dans Paramètres.
- **Garde-fou** : si l'écran ne répond plus (délai d'écriture ou de validation dépassé), le programme :
  1. réinitialise le périphérique USB ;
  2. lit les réponses en attente ;
  3. fait un `clear halt` sur l'endpoint de sortie ;
  4. refait la poignée de main ;
  5. renvoie la dernière image.

  Le point de l'écran passe au rouge pendant la reprise. Ce garde-fou est **obligatoire** : Alex a insisté après avoir vu l'écran se figer pendant l'essai.
- Branchement et débranchement à chaud : reprise automatique.

**Données et confidentialité**
- Une source qui tombe (Freebox injoignable, API météo en panne…) fait passer **son** module au rouge sans affecter les autres.
- Les secrets (jeton Freebox, clé UniFi) sont stockés chiffrés pour l'utilisateur Windows, avec DPAPI **(choix de Claude)**.

## 11. Projet GitHub

- **Dépôt public**, qui s'appelait `trofeo-HB-dashboard` (choix d'Alex) et devient **`Ashewos/ashboard-vision`** (renommage décidé au plan 6) : le logiciel s'appelle **Ashboard Vision** (identifiant technique `ashboard-vision`). Ce nom est l'identité définitive de l'appli : dossiers d'installation et de réglages, nom de la tâche planifiée, installateur. Il ne doit plus changer une fois que des amis l'ont installée. Seuls restent à l'ancien identifiant l'entropie DPAPI, l'identifiant Freebox et le format d'export de la disposition, pour relire ce qui existe déjà. GitHub redirige les anciens liens.
- Les mises à jour en un clic lisent les releases publiques du dépôt, sans jeton. Les versions sont construites par GitHub Actions à partir d'une étiquette `vX.Y.Z`, avec tous les tests.
- **Licence GPL-3.0**, acceptée par Alex, puisque le protocole de l'écran s'appuie sur des projets GPL-3.0. Créditer ces projets dans le README.
- Les mascottes sont des redessins de fan des personnages de Claude (Anthropic) et de Codex (OpenAI). Le README doit l'indiquer et préciser que le projet n'est affilié à aucune des deux sociétés **(choix de Claude)**.

## 12. Laissé au constructeur

- Le découpage exact des 462 px de hauteur en 4 rangées.
- La structure du code, les bibliothèques USB (WinUSB direct ou libusb), le décodage vidéo, la qualité JPEG (85 à 90 a bien marché pendant l'essai).
- Le dessin exact des images de la mascotte et le nombre de variantes par état (au moins deux par état, sauf « terminé »).
- Les intervalles de lecture de chaque source, dans le respect de la sobriété (4.3).
- Le contenu exact des tailles Compact et Large de chaque module.
- Le README GitHub : présentation soignée avec une capture de l'écran rendue par l'émulateur, un GIF de la mascotte, la liste des modules, l'installation et les crédits.

## Annexe A — Protocole USB (vérifié sur l'écran d'Alex le 24/10/2026)

- **Endpoints** : bulk OUT `0x09`, bulk IN `0x81`. Interface 0.
- **Poignée de main** :
  - envoyer 2048 octets, avec l'octet 0 = `0x02`, l'octet 1 = `0xFF`, l'octet 8 = `0x01` et le reste à zéro ;
  - lire 512 octets. On attend l'octet 0 = `0x03` et l'octet 1 = `0xFF`.
  - Réponse obtenue : `03 ff 00 00 00 00 00 00 01 00 00 00 00 00 00 00 49 8e d6 bc 02 00 04 00 80 07 00 00 57 02 00 00 41 …`
- **Image** : un JPEG de 1920 × 462 en paysage (qualité 70 à 90 testée, sous-échantillonnage 4:4:4), découpé en paquets de 512 octets.
  - **En-tête de 16 octets**, en little-endian :

    | Octets | Contenu |
    |---|---|
    | 0–1 | `01 FF` |
    | 2–5 | taille totale du JPEG (u32) |
    | 6–7 | taille utile du paquet (u16) |
    | 8 | `01` |
    | 9–10 | nombre total de paquets (u16) |
    | 11–12 | index du paquet (u16) |
    | 13–15 | zéros |

  - **Charge utile** : 496 octets, complétée par des zéros.
  - **Nombre de paquets** : `taille // 496 + 1`. On complète ensuite avec des paquets entièrement à zéro (sans en-tête) jusqu'à un multiple de 4.
  - **Envoi** : par blocs de 4096 octets. Lire ensuite 512 octets de validation.
- **Mesures** : écriture ~7 ms, validation ~73 ms, soit ~12,4 images/s. Le plafond reste le même entre 32 Ko et 700 Ko par image.
- **Gel et reprise** : voir la section 10.

## Annexe B — Sources

- Protocole : Lexonight1/thermalright-trcc-linux (GPL-3.0), sukualam/trofeo-lcd, haven80/trofeo-lcd-rs, JuliusNarvasa/TrofeoVision, CraigyBabyJ/lcd_therma.
- Mascotte : Codrops, « Reverse-Engineering Claude AI's Mascot Animations with SVG and GSAP » (5 mai 2026).
- Mascotte Codex : note.com/akikito/n/nc0b4362b626f (« Are Codex Pets a Revival of 2000s Desktop Mascots? », 3 mai 2026).
- Jetons d'abonnement : documentation « Legal and compliance » de Claude Code (février 2026) et sa couverture presse (gigazine, winbuzzer).
- Freebox : dev.freebox.fr/sdk/os/connection. UniFi : Integration API (statistiques des appareils). Quotas Claude Code : objet `rate_limits` de la barre d'état. Codex : transcriptions `~/.codex/sessions`.
