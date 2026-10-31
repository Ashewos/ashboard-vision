# Plan 6 — Distribution : conception

*Écrite le 29/10/2026 avec Alex. Elle complète la spec d'origine (`2026-10-24-trofeo-dashboard-design.md`) et la remplace là où elles diffèrent (voir la dernière section). Les choix marqués **(choix de Claude)** n'ont pas été demandés par Alex : il peut les revoir en relisant.*

## 1. Intention

Le tableau de bord marche sur le PC d'Alex, mais seulement depuis le dépôt : il faut le construire, lancer des commandes et donner des chemins. Le plan 6 en fait **un logiciel qu'un ami installe comme Steam ou VLC** :
- il télécharge un `Setup.exe` sur GitHub ;
- il suit un assistant d'installation à pages ;
- il le retrouve dans la liste des applications de Windows ;
- il le met à jour en un clic ;
- il le désinstalle proprement.

On ne connaît pas encore cet ami. Il a peut-être déjà l'écran, peut-être un autre modèle, et le logiciel de Thermalright tourne peut-être chez lui. L'installation doit être **clé en main** : rien à télécharger ni à régler à la main avant que l'écran s'allume (seul PawnIO, le pilote des températures, est téléchargé par l'installateur lui-même, voir plus bas).

Le projet change aussi de nom : il devient **Ashboard Vision**, le tableau de bord signé du pseudo d'Alex (Ashewos), pour la gamme d'écrans Vision de Thermalright.

## 2. Objectifs, non-objectifs, anti-objectifs

**Objectifs**
- Un installateur Windows classique, en français, qui embarque tout, sauf le pilote PawnIO, qu'il télécharge pendant l'installation (voir 5.2).
- Une mise à jour en un clic depuis GitHub, avec deux canaux : Stable et Bêta.
- Une désinstallation propre, depuis « Applications installées ».
- Des versions construites et publiées par GitHub, quand Alex le demande.
- Une page GitHub soignée, sobre, aux codes des gros projets, avec une image d'accueil animée.
- Le nouveau nom, sans rien perdre des réglages d'Alex ni de Macorou.
- Reconnaître exactement l'écran branché, et dire clairement quand il n'est pas pris en charge.

**Non-objectifs (pour plus tard)**
- D'autres modèles d'écran, et plusieurs écrans à la fois. Les notes sont dans `docs/superpowers/notes/2026-10-29-ecrans-thermalright.md`.
- La signature numérique de l'installateur.
- Une mise à jour entièrement automatique, sans clic.
- L'accélération matérielle complète et la refonte du verre (voir le bilan du plan 4).

**Anti-objectifs** (ce qui ferait échouer le plan même si tout « marche ») :
- Une désinstallation qui laisse des traces : tâche planifiée, démarrage avec Windows, mod, dossiers.
- Perdre les réglages ou les secrets d'Alex au changement de nom : jeton Freebox, clé UniFi, disposition.
- Un retour de Bêta à Stable qui efface ou casse des réglages sans prévenir.
- Une page GitHub tape-à-l'œil, ou qui laisse croire à un produit officiel de Thermalright.
- Quoi que ce soit poussé, publié ou renommé sur GitHub sans l'accord d'Alex donné pour cette action-là.
- Un logiciel moins sobre qu'avant. La vérification des mises à jour ne doit pas compter dans la consommation.

## 3. Contraintes

- Windows 10 et 11, 64 bits. .NET 10, embarqué dans l'installateur.
- Textes en français, qui tutoient. Écran pris en charge : Trofeo Vision 9,16" (LY `0416:5408`, modèle PM 65, 1920×462).
- Sobriété (spec 4.3) : le service reste à environ 0,3 % de processeur.
- Règles du dépôt (`CLAUDE.md`) :
  - on travaille sur `Canary` ;
  - `main` ne reçoit que les versions stables, sur demande d'Alex ;
  - les commits utilisent l'adresse `noreply` ;
  - rien n'est publié sans l'accord d'Alex.
- `docs/guide-personnalisation.md` ne doit jamais être commité.

## 4. Ce qu'Alex a décidé

### 4.1 Nom
- **Nom affiché : « Ashboard Vision »**, partout où quelqu'un le voit :
  - l'installateur ;
  - la liste des applications ;
  - la fenêtre de réglage ;
  - l'icône de notification ;
  - la page GitHub.
- **Présentation sobre.** On ne commente pas le nom, on ne fait pas d'effets de manche.
- **Dépôt GitHub renommé `Ashewos/ashboard-vision`.** GitHub redirige les anciens liens. Le renommage lui-même attend l'accord d'Alex.
- Alex pourra plus tard ajouter un logo ou changer encore de nom. L'outil de l'image d'accueil (5.6) prend donc le nom, la phrase et le logo en paramètres.

### 4.2 Installateur
Un **assistant d'installation classique** à pages, comme VLC ou Steam, plutôt que l'installation en un clic de Velopack. Il tourne **en administrateur**, ce qui lui permet d'installer l'assistant des capteurs et le pilote PawnIO pendant l'installation même.

Ses pages :
1. « Bienvenue » ;
2. licence GPL-3.0 ;
3. dossier d'installation (par défaut dans `Program Files`) ;
4. options :
   - raccourci sur le bureau ;
   - démarrer avec Windows ;
   - installer le pilote des températures (PawnIO), coché par défaut, avec la mention « connexion Internet requise » ;
5. installation ;
6. « Terminer », avec la case « Lancer Ashboard Vision ».

Au premier lancement, l'assistant de première ouverture qui existe déjà prend le relais : écran, orientation, modules, Claude Code, réseau, météo, capteurs, démarrage.

### 4.3 Mises à jour
- Le logiciel regarde les versions publiées sur GitHub au démarrage, puis une fois par jour.
- Une mise à jour est signalée dans l'icône de notification et dans la fenêtre.
- **Un clic** la télécharge. Windows demande alors **l'autorisation administrateur** (accepté par Alex, comme pour VLC). L'installateur la fait sans pages, puis relance le logiciel. L'assistant des capteurs est réinstallé à chaque mise à jour.
- **Canal**, dans Paramètres : « Version : **Stable** / **Bêta** ».
  - Stable suit `main` et c'est le choix par défaut.
  - Bêta suit `Canary` : ce sont les préversions.
- **Repasser de Bêta à Stable** installe tout de suite la dernière version stable, en prévenant que des réglages enregistrés par la bêta pourraient mal se relire. Une case « **Attendre la prochaine version stable** » évite ce retour en arrière. *Avant un retour en arrière, une copie des réglages est faite* **(choix de Claude, proposé à Alex)**.

### 4.4 Désinstallation
- Le logiciel apparaît dans « Applications installées », et la désinstallation est propre (anti-objectifs).
- Elle demande s'il faut garder ou effacer les réglages : disposition, jetons, clés.
- PawnIO reste installé, car d'autres logiciels peuvent s'en servir.

### 4.5 Confiance de Windows
**Aucune signature numérique**, ni payante ni par la fondation SignPath : Alex refuse qu'un autre que lui apparaisse comme éditeur. L'écran bleu SmartScreen (« Windows a protégé votre ordinateur ») est accepté. La page GitHub explique, images à l'appui, comment le passer : « Informations complémentaires », puis « Exécuter quand même ».

### 4.6 Construction des versions
Par **GitHub Actions**, quand Alex le demande : stable depuis `main`, bêta depuis `Canary`. GitHub construit, lance tous les tests, fabrique l'installateur et publie la version. C'est aussi la preuve publique que l'installateur sort du code du dépôt.

### 4.7 Logo
Alex l'a validé le 29/10/2026. Il a été fait avec la méthode du skill [logo-design-skill](https://github.com/kaankiziltug/logo-design-skill) (MIT), en suivant son texte, sans lancer ses scripts. Les maquettes de travail sont dans `.superpowers/brainstorm/`, et les fichiers SVG dans `docs/superpowers/logo/`.

- **L'idée : la « fente de braise ».** Un bloc de charbon aux coins adoucis (courbure continue) est fendu d'une seule fente incandescente en lentille. La fente, c'est l'écran en bandeau qui s'allume. Elle se lit aussi comme un œil : la vision, l'information d'un coup d'œil.
- **Construction :**
  - grille de 256, bloc de 192, coins de 64 avec raccord progressif ;
  - fente de 144 × 36, centrée 4 unités au-dessus du centre (centre optique) ;
  - pour 16 à 24 px, une coupe dédiée : le bloc remplit le carré et la fente est plus haute.
- **Palette :**
  - Charbon `#17181C` et Braise `#FF5B1F` ;
  - Lueur en dégradé `#E8401C` → `#FFB04A` → `#E8401C`, seulement en grand ;
  - Cendre `#ECECEE`.

  Le turquoise reste la couleur du logiciel, pas du logo.
- **Le logo suit le thème de Windows** (demande d'Alex). En thème sombre, le bloc est sombre : charbon éclairé, liseré discret. En thème clair, il est clair : cendre légèrement ombrée, liseré gris. Seule la braise ressort.
  - **Zone de notification** : `ashboard-symbole-petit-sombre.svg` ou `ashboard-symbole-petit-clair.svg`, choisi selon le thème de la barre des tâches. Le logiciel change d'icône quand le thème change.
  - **Grande icône et logo avec le nom** : `ashboard-symbole-icone.svg` sur fond sombre, `ashboard-symbole-icone-clair.svg` sur fond clair. Sur fond clair, Alex préfère l'icône claire au charbon.
  - **L'installateur suit aussi le thème de Windows** (demande d'Alex) : pages claires ou sombres, avec l'image d'assistant et le petit logo assortis. Inno Setup sait le faire depuis la version 6.6 (vérifié dans sa documentation le 31/10/2026) : réglage `WizardStyle` « dynamique » et réglages `…DynamicDark` pour les images du mode sombre.
  - **L'icône du fichier** (`.exe`, raccourcis, liste des applications) ne peut pas changer avec le thème. Elle utilise `ashboard-symbole-icone.svg`, lisible sur les deux fonds grâce à son liseré.
- **Autres fichiers :**
  - `ashboard-symbole-couleur.svg` (référence à plat) ;
  - `ashboard-symbole-braise.svg` ;
  - `ashboard-symbole-noir.svg` et `ashboard-symbole-blanc.svg` (une couleur ; la version blanche a une fente affinée) ;
  - `ashboard-symbole-petit.svg`.
- **Le nom, avec le symbole** : « Ashboard » en Manrope Medium, suivi de « VISION » en petites capitales espacées.
  - Il existe une version horizontale et une version empilée.
  - Espace libre autour : au moins un quart de la largeur du symbole.
- **Reste à faire dans le plan 6 :**
  - convertir le nom en tracés (par exemple avec SkiaSharp, à partir de la police du dépôt) ;
  - générer les fichiers `.ico` multi-tailles (16, 20, 24, 32, 48, 256) et les PNG, avec un petit outil du dépôt plutôt qu'à la main ;
  - les images de l'assistant d'installation ;
  - le logo dans la fenêtre de réglage et dans l'image d'accueil (5.6).
- **Avant la version publique**, Claude recommande une recherche dans les registres de marques et par image inversée. Il ne peut pas garantir qu'aucun logo proche n'existe.

### 4.8 Page GitHub
- **En haut**, l'image d'accueil animée retenue. La maquette est `docs/superpowers/maquettes/readme-accueil-v4.html` :
  - style « produit premium » sur fond noir ;
  - l'écran incliné à 10° vers l'arrière ;
  - une **lueur Ambilight moyenne**, c'est-à-dire une copie floue et saturée de l'image, dont les couleurs viennent de l'écran ;
  - les **trois thèmes en motion design** (Classique, Liquid Glass, Pixel), 4 s chacun. Chaque changement glisse, un reflet balaie la vitre, le nom du thème s'affiche et une barre de progression avance. L'écran oscille légèrement.
- **Sous l'image** : badges (version, bêta, Windows, licence, téléchargements), liens rapides et bouton « Télécharger pour Windows ».
- La suite reste sobre (5.6).

### 4.9 Écrans
- Seul l'écran d'Alex est pris en charge.
- Le logiciel **reconnaît le modèle exact** grâce à la poignée de main : `0416:5408`, avec `PM = 64 + octet 20`, les valeurs 0 à 3 de l'octet comptant pour 1, ce qui donne 65. Il **dit clairement** quand l'écran branché n'est pas encore pris en charge, au lieu de tenter de l'utiliser.
- **La limite de 450 Ko par image reste la même pour tous.** Elle existe déjà (`JpegEncoder.MaxBytes`, `LyProtocol.MaxJpegSize`) et ne s'est jamais déclenchée sur l'écran d'Alex : sur 4 013 relevés, la médiane est de 164 Ko et le maximum de 280 Ko, à la qualité 88.

## 5. Choix de Claude et précisions pour le constructeur

### 5.1 Outil d'installation
**Inno Setup 7** **(choix de Claude ; version 7.1.1 choisie par Alex le 31/10/2026 plutôt que la 6)**. Il garde la compatibilité avec les scripts de la 6 et sait fabriquer un installateur 64 bits. Il propose un assistant classique et une traduction française, il sait se piloter en mode silencieux pour les mises à jour, et la recette GitHub l'installe dans cette version précise, sans compter sur celle déjà présente sur les machines Windows de GitHub. Velopack est abandonné.

### 5.2 Contenu installé
- Tout va **dans un seul dossier** (`Program Files\Ashboard Vision`) :
  - `Trofeo.Display.exe` (le service) ;
  - `Trofeo.Settings.exe` (la fenêtre) ;
  - `Trofeo.SensorHelper.exe` ;
  - les modules ;
  - les polices et leurs licences OFL ;
  - les licences tierces (Vortice, SharpGen, LibreHardwareMonitor) ;
  - le mod des quotas.

  `AppLocator` les trouve déjà dans le dossier de l'application.
- **.NET embarqué** : publication autonome `win-x64`. Le constructeur choisit entre plusieurs dossiers et un dossier commun, à condition que le service reste sobre en mémoire.
- **PawnIO téléchargé pendant l'installation** **(décision d'Alex, 31/10/2026 ; exception à « tout embarqué »)**. La licence de l'installeur signé de PawnIO ne dit rien de sa redistribution (code du pilote en GPL-2.0 ou ultérieure, mais le dépôt qui héberge l'installeur n'a pas de licence et le site réserve « tous droits » : voir `installer/README.md`). L'installateur ne l'embarque donc pas : si la case est cochée et que PawnIO n'est pas déjà installé, il le télécharge depuis l'adresse officielle (`installer/pawnio.json` : version, adresse, SHA-256), en vérifiant l'empreinte, puis lance `Trofeo.SensorHelper.exe install --pawnio <fichier>`. L'assistant dit qu'une connexion Internet est requise. Si le téléchargement échoue, l'installation continue sans PawnIO et dit comment l'installer plus tard depuis la fenêtre de réglage.
- **L'assistant des capteurs** est installé et sa tâche planifiée créée, ou réinstallés à chaque mise à jour. Ce point vient du bilan du plan 5 : un ancien assistant n'envoie qu'une carte graphique.
- **Partage du travail entre l'installateur et le logiciel** **(choix de Claude)**.
  - **L'installateur, en administrateur, fait tout ce qui concerne la machine :**
    - copier les fichiers ;
    - installer PawnIO ;
    - installer ou réinstaller l'assistant des capteurs et sa tâche ;
    - arrêter les processus qui tournent : le service, la fenêtre, l'ancien assistant, y compris une ancienne version lancée depuis le dépôt ;
    - retirer l'ancienne tâche et l'ancien dossier `Program Files\trofeo-HB-dashboard\sensors`.
  - **Le logiciel, lancé comme l'utilisateur, fait tout ce qui le concerne lui :**
    - la valeur `Run` ;
    - l'installation, le remplacement ou le retrait du mod des quotas (`claude` doit être dans le `PATH` de l'utilisateur) ;
    - le retrait de l'ancienne barre d'état ;
    - le déplacement de `%AppData%`.

    Il le fait au premier lancement, puis après chaque mise à jour, en comparant la version installée à la dernière version qu'il a préparée. Les options cochées dans l'installateur (démarrer avec Windows) lui sont transmises par un petit fichier dans le dossier d'installation.
  - **Le lancement final** (« Lancer Ashboard Vision », et la relance après une mise à jour) se fait **sans élévation**, comme l'utilisateur d'origine. Le tuyau de contrôle n'accepte que l'utilisateur courant.
  - **Limite acceptée.** Si un autre compte administrateur que celui de l'ami accepte l'autorisation, la désinstallation ne nettoie les réglages, la valeur `Run` et le mod que du compte qui la lance. Le README le signale. C'est rare sur un PC personnel.
- **Le logiciel de Thermalright.** L'assistant de première ouverture sait déjà dire de le fermer. L'installateur n'y touche pas.

### 5.3 Mises à jour (précisions)
- **Qui vérifie.** Le service, qui tourne toujours, interroge l'API publique des versions GitHub, sans jeton : au démarrage, puis toutes les 24 h **(choix de Claude)**. Il ne garde rien en mémoire entre deux vérifications.
- **Ce qu'il retient.**
  - En Stable, la dernière version non marquée « préversion ».
  - En Bêta, la plus récente des préversions **et** des versions stables : une stable plus récente qu'une bêta est aussi proposée.
  - Il compare les numéros SemVer.
- **Téléchargement vérifié.** La version publiée contient le `Setup.exe` et son empreinte SHA-256. Le logiciel vérifie l'empreinte avant de lancer l'installateur. Si la vérification échoue, rien n'est installé et un message le dit.
- **Pendant la mise à jour.** L'installateur arrête le service et ferme la fenêtre, qui verrouillent leurs fichiers. Il installe, réinstalle l'assistant des capteurs, relance le service, et rouvre la fenêtre si elle était ouverte.
- **Copie des réglages** avant un retour de Bêta à Stable : le dossier des réglages est copié à côté, daté. Le constructeur choisit l'emplacement exact.
- **Retour de Bêta à Stable** **(choix de Claude)** :
  - l'avertissement s'affiche **à chaque fois** qu'on descend vers une version plus ancienne. Il n'y a pas de numéro de version des réglages à comparer ;
  - l'installateur accepte de remplacer une version plus récente : même identité d'application ;
  - la case « Attendre la prochaine version stable » met le canal sur Stable, mais garde la bêta installée. Une mise à jour n'est proposée que lorsqu'une stable **plus récente que la bêta installée** paraît.
- **Avant la première stable** : en Stable, il n'y a rien à proposer, et le logiciel le dit simplement (« Aucune version stable pour l'instant »).
- **Après une mise à jour, le mod des quotas est réinstallé** par le logiciel, comme l'utilisateur (voir le partage du travail en 5.2).

### 5.4 Désinstallation (précisions)
Elle arrête le service et la fenêtre, puis retire :
- la valeur `Run` ;
- la tâche et les fichiers de l'assistant des capteurs ;
- le mod des quotas (`claude plugin uninstall`, si Claude Code est présent) ;
- les raccourcis ;
- le dossier d'installation.

Ensuite, elle demande s'il faut garder ou effacer `%AppData%\ashboard-vision` (réglages, journaux, secrets).

Avant de supprimer les fichiers, le désinstalleur appelle le logiciel pour faire le nettoyage propre à l'utilisateur : la valeur `Run`, le mod, et la question sur les réglages. C'est le même code que pour l'installation, avec la limite décrite en 5.2.

### 5.5 Nouveau nom dans le code
- **Identifiant technique `ashboard-vision`.** Il nomme :
  - le dossier des réglages, `%AppData%\ashboard-vision` (`AppPaths`) ;
  - la valeur `Run` (`StartupRegistration.ValueName`) ;
  - le dossier de l'assistant des capteurs (`HelperIdentity`) et sa tâche (`TaskDefinition`) ;
  - le dossier des captures de quotas sous `~/.claude` ;
  - le mod et sa place de marché.
- **Ce qui garde l'ancien nom, exprès :**
  - **l'entropie DPAPI** des secrets (`DpapiSecretVault`) : la changer rendrait illisibles le jeton Freebox et la clé UniFi déjà enregistrés ;
  - **l'identifiant d'application Freebox** (`FreeboxSource.AppId`) : le changer annulerait l'autorisation donnée sur la Freebox ;
  - les noms internes du code (`Trofeo.*`, projets, espaces de noms, tuyaux nommés). Personne ne les voit.
- **Migration, une seule fois.** Elle sert à Alex et à Macorou, et se fait en deux temps (voir le partage en 5.2) :
  - **par l'installateur** :
    - arrêt de l'ancien service et de l'ancienne fenêtre (même lancés depuis le dépôt) ;
    - retrait de l'ancienne tâche et de l'ancien dossier de l'assistant des capteurs ;
  - **par le service, au premier lancement de la nouvelle version, avant tout le reste.** La fenêtre de réglage attend qu'il ait fini, au lieu de migrer elle aussi :
    - le dossier `%AppData%\trofeo-HB-dashboard` est déplacé vers `%AppData%\ashboard-vision`, si le nouveau n'existe pas ;
    - l'ancienne valeur `Run` et l'ancien mod sont retirés ;
  - le mod `trofeo-quotas` est remplacé par le nouveau ;
  - pendant une période de transition, la source des quotas lit aussi l'ancien dossier de captures ;
  - une migration ratée ne perd rien : l'ancien dossier reste en place, et un message le dit.
- **Barre d'état de Claude Code retirée** **(choix de Claude)** :
  - `Trofeo.StatusLine.exe` ne fait plus partie de la distribution, car le mod couvre l'appli desktop et le terminal ;
  - si l'ancienne barre d'état est configurée chez quelqu'un, la migration la retire et remet la configuration précédente (`statusline-previous.json`) ;
  - le constructeur décide s'il supprime le projet du dépôt ou le garde hors de la distribution.

### 5.6 Page GitHub (précisions)
- **README principal**, court et sobre **(choix de Claude)** :
  1. l'image d'accueil, les badges et le bouton ;
  2. ce que fait le logiciel, en quelques lignes ;
  3. les modules, avec une capture chacun ;
  4. l'installation pas à pas, avec SmartScreen ;
  5. les versions Stable et Bêta ;
  6. les écrans compatibles (seulement le Trofeo Vision 9,16" pour l'instant) ;
  7. une petite foire aux questions ;
  8. les crédits, déjà écrits ;
  9. la licence ;
  10. la mention que le projet n'est affilié ni à Thermalright, ni à Anthropic, ni à OpenAI, et que les mascottes sont des redessins de fan.
- **Le README technique actuel** (réglages à la main, détail des modules) part dans `docs/` **(choix de Claude)**.
- **Image d'accueil.** Un outil du dépôt, à côté de `tools/Trofeo.ThemePreview`, fabrique l'image animée à partir de vrais rendus de l'écran. Il prend en paramètres le nom, la phrase d'accroche et un logo facultatif.
  - **Format en couleurs complètes que GitHub affiche** (PNG animé ou WebP animé, au choix du constructeur après essai). Le GIF n'est gardé que s'il rend bien : 256 couleurs font des marches dans la lueur.
  - Taille raisonnable pour une page web : quelques Mo au plus.
  - Phrase d'accroche proposée, sobre : « Le tableau de bord de ton écran Thermalright Trofeo Vision. »

### 5.7 Versions et publication
- **Numéros SemVer** tirés de `Directory.Build.props` (aujourd'hui `0.5.0`) **(choix de Claude)** :
  - les bêtas d'essai, avant la première version publique, sont `1.0.0-beta.1`, `1.0.0-beta.2`, etc., publiées depuis `Canary` ;
  - **la première version stable est la 1.0.0**, publiée depuis `main` ;
  - les bêtas suivantes s'écrivent `1.1.0-beta.1`, et ainsi de suite.
- **Le dépôt est déjà public.** Le logiciel cherche les versions sous le **nouveau** nom, `Ashewos/ashboard-vision`. Le renommage du dépôt, avec l'accord d'Alex, a donc lieu **avant la première bêta**.
- **Notes de version** **(choix de Claude)** : un fichier `CHANGELOG.md` en français, que Claude rédige à partir des commits. Alex le relit avant de donner son accord pour la version. La recette en extrait la section de la version publiée.
- **Tests dans la recette.** La recette doit être verte. Tous les tests passent aujourd'hui sous Windows, sur le PC d'Alex. Si l'un d'eux échoue sur la machine de GitHub uniquement pour une raison d'environnement (aucun écran, aucun capteur), il est marqué et exclu de la recette, avec la raison écrite à côté. Il continue de tourner sur le PC d'Alex. Jamais pour masquer un vrai défaut.
- **Une recette GitHub Actions**, `.github/workflows/release.yml`, sur une machine Windows. Elle :
  1. vérifie que le numéro correspond à la branche (stable sur `main`, bêta sur `Canary`) ;
  2. construit en Release et lance **tous** les tests ;
  3. publie ;
  4. compile l'installateur ;
  5. calcule l'empreinte ;
  6. crée la version GitHub, marquée « préversion » pour une bêta, avec ses notes.
- **Déclenchement par une étiquette `vX.Y.Z`** **(choix de Claude)**. Claude ne pose et ne pousse une étiquette qu'avec l'accord d'Alex pour cette version.
- **Nom du fichier** : `Ashboard-Vision-Setup-1.0.0.exe`.

### 5.8 Essais avant la 1.0.0
- **Installation à vide** dans le **Bac à sable Windows** (Windows Sandbox, présent dans Windows 11 Pro) **(choix de Claude)**. On vérifie les pages, la liste des applications et la désinstallation propre, sans écran branché : l'assistant doit le dire.
- **Sur le PC d'Alex, avec l'écran :**
  - installer une première bêta publiée par GitHub ;
  - vérifier la migration de ses réglages et de ses secrets (la Freebox reste autorisée) ;
  - passer à une deuxième bêta en un clic ;
  - repasser en Stable, avec et sans la case « attendre » ;
  - désinstaller en gardant, puis en effaçant les réglages ;
  - réinstaller ;
  - vérifier le démarrage avec Windows.
- **Sobriété** : même méthode qu'aux bilans 4 et 5. Le service ne doit pas consommer plus qu'avant.

## 6. Laissé au constructeur

- Le script Inno Setup en détail : textes des pages, icônes, image de l'assistant.
- La façon d'afficher une mise à jour dans l'icône et dans la fenêtre. Elle suit le style existant de `Trofeo.Settings`.
- Le format exact de l'image animée et son rythme précis, à partir de la maquette.
- La répartition en tâches et l'ordre de construction (cloud ou PC d'Alex).
- La façon de reconnaître un écran Thermalright non pris en charge, à partir des identifiants USB de la note sur les écrans.

## 7. Ce qui change dans la spec d'origine

- **9.1 Installateur** : Inno Setup, assistant à pages, installation en administrateur dans `Program Files`. PawnIO et l'assistant des capteurs sont installés pendant l'installation.
- **9.3 Mises à jour** : plus de Velopack. Le logiciel télécharge et lance l'installateur en silence, avec une autorisation administrateur à chaque fois. Il y a deux canaux, Stable et Bêta.
- **11 Projet GitHub** : le projet s'appelle **Ashboard Vision** (dépôt `ashboard-vision`). L'identifiant technique devient `ashboard-vision`, sauf l'entropie DPAPI et l'identifiant Freebox, qui gardent l'ancien nom (5.5).
- **Roadmap (section 1 du plan 1)** : la ligne du plan 6 parle d'Inno Setup au lieu de Velopack.
