# Plan 7 — Modules complémentaires et SDK : conception

*Écrite le 01/11/2026 avec Alex. Elle complète la spec d'origine (`2026-10-24-trofeo-dashboard-design.md`) et la conception du plan 6 ; elle les remplace là où elles diffèrent. Les choix marqués **(choix de Claude)** n'ont pas été demandés par Alex : il peut les revoir en relisant.*

## 1. Intention

Ashboard Vision a six modules, tous écrits dans le dépôt. Alex veut que **n'importe qui puisse créer un module qui ajoute une fonctionnalité**, comme on crée un module pour le Stream Deck d'Elgato. Un développeur peut publier son module pour les autres, ou s'en faire un pour lui seul, sans le publier.

Les exemples d'Alex disent à quoi ça sert :
- une intégration Home Assistant : un mini tableau de bord de la maison ;
- un overlay Discord : qui parle en vocal (pas possible dans la première version, voir 5.3) ;
- une caméra Frigate en direct ;
- l'état des services vu par Downdetector ;
- les informations d'un serveur Proxmox ;
- les statistiques de Pi-hole ou d'AdGuard.

Presque tous parlent à un service du réseau local ou à une API sur Internet, avec un jeton d'accès, et dessinent des valeurs, des états, des listes, des courbes ou des images. La caméra Frigate demande plusieurs images par seconde.

Alex insiste sur deux choses : **le service ne doit jamais tomber parce qu'un module plante**, et **les gens doivent faire attention à leur sécurité informatique**, parce qu'un module de la communauté peut contenir du code malveillant.

Alex ne connaît pas le monde des SDK. Il compte sur cette conception pour suivre les bonnes pratiques du métier : contrat versionné, modèle de projet, tests, isolement.

## 2. Objectifs, non-objectifs, anti-objectifs

**Objectifs**
- Un **SDK** pour écrire un module complémentaire en C# : un paquet, un modèle de projet, une documentation en français.
- Des modules complémentaires qui tournent **chacun dans son propre processus**, enfermés par Windows, avec des **permissions** déclarées par le créateur et validées une par une par l'utilisateur.
- Un **fichier de module** (`.avmod`) qu'on télécharge et qu'on installe depuis la fenêtre de réglage, et des **mises à jour** trouvées sur GitHub et proposées à l'utilisateur.
- Un **banc d'essai** qui dit au créateur si son module fonctionnera avec le logiciel, et combien il consomme.
- Une **vue de la consommation** des modules dans la fenêtre de réglage.
- Un **mode développeur** pour tester un module pendant qu'on l'écrit.

**Non-objectifs (pour plus tard)**
- Une boutique ou un catalogue hébergé (Alex n'a pas d'infrastructure). Une simple liste des modules connus dans le dépôt GitHub reste une idée pour plus tard.
- Des SDK dans d'autres langages que C#. Le protocole entre le service et un module est documenté pour les permettre plus tard, mais la première version ne lance que des modules .NET.
- Les actions depuis l'écran ou les raccourcis clavier (allumer une lampe Home Assistant, par exemple) : la première version du SDK **affiche**.
- L'accès d'un module aux fichiers de l'utilisateur (aucun exemple d'Alex n'en a besoin).
- Faire passer les six modules d'origine dans des processus à part : ils restent dans le service, inchangés.
- La signature numérique des modules.

**Anti-objectifs** (ce qui ferait échouer le plan même si tout « marche ») :
- Un module complémentaire qui fait tomber le service, ou qui fige l'écran.
- Un module qui obtient un accès que l'utilisateur n'a pas validé, ou qui tourne sans que l'utilisateur l'ait accepté (déposé dans le dossier par un autre programme, par exemple).
- Une mise à jour de module installée en silence.
- Un module ancien refusé alors qu'il fonctionnerait encore.
- Un logiciel moins sobre qu'avant **quand aucun module complémentaire n'est installé** : le service doit rester à environ 0,3 % de processeur, comme au plan 6.
- Un avertissement de sécurité qu'on ne voit pas, ou qu'on peut confondre avec un module d'origine.

## 3. Contraintes

- Windows 10 et 11, 64 bits ; .NET 10 ; SkiaSharp comme les modules actuels.
- Textes en français, qui tutoient ; la documentation du SDK aussi.
- Écran : 1920×462, damier de 16 colonnes sur 4 lignes (`GridGeometry`) ; un module dessine dans sa case, à sa taille.
- Le service tourne avec les droits de l'utilisateur, sans élévation (plan 6). Tout ce qui suit doit marcher sans droits d'administrateur.
- Règles du dépôt (`CLAUDE.md`) : travail sur `Canary`, `main` pour les stables, adresse `noreply`, rien de publié sans l'accord d'Alex.

## 4. Ce qu'Alex a décidé (01/11/2026)

### 4.1 Un processus par module
- Chaque module complémentaire tourne dans **son propre processus**, comme au Stream Deck. Si un module plante, le service continue ; seule la case du module affiche une erreur.
- Alex a choisi l'isolement plutôt que la sobriété : un module complémentaire coûte de la mémoire en plus (de l'ordre de 20 à 40 Mo par module actif). Les modules d'origine ne coûtent rien de plus.

### 4.2 Sécurité
- **Un avertissement** à l'installation : un module de la communauté peut contenir du code malveillant.
- **Des permissions à la manière d'Android** : le créateur déclare dans le manifeste ce dont son module a besoin. À l'installation, l'utilisateur **coche chaque permission déclarée, une par une**. Ce que le créateur n'a pas déclaré est refusé d'office, sans question : on ne surcharge pas l'utilisateur de choix.
- L'utilisateur peut changer ses choix plus tard, dans les réglages du module.
- **Un point d'exclamation jaune** partout où un module complémentaire apparaît dans la fenêtre de réglage :
  - dans ses réglages, avec la mention « Module complémentaire : créé par la communauté, pas par Ashboard Vision » et le rappel de ses permissions ;
  - dans la liste des modules à ajouter ;
  - sur sa case dans l'éditeur de disposition : **juste un point d'exclamation jaune dans un angle**.
- **Sur l'écran Trofeo, rien ne distingue un module complémentaire** : l'écran est fait pour regarder.

### 4.3 Distribution
- Un module est **un fichier à télécharger**, publié où le créateur veut (en pratique, dans les versions GitHub de son dépôt). Pas de boutique.
- **Dossier des modules** : par défaut `Documents\Ashboard Vision\Modules`, modifiable dans les Paramètres.
- **Mises à jour** : si le manifeste indique un dépôt GitHub, le logiciel y trouve les nouvelles versions tout seul.

### 4.4 Manifeste
Chaque module décrit dans son manifeste : son nom, **une description**, son auteur, sa version, la version du SDK dont il a besoin, ses permissions et, s'il veut, son dépôt GitHub. **(choix de Claude)** Le manifeste porte aussi son identifiant, ses tailles et le schéma de ses réglages, pour que la fenêtre puisse tout montrer avant que le module soit validé (5.5).

### 4.5 Compatibilité
Un module ancien n'est pas refusé parce qu'il est ancien (question d'Alex, réponse validée) :
- Le SDK suit la numérotation habituelle `MAJEUR.MINEUR`. Le deuxième chiffre augmente quand on **ajoute** ; rien n'est retiré tant que le premier chiffre ne change pas.
- Un module déclare la version **minimale** du SDK dont il a besoin. Un module pour la 1.2 tourne dans un logiciel qui parle la 1.5.
- Un module qui demande une version plus récente que celle du logiciel est refusé avec le message « Ce module demande une version plus récente d'Ashboard Vision : mets à jour le logiciel ».
- Quand le premier chiffre change (2.0), le logiciel parle encore l'ancienne version pendant une période de transition ; un module n'est refusé qu'après, avec un message clair.

### 4.6 Dessin libre
- **Sur l'écran, le module dessine ce qu'il veut dans sa case**, pixel par pixel (exemple d'Alex : le mini tableau de bord Home Assistant d'un développeur, fait pour lui seul).
- Il peut afficher **une caméra en direct** (Frigate) : plusieurs images par seconde.
- Ce qui est automatique, c'est seulement **le formulaire de ses réglages** dans la fenêtre de réglage, construit à partir de ce que le module déclare.

### 4.7 Mode développeur
- Il sert à **tester un module pendant qu'on l'écrit**, pas à l'utiliser durablement.
- Pour garder un module, on fabrique son `.avmod` et on l'installe normalement, avec l'avertissement et les permissions, même si c'est son propre module.

### 4.8 Ajouts d'Alex à la relecture (01/11/2026)
- **Autoriser simplement un module à joindre un service de cet ordinateur**, par exemple un serveur Minecraft lancé sur le même PC, sans commande d'administrateur (voir 5.2, permission « Cet ordinateur »).
- **Un module ne doit pas pouvoir être modifié ou compromis entre son installation et son exécution**, et **cette vérification doit marcher hors ligne** (voir 5.5). Une signature par clé du créateur a été envisagée puis **écartée par Alex** (01/11/2026) : trop compliquée pour le projet à ce stade.
- **Les ports ne sont pas écrits en dur dans le module** : c'est l'utilisateur qui les choisit dans les réglages du module (plusieurs serveurs, port changé). Plus généralement, le SDK permet au module de proposer des champs de réglage dans la fenêtre : listes de choix, cases à cocher, textes, nombres, ports (voir 5.6).
- **Le SDK n'est pas publié sur nuget.org.** Il est joint aux versions GitHub d'Ashboard Vision, et **l'installateur propose de l'installer** : case décochée par défaut, mais cochée lors d'une mise à jour s'il était déjà installé (voir 5.12).

### 4.9 Sobriété
- Le logiciel **guide pour éviter les énormités** (le plafond d'images par seconde, par exemple) ; ensuite, optimiser son module est l'affaire du créateur.
- La fenêtre de réglage **affiche la consommation de tous les modules**, et signale une **consommation élevée** quand un module dépasse une limite.

## 5. Choix de Claude

Tout ce qui suit est marqué **(choix de Claude)** : Alex peut le revoir.

### 5.1 Architecture
- **Un hôte de module** : un nouvel exécutable livré avec le logiciel (dans `Program Files\Ashboard Vision`), qui charge la DLL d'un module .NET et parle au service. Le service lance **une instance de cet hôte par module complémentaire actif**, enfermée par Windows (5.3). Le créateur ne livre donc que sa DLL et ses dépendances, pas un exécutable.
- **Un processus par type de module, pas par case.** Si l'utilisateur place deux cases du même module (deux tableaux Home Assistant, par exemple), un seul hôte les sert toutes les deux, chacune avec sa taille, ses réglages et sa zone d'image. Les secrets sont déjà propres au type de module. Une panne du module arrête ses cases à lui, jamais celles des autres.
- **Contrat de dessin** : l'hôte fournit au module la bibliothèque du SDK et SkiaSharp, dans leurs versions à lui. Les copies éventuellement glissées dans le `.avmod` sont ignorées. La version majeure de SkiaSharp est figée pour toute la ligne 1.x du SDK. Le module dessine dans un `SKCanvas` que l'hôte lui donne, posé sur la zone d'image partagée. Il n'a pas de bibliothèque de dessin à lui.
- **Le canal** entre le service et un hôte de module : un tube nommé propre à chaque module, ouvert par le service, avec des droits d'accès réservés à l'identité Windows de ce module. Les messages sont en JSON, versionnés comme le SDK, et documentés (c'est ce qui permettra plus tard d'autres langages).
- **Les images** passent par une zone de mémoire partagée, à la taille exacte de la case, en deux tampons : le module écrit dans l'un pendant que le service lit l'autre, puis il signale qu'une image est prête. Le service ne copie l'image qu'une fois, en la posant dans la case.
- **Format de l'image** : BGRA avec transparence (alpha prémultiplié). Le service la pose **par-dessus le panneau déjà peint**, comme les modules d'origine dessinent aujourd'hui. Le module dessine toujours à l'endroit : la rotation de 180° et le découpage aux bords arrondis du panneau sont l'affaire du service. Quand la taille d'une case change (éditeur, rechargement), le service recrée sa zone d'image et prévient le module de sa nouvelle taille.
- **Le service n'attend jamais un module** : il pose la dernière image reçue. Un module qui ne répond plus garde sa dernière image, puis sa case passe en erreur au-delà d'un délai.
- **Le service reste le seul à toucher au matériel** (capteurs, musique, écran USB) : un module demande ces données au service par le canal (5.6).
- **Coût nul sans module complémentaire** : sans module installé et activé, aucun hôte n'est lancé et rien ne tourne en plus.

### 5.2 Les permissions de la première version

| Permission | Ce qu'elle ouvre | Imposée par |
|---|---|---|
| Réseau local | les appareils et serveurs de ton réseau (Home Assistant, Frigate, Proxmox…) | Windows (capacité `privateNetworkClientServer`) |
| Internet | les services sur Internet (Downdetector…) | Windows (capacité `internetClient`) |
| Cet ordinateur | un service lancé sur ce PC, sur les **ports choisis par l'utilisateur** dans les réglages du module (serveur Minecraft sur le 25565, Home Assistant sur le 8123…) | le service, qui relaie la connexion (5.3) |
| Capteurs | températures, charges, mémoire, lues par le service | le service |
| Musique en cours | titre, artiste, pochette | le service |

- Toujours accordés, sans permission : la taille de la case, le thème, l'heure, ses propres réglages, **son propre coffre à secrets** (chiffré par le service, propre au module), son propre dossier de données privé, et le journal.
- **Pas de permission « lancer un programme »** : elle permettrait de sortir de l'enfermement.
- Une permission refusée renvoie « refusé » au module, jamais une panne. La case peut l'afficher (« Internet refusé : la météo ne peut pas se mettre à jour »).
- Une mise à jour du module qui déclare une permission nouvelle la fait de nouveau valider.

### 5.3 Enfermement
- Chaque hôte de module tourne dans un **AppContainer** Windows propre au module (le mécanisme des applications du Store) : il ne voit pas les fichiers de l'utilisateur, et le réseau ne lui est ouvert que par les capacités du tableau 5.2. Les droits de lecture nécessaires (le dossier du module) sont accordés à l'identité de ce module seulement.
- Chaque hôte tourne aussi dans un **job object** Windows qui plafonne sa mémoire et son processeur. Point de départ : 512 Mo de mémoire (au-delà, Windows l'arrête) et 25 % du processeur de la machine. Ce sont des garde-fous contre les énormités, pas la limite de « consommation élevée » (5.9).
- **Joindre un service de ce PC** : Windows interdit à un AppContainer de joindre un serveur du même PC (`localhost`), et l'exemption demande une commande d'administrateur. Demande d'Alex : plus simple. Le **service relaie** donc la connexion : le module demande « ouvre-moi le port 25565 de ce PC », le service (qui n'est pas enfermé) se connecte à `127.0.0.1` sur ce port et fait passer les octets dans les deux sens. Seuls sont relayés les ports **que l'utilisateur a saisis dans les champs « port » des réglages** de la case concernée, et seulement si la permission « Cet ordinateur » est accordée. Le créateur n'écrit donc aucun port en dur : il déclare un champ « port » (avec une valeur proposée, 25565 par exemple), et l'utilisateur le change s'il a plusieurs serveurs ou un autre port. Côté SDK, c'est transparent : le client HTTP du SDK passe par ce relais pour `localhost` et `127.0.0.1`, et une connexion TCP brute s'ouvre avec une méthode du SDK.
- **Conséquence pour l'overlay Discord** : Discord se parle par un tube local et une interface sur `localhost`, tous deux fermés à un module enfermé, et l'accès au vocal demande en plus l'autorisation de Discord. La première version **ne promet donc pas** l'overlay Discord. Il demanderait une source de données Discord fournie par le service, comme les capteurs : une idée pour une version suivante du SDK.
- La protection est forte mais pas absolue : un module autorisé à aller sur Internet peut y envoyer les données qu'on lui confie. L'avertissement reste donc toujours.

### 5.4 Pannes
- Un hôte qui s'arrête est relancé tout seul, avec des attentes croissantes. Au-delà de 3 arrêts en 10 minutes, le module est **mis de côté** : sa case l'affiche, la fenêtre de réglage le dit, et l'utilisateur le relance à la main.
- Un module mis de côté le reste après un redémarrage, jusqu'à ce que l'utilisateur le relance ou le mette à jour.

### 5.5 Le paquet `.avmod`, l'installation et la validation
- Un `.avmod` est une archive ZIP : `manifest.json` à la racine, la DLL du module et ses dépendances, et ses images éventuelles (icône). Le créateur publie à côté son empreinte SHA-256 (comme les versions d'Ashboard Vision).
- **Identifiant** : chaque module a un identifiant unique, de la forme `auteur.module` (par exemple `ashewos.github`). Deux modules du même identifiant ne peuvent pas cohabiter : le second est une mise à jour du premier, s'il vient du même dépôt.
- **Installation** : double-clic sur le `.avmod` (association de fichier posée par l'installateur) ou bouton « Installer un module… ». La fenêtre affiche l'avertissement, la description, l'auteur, la version et les permissions à cocher. Le module est copié dans `<dossier des modules>\<identifiant>\<version>\`.
- **Validation** : le logiciel garde la liste des modules acceptés par l'utilisateur, avec l'empreinte de leurs fichiers et les permissions accordées, dans `%AppData%\ashboard-vision\modules.json` (hors du dossier des modules, que d'autres programmes peuvent écrire). **Un module présent dans le dossier mais absent de cette liste, ou dont les fichiers ont changé, n'est pas lancé** : la fenêtre propose de le valider, avec l'avertissement et les permissions.
- **Qui écrit la liste** : seul le service l'écrit, sur demande de la fenêtre de réglage par le canal de contrôle. Il la scelle avec une clé protégée par Windows (DPAPI, comme le coffre à secrets), et une liste modifiée par un autre programme est refusée. L'empreinte des fichiers est recalculée **au lancement** de l'hôte, juste avant de charger le module.
- **Limite assumée** : un programme malveillant qui tourne déjà sous ton compte peut faire bien pire que déposer un module. Ces protections empêchent un module de s'installer en douce, mais ne protègent pas contre un PC déjà infecté. La documentation le dit.
- **Dossier des modules dans Documents et OneDrive** : si Documents est synchronisé par OneDrive, les fichiers d'un module peuvent n'être présents qu'en ligne. Le service les fait rapatrier avant de calculer l'empreinte. S'il n'y arrive pas, la case l'indique, avec le conseil de choisir un dossier non synchronisé dans les Paramètres.
- **Aucun code d'un module non validé ne tourne**, même enfermé. Tout ce que la fenêtre montre avant la validation (nom, description, auteur, permissions, **formulaire des réglages**) vient du manifeste. Le schéma des réglages est donc écrit dans le manifeste. Les recherches et les actions des champs de réglage n'existent qu'une fois le module validé, quand son hôte tourne.
- **Nom des fichiers publiés** : `<identifiant>-<version>.avmod` et `<identifiant>-<version>.avmod.sha256`, joints à une version GitHub du dépôt nommé dans le manifeste.
- **Conflit d'identifiant** : un `.avmod` qui porte l'identifiant d'un module déjà installé n'est accepté comme mise à jour que s'il nomme le même dépôt GitHub. Sinon, il est refusé avec le message « Un autre module porte déjà cet identifiant » ; l'utilisateur doit d'abord désinstaller l'autre.
- L'empreinte SHA-256 publiée à côté d'un `.avmod` protège d'un fichier abîmé ou d'un téléchargement détourné, pas d'un dépôt de créateur piraté. C'est la raison de l'avertissement permanent et de la validation des permissions nouvelles.
- **Intégrité, hors ligne (demande d'Alex, sans système de clés).** À l'installation, le logiciel calcule l'empreinte de tout le contenu du module et la range dans la liste scellée. **Au moment de l'exécuter**, avant de lancer l'hôte, le service ouvre chaque fichier du module **en interdisant l'écriture et la suppression**, recalcule l'empreinte et la compare à celle de l'installation, puis garde ces fichiers ouverts tant que l'hôte tourne. Un seul octet changé depuis l'installation, et le module n'est pas lancé : il faut le valider de nouveau. Rien de tout cela ne demande Internet.
- **Les secrets** d'un module restent dans le coffre du service : le module les lit et les écrit par le canal, au moment où il en a besoin.
- **Désinstaller un module** retire son dossier, son AppContainer, son coffre à secrets et ses données privées. Désinstaller Ashboard Vision retire aussi les AppContainer et les secrets des modules ; le dossier des modules, dans Documents, suit le choix « garder mes réglages ».

### 5.6 Ce que le SDK offre au créateur
- Une classe de base de module, proche de `IModule` : démarrer, dessiner dans sa case (avec `SKCanvas`), dire « redessine-moi », changer d'état (`Online`, `Offline`, `NotConfigured` avec un message).
- **Le thème** : couleurs, polices, arrondi et facteur de texte du thème choisi, et les outils de dessin de `ModuleDrawing`, pour qu'un module puisse ressembler aux modules d'origine sans y être obligé.
- **Les réglages** décrits avec `SettingsSchema` : liste de choix, case à cocher, texte, sélection multiple, secret, recherche, bouton d'action, et deux champs nouveaux, **nombre** (avec minimum et maximum) et **port** (1 à 65535, avec une valeur proposée). La fenêtre de réglage les dessine comme pour les modules d'origine ; les recherches et actions sont transmises au module. Chaque case placée a ses propres réglages : deux cases du même module peuvent viser deux serveurs différents.
- **Les données du service** (avec la permission) : instantané des capteurs, musique en cours.
- **Les certificats « maison »** : un outil du SDK pour faire confiance à un certificat précis, validé une fois par l'utilisateur dans les réglages, comme le module Réseau le fait pour UniFi.
- **Le rythme** : un module redessine quand il le demande (événement ou minuterie), et le service plafonne le tout par le réglage « images par seconde » de l'écran. Une caméra en direct écrit plusieurs images par seconde, dans la même limite. Il n'y a pas d'autre plafond propre au module (décision d'Alex : guider, puis laisser le créateur optimiser). Le repère « Consommation élevée » et le banc d'essai disent ce que coûte une caméra.
- Le SDK est un paquet NuGet `AshboardVision.Sdk`, en version `1.0`, numéroté à part du logiciel. Sa compatibilité est vérifiée à chaque publication par l'outil de .NET (`EnablePackageValidation`) contre la dernière version publiée.

### 5.7 Mises à jour des modules
- Si le manifeste nomme un dépôt GitHub (`propriétaire/dépôt`), le logiciel y cherche les versions comme pour lui-même (code de `Trofeo.Distribution`) : au démarrage du service, puis toutes les 24 heures, tous modules confondus.
- Une nouvelle version est **proposée, jamais installée en silence** : la fenêtre montre la version, ses notes, et les permissions nouvelles à valider.
- Le fichier téléchargé est vérifié par son empreinte SHA-256 avant d'être installé.

### 5.8 Mode développeur
- Un interrupteur dans les Paramètres. Allumé, il permet de charger un module **depuis son dossier de travail**, sans `.avmod`, et de le recharger d'un clic après chaque modification.
- Un bandeau « Mode développeur actif » reste visible dans la fenêtre tant qu'il est allumé. Éteint, les modules chargés ainsi disparaissent de l'écran.
- Ces modules tournent enfermés comme les autres et portent le point d'exclamation jaune.
- **Permissions en mode développeur** : ajouter un dossier de travail affiche le même avertissement et les mêmes cases à cocher qu'une installation. Si le manifeste du dossier déclare une permission nouvelle, elle est redemandée. L'interrupteur et la liste des dossiers sont rangés dans la liste scellée du service (5.5) : un autre programme ne peut pas les allumer en douce.
- Le module se recharge **sur un clic** (« Recharger »), pas en surveillant le dossier.

### 5.9 Consommation
- Une section « Consommation des modules » dans l'onglet Paramètres : processeur et mémoire de chaque module complémentaire, mesurés par le service via le job object de chaque hôte ; les modules d'origine sur une ligne commune, pour comparer.
- **Repère « Consommation élevée »**, en orange, quand un module dépasse **2 % du processeur ou 150 Mo de mémoire en moyenne sur une minute**. C'est un avertissement, pas un blocage : le module continue. Les seuils sont des points de départ à ajuster après les essais.
- La mesure ne coûte rien quand la section n'est pas affichée, hors un relevé par minute pour le repère.

### 5.10 Banc d'essai
Un outil en ligne de commande livré avec le SDK (outil .NET), qui fait trois choses :
- **`check`** : lance le module **dans les mêmes conditions que le logiciel** (le vrai hôte de module, enfermé, avec les permissions de son manifeste) et vérifie :
  - le manifeste (champs obligatoires, identifiant, version du SDK, permissions connues) ;
  - le démarrage et l'arrêt propres ;
  - le dessin à toutes ses tailles et dans les trois thèmes, avec une image PNG de chaque cas ;
  - le comportement **avec chaque permission refusée**, l'une après l'autre : le module ne doit pas planter ;
  - le temps de dessin, le processeur et la mémoire, avec les mêmes repères que la fenêtre de réglage.

  Il conclut « compatible » ou explique ce qui coince, en français.
- **`pack`** : fabrique le `.avmod` et son empreinte SHA-256.
- `check` et la vérification d'un `.avmod` marchent **hors ligne**.
- L'outil **embarque sa propre copie de l'hôte de module**, de la même version que le SDK : le créateur n'a pas besoin d'installer Ashboard Vision pour tester, et l'outil marche aussi sur les machines Windows de GitHub. Il ne marche que sous Windows, comme le logiciel.
- Des aides de test pour xUnit, que le modèle de projet utilise déjà.

### 5.11 Modèle de projet, documentation, exemples
- `dotnet new ashboard-module` crée un module qui marche, avec son manifeste, ses tests et la commande `check` prête.
- Documentation en français dans `docs/sdk/` : démarrer, le contrat, les permissions, le dessin, les réglages, les tests, publier sur GitHub, la compatibilité.
- **Module d'exemple officiel : « Dépôt GitHub »** (étoiles, dernière version, téléchargements d'un dépôt, jeton facultatif). Alex n'a ni Home Assistant, ni Frigate, ni Proxmox, ni Pi-hole : cet exemple se teste sans rien installer et touche à Internet, aux secrets, aux réglages et aux mises à jour. Un second exemple sans réseau, animé, sert à essayer les images rapides et la consommation.
- **Module d'exemple « Serveur Minecraft »** (idée d'Alex, 01/11/2026 : un serveur Minecraft se monte vite pour tester). Il interroge un serveur Java par le protocole public de la liste des serveurs (« Server List Ping », port 25565 par défaut) et affiche :
  - son nom (le message du jour) et sa version ;
  - les joueurs connectés sur le maximum, et quelques pseudos ;
  - la latence ;
  - l'icône du serveur.

  Ses réglages : l'adresse du serveur et un champ « port » (25565 proposé). Il déclare « Cet ordinateur », « Réseau local » et « Internet » : l'utilisateur ne coche que ce qui correspond à son serveur. C'est l'exemple qui éprouve le relais vers ce PC, le réseau local, les images (l'icône) et les états « en ligne / hors ligne ». Un serveur lancé sur le PC d'Alex suffit pour l'essai.

### 5.12 Distribution du SDK (décision d'Alex)
- Pas de nuget.org. Les paquets du SDK (`AshboardVision.Sdk`, ses aides de test, le banc d'essai et le modèle de projet) sont **joints à chaque version GitHub** d'Ashboard Vision, construits par la recette de publication.
- L'installateur d'Ashboard Vision les embarque et propose, à la page des options, **« Outils pour créer des modules (SDK) »**, **décochée par défaut**. Lors d'une mise à jour, la case reprend le choix de l'installation précédente : cochée si le SDK était installé.
- Cochée, elle copie les paquets dans le dossier de l'application et, si le SDK .NET 10 est présent sur le PC, ajoute ce dossier comme source de paquets de l'utilisateur, installe le modèle de projet et l'outil `ashboard-module`, avec les droits de l'utilisateur. Sans SDK .NET, un message dit de l'installer pour créer des modules. La désinstallation retire la source, le modèle et l'outil.

### 5.13 Découpage
Un projet plus gros que le plan 6, en deux parties :
- **Partie A — le moteur et la sécurité** : hôte de module, canal, mémoire partagée, AppContainer, job objects, permissions, pannes, `.avmod`, installation et validation, fenêtre de réglage (avertissements, permissions, point d'exclamation, consommation), mises à jour, mode développeur.
- **Partie B — le SDK et le banc d'essai** : paquet `AshboardVision.Sdk`, modèle de projet, `check` et `pack`, documentation, modules d'exemple.

## 6. Ce qui reste au constructeur

- Le détail des messages du canal et leur format exact, tant qu'ils sont versionnés et documentés.
- La façon de mesurer le processeur et la mémoire, tant qu'elle ne coûte rien sans module complémentaire.
- Le délai au-delà duquel une case passe en erreur quand son module ne répond plus.
- L'organisation des projets dans la solution (noms `Trofeo.*` à l'intérieur, nom `AshboardVision.Sdk` pour ce que voient les créateurs).
- Les textes exacts des avertissements, dans l'esprit d'Alex : clairs, qui tutoient, qui font attention sans faire peur.
