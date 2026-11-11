# Plan 8 — Vues multiples et raccourcis : conception

*Écrite le 11/11/2026 avec Alex. Elle complète la spec d'origine (`2026-10-24-trofeo-dashboard-design.md`) et les conceptions des plans 6 et 7 ; elle les remplace là où elles diffèrent. Les choix marqués **(choix de Claude)** n'ont pas été demandés par Alex : il peut les revoir en relisant.*

## 1. Intention

Aujourd'hui, l'écran n'a qu'une disposition. Pour voir autre chose (le serveur Minecraft pendant une partie, Home Assistant le soir), il faut retirer des modules et en placer d'autres. Alex veut **plusieurs vues**, comme les bureaux virtuels de Windows : il prépare une fois chaque vue avec les modules qu'il veut, puis passe de l'une à l'autre au clavier, `Ctrl+Alt+Gauche` et `Ctrl+Alt+Droite`.

Ces deux raccourcis servent aujourd'hui à changer d'onglet dans un module (les sessions de Claude Code) : ils passent sur `Ctrl+Alt+Haut` et `Ctrl+Alt+Bas`.

Alex veut aussi que **les créateurs de modules complémentaires puissent déclarer des raccourcis** pour des actions de leur module, proposés à l'utilisateur, vérifiés contre les conflits, et visibles dans l'onglet Raccourcis comme les autres.

Enfin, comme un même module peut maintenant se trouver plusieurs fois (dans plusieurs vues, ou deux fois dans la même), Alex veut pouvoir **donner un nom à chaque case** pour s'y retrouver.

## 2. Objectifs, non-objectifs, anti-objectifs

**Objectifs**
- Plusieurs **vues**, chacune avec sa disposition, préparées dans l'onglet Disposition et choisies au clavier ou depuis la fenêtre.
- Un **passage animé** d'une vue à l'autre si l'écran le rend fluide ; sinon le nouvel écran s'affiche d'un coup.
- Les raccourcis des **onglets de module** sur `Ctrl+Alt+Haut` / `Bas`, ceux des **vues** sur `Ctrl+Alt+Gauche` / `Droite`.
- Des **actions de module complémentaire** déclarées dans le manifeste, avec un raccourci proposé par le créateur, choisies par l'utilisateur, et la **vérification des conflits**.
- Un **nom** facultatif pour chaque case.
- Une nouvelle version du **SDK** (1.1.0), sa documentation et ses exemples.

**Non-objectifs**
- Changer de vue tout seul (à une heure, au lancement d'un jeu, à l'arrivée d'une alerte) **(choix de Claude)** : on pourra le faire plus tard, les vues le permettront.
- Une apparence (thème, fond) propre à chaque vue : l'apparence reste commune **(choix de Claude)**.
- Plusieurs écrans branchés en même temps, d'autres écrans Thermalright (idées notées aux bilans 6 et 7).
- Les commandes d'onglets (`Ctrl+Alt+Haut` / `Bas`) pour les modules complémentaires (Alex : un même raccourci qui touche plusieurs modules perdrait l'utilisateur).

**Anti-objectifs** (ce qui ferait échouer le plan même si tout « marche »)
- Dessiner une vue qui n'est pas sur l'écran, ou laisser un module animé d'une vue cachée faire tourner l'écran à pleine cadence.
- Un raccourci qui agit sur un module d'une vue cachée.
- Un module complémentaire qui voit les touches tapées au clavier.
- Une touche prise en silence : un raccourci de module qui écrase un raccourci existant sans que l'utilisateur le sache.
- Perdre la disposition, les raccourcis ou les réglages d'Alex à la mise à jour.
- Un logiciel plus gourmand avec une seule vue qu'aujourd'hui.

## 3. Contraintes

- Celles du projet (`CLAUDE.md`) : .NET 10, SkiaSharp, Avalonia pour la fenêtre, xUnit, Debug seulement sur le PC d'Alex, `Canary` comme branche de travail, rien de poussé sans son accord, textes en français qui tutoient.
- Les réglages restent dans `%AppData%\ashboard-vision\layout.json`. Le fichier d'aujourd'hui (un seul tableau `Placements`) doit être lu et converti sans perte.
- Le canal de contrôle passe en **protocole 4** ; une fenêtre et un service de versions différentes le disent déjà (message existant).
- Le protocole entre le service et l'hôte des modules change aussi : un module construit avec le SDK 1.0.0 doit continuer à tourner sans être reconstruit.
- Le SDK reste hors de nuget.org (paquets joints aux versions GitHub et posés par l'installateur).
- Les règles de sécurité du plan 7 tiennent : un module complémentaire n'a rien de plus que ce que l'utilisateur lui accorde, et tout ce qu'il envoie est une entrée hostile.

## 4. Ce qu'Alex a décidé (11/11/2026)

### 4.1 Les vues

- On prépare les vues dans la fenêtre de réglage, comme la disposition aujourd'hui, avec la possibilité d'en ajouter.
- **Seule la vue affichée est dessinée.** Les modules des autres vues **continuent de récupérer leurs données** (c'est peu coûteux) : en revenant sur une vue, les courbes et les fils sont à jour.
- Le passage d'une vue à l'autre est **animé si l'écran le rend fluide** ; sinon le nouvel écran s'affiche directement.

### 4.2 Dans la fenêtre

- **Des onglets de vues au-dessus de l'éditeur** (maquette A) : une vue à la fois en grand, un point vert sur celle qui est sur l'écran, et « + Nouvelle vue ».
- **Cliquer sur l'onglet d'une vue fait passer l'écran sur elle** (pour voir le résultat en vrai). Une option permet de désactiver ce suivi.
- **Chaque case peut recevoir un nom**, pour s'y retrouver quand un même module est placé plusieurs fois.

### 4.3 Les raccourcis

- `Ctrl+Alt+Haut` / `Ctrl+Alt+Bas` : onglet précédent / suivant d'un module (aujourd'hui sur Gauche / Droite).
- `Ctrl+Alt+Gauche` / `Ctrl+Alt+Droite` : vue précédente / suivante.
- **Un raccourci n'agit que sur la vue affichée** : les modules d'une vue cachée ne reçoivent rien.
- Les commandes d'onglets restent **réservées aux modules d'origine**.

### 4.4 Les raccourcis des modules complémentaires

- Le créateur **déclare** les actions de son module, chacune pouvant avoir un **raccourci par défaut**.
- Ces raccourcis sont **proposés à l'utilisateur** à l'installation du module, et à une mise à jour qui en ajoute.
- Les **conflits** de raccourcis sont vérifiés.
- Les raccourcis des modules sont **visibles dans l'onglet Raccourcis**, où on les ajoute et les retire avec les boutons dédiés.

## 5. Choix de Claude

### 5.1 Ce qu'est une vue

- Une vue a un identifiant stable, un **nom** (modifiable, 30 caractères au plus) et sa liste de cases. Les vues sont ordonnées ; l'ordre est celui des onglets et des raccourcis.
- **Au plus 9 vues**, une par chiffre (« Vue n° 1 » à « Vue n° 9 »). Il en reste toujours au moins une.
- Sont **communs à toutes les vues** : l'étagère (modules retirés), les raccourcis, l'apparence, la rotation, le plafond d'images par seconde.
- Un même type de module peut être placé dans plusieurs vues ; **chaque case a ses propres réglages**, comme deux cases du même module aujourd'hui. Les sources de données partagées (capteurs, musique en cours, Freebox…) restent partagées : deux cases Réseau n'ouvrent pas deux connexions de plus qu'aujourd'hui.
- **Au démarrage**, l'écran reprend la dernière vue affichée (enregistrée à chaque changement, sans écrire le fichier plus d'une fois par seconde).
- **Migration** : un `layout.json` sans vues devient une seule vue nommée « Vue 1 » qui contient ses cases ; rien d'autre ne change. Un retour à une ancienne version (canal Stable) passe déjà par une copie des réglages (plan 6) ; il n'est pas demandé à une ancienne version de relire le nouveau format.

### 5.2 Le service : dessiner seulement la vue affichée

- Toutes les cases de toutes les vues sont **créées et démarrées** (elles récupèrent leurs données), mais **seules celles de la vue affichée sont dessinées**. L'animation (`IsAnimating`) et les demandes de dessin (`Invalidated`) d'une case cachée ne font jamais tourner l'écran ; elles marquent seulement sa vue comme « à redessiner » pour le prochain passage.
- **Modules complémentaires** : aujourd'hui, une case n'est ouverte dans l'hôte qu'à son premier dessin. Une case d'une vue cachée doit pourtant tourner (données à jour). Elle est donc ouverte dès son démarrage, à la taille de sa case, et l'hôte apprend par un nouveau message qu'elle est **cachée** : il ne la dessine pas tant qu'elle l'est (une demande de dessin reste en attente), et la redessine quand elle redevient visible. Un module animé caché ne dessine donc rien.
- Un module complémentaire placé seulement dans des vues cachées a donc son hôte qui tourne (pour ses données), mais qui dort côté dessin.
- **Sobriété attendue** : avec une seule vue, rien ne change par rapport à la 1.1.0. Avec plusieurs, le surcoût est celui des données des modules cachés, mesuré à la fin du plan.

### 5.3 Le passage d'une vue à l'autre

- Un **glissement horizontal** dans le sens de la touche (Droite : la nouvelle vue arrive par la droite), d'environ 300 ms, composé à partir de la dernière image de l'ancienne vue et d'une image fraîche de la nouvelle.
- Pendant le glissement, l'écran va aussi vite que l'envoi le permet (environ 20 images/s mesurées au plan 7 : 30 ms de rendu, 13 ms d'envoi), puis revient à son rythme normal.
- **Décision par la mesure**, sur l'écran d'Alex, à la dernière tâche du plan : si le glissement ne passe pas au moins 5 images en 300 ms, ou s'il saccade à l'œil, il est retiré et le nouvel écran s'affiche d'un coup. S'il est gardé, une case « Animer le passage entre les vues » dans Personnalisation permet de le couper.
- Un deuxième appui pendant un glissement l'abandonne et part vers la vue suivante (pas de file d'attente de glissements).

### 5.4 Les raccourcis du logiciel

- Nouveaux réglages par défaut : `Ctrl+Alt+Haut` / `Bas` → onglet précédent / suivant ; `Ctrl+Alt+Gauche` / `Droite` → vue précédente / suivante, **en boucle** (après la dernière vue, la première) ; `Ctrl+Alt+Maj+1` à `9` gardent « Onglet n° N ».
- Nouvelles actions dans l'onglet Raccourcis : « Vue précédente », « Vue suivante », « Vue n° 1 » à « Vue n° 9 » (sans touche par défaut).
- **Migration des raccourcis existants** : les liaisons identiques aux anciennes valeurs par défaut (`Ctrl+Alt+Left` → onglet précédent, `Ctrl+Alt+Right` → onglet suivant) passent sur Haut / Bas, puis Gauche / Droite reçoivent les vues. Une liaison qu'Alex a changée lui-même est gardée telle quelle, et les vues ne reçoivent Gauche / Droite que si ces touches sont libres.
- Une commande d'onglet sans case visée va au premier module à onglets **de la vue affichée** ; une commande qui vise une case précise ne fait rien si cette case est dans une vue cachée.

### 5.5 Les actions des modules complémentaires

- **Manifeste** : un nouveau tableau facultatif `actions`, chaque action avec un `id` (même règle que les identifiants de réglages), un `name` lisible (nettoyé comme les autres textes du manifeste) et un `shortcut` facultatif (même syntaxe que les raccourcis du logiciel). **10 actions au plus** par module.
- **SDK 1.1.0** : `CommunityModule` reçoit une méthode virtuelle appelée quand une action est demandée pour cette case, avec l'identifiant de l'action. Un module construit avec le SDK 1.0.0 continue de tourner sans rien changer ; un module qui déclare des actions demande au moins la version du logiciel qui les comprend (règle de compatibilité du plan 7).
- **Le module ne voit jamais le clavier** : c'est le service qui écoute les touches (comme aujourd'hui) et qui transmet « action X demandée » à l'hôte. Il y a au plus **5 actions transmises par seconde et par module**, pour qu'une touche tenue enfoncée n'inonde pas l'hôte.
- **Qui reçoit l'action** : les cases de ce module **dans la vue affichée**. Une liaison peut viser une case précise, désignée par son nom dans l'onglet Raccourcis ; sans case visée, toutes les cases du module dans la vue affichée la reçoivent. Si aucune case du module n'est affichée, rien ne se passe.
- **À l'installation** (et à la mise à jour qui ajoute des actions), le dialogue montre une section « Raccourcis proposés » : chaque action avec sa touche par défaut et une case à cocher. Une touche **libre** arrive cochée ; une touche **déjà prise** (par le logiciel ou un autre module) arrive décochée, avec le nom de ce qui la tient. Les raccourcis ne sont pas des permissions : ils ne donnent aucun accès, d'où la case cochée par défaut quand la touche est libre. Une action sans touche par défaut n'est pas montrée là ; on peut toujours lui en donner une dans l'onglet Raccourcis.
- À une mise à jour, seules les **nouvelles** actions sont proposées ; les liaisons existantes sont gardées. Une action qui disparaît du manifeste perd ses liaisons.
- **Désinstaller** un module retire ses liaisons. Un module **mis de côté** garde les siennes, mais elles ne font rien tant qu'il l'est.

### 5.6 L'onglet Raccourcis et les conflits

- L'onglet liste toutes les liaisons, celles des modules complémentaires comprises, marquées du **point d'exclamation jaune** comme partout ailleurs.
- La liste des actions qu'on peut choisir comprend celles des modules complémentaires installés, groupées par module.
- **Conflit dans le logiciel** : deux liaisons ne peuvent pas avoir la même touche. Ajouter une touche déjà prise est refusé, avec le nom de ce qui la tient.
- **Conflit avec un autre logiciel de Windows** : le service sait quand Windows refuse d'enregistrer une touche (déjà réservée ailleurs). L'onglet le montre sur la liaison concernée (« Touche déjà prise par un autre logiciel »).

### 5.7 Les noms de cases

- Chaque case peut recevoir un nom (30 caractères au plus), modifiable dans le panneau de réglage de la case.
- Le nom remplace celui du module dans les listes de la fenêtre (« Sur l'écran », cible d'un raccourci). Il ne s'affiche pas sur l'écran.

### 5.8 L'onglet Disposition

- Les onglets de vues sont au-dessus de l'éditeur, avec le point vert sur la vue affichée et « + Nouvelle vue ».
- Sur un onglet, un menu permet de **renommer**, **dupliquer** (la vue et ses cases, avec des réglages copiés) et **supprimer** (avec une confirmation si la vue a des cases ; ses modules partent à l'étagère). On réordonne les onglets en les faisant glisser.
- La case « **L'écran suit la vue choisie** » est près des onglets, cochée par défaut. C'est une préférence de la fenêtre, gardée avec ses autres préférences.
- Les opérations sur les vues entrent dans l'historique Annuler / Rétablir existant.
- La liste « Sur l'écran » montre les cases de la vue choisie.
- L'assistant du premier lancement ne change pas : il prépare une seule vue.

### 5.9 Documentation et exemples

- Une nouvelle page `docs/sdk/raccourcis.md` (déclarer une action, la recevoir, la tester), et les pages du manifeste, de la compatibilité et du contrat mises à jour.
- **Démo** reçoit une action « Changer de couleur » avec un raccourci par défaut ; **Dépôt GitHub** reçoit « Rafraîchir » sans raccourci par défaut.
- Les aides de test du SDK permettent de déclencher une action ; le banc d'essai vérifie que les actions déclarées sont bien reçues et qu'aucune ne fait planter le module.
- Le README présente les vues et la nouvelle répartition des raccourcis.

### 5.10 Précisions après relecture (choix de Claude)

- **L'hôte est celui du logiciel.** Les messages « case cachée / visible » et « action demandée » vont à `Trofeo.ModuleHost`, livré avec le logiciel ; le module n'est qu'une bibliothèque chargée par lui. Un module construit avec le SDK 1.0.0 reçoit donc le nouveau comportement (pas de dessin quand il est caché) sans rien changer ; il n'a simplement pas d'actions.
- **Cases, étagère et vues.** L'étagère contient des cases avec leurs réglages, comme aujourd'hui. « Retirer de l'écran » agit sur une case, jamais sur toutes celles du même module. Supprimer une vue envoie ses cases à l'étagère avec leurs réglages et leur identifiant. Dupliquer une vue donne de nouveaux identifiants aux copies.
- **Raccourcis visant une case.** Une liaison qui vise une case la suit partout : rangée à l'étagère ou dans une vue cachée, elle ne fait rien, sans erreur. Elle n'est retirée que si la case est supprimée pour de bon (retirée de l'étagère, module désinstallé). Annuler / Rétablir remet les cases et leurs liaisons ensemble. Les copies d'une vue dupliquée ne reprennent pas les liaisons des originaux.
- **Touches refusées par Windows à l'installation.** Pour dire qu'une touche est libre, le dialogue demande au service, qui essaie de l'enregistrer auprès de Windows puis la relâche : une touche réservée par un autre logiciel arrive décochée, avec « Touche déjà prise par un autre logiciel ».
- **Une case qui redevient visible** montre d'abord sa dernière image (le glissement part d'elle), puis la nouvelle dès que l'hôte l'a dessinée. Une case qui n'a jamais dessiné reste vide, comme au premier lancement aujourd'hui, avec le message « Le module ne répond pas » au-delà du délai existant.
- **Plafond des actions.** Les 5 actions par seconde se comptent par appui de touche et par module, quel que soit le nombre de cases qui la reçoivent. Une action demandée pendant que l'hôte ne tourne pas (relance, mis de côté, pas encore lancé) est abandonnée, sans file d'attente.
- **« Vue n° N » sans vue N** ne fait rien. Ces neuf actions sont toujours proposées dans l'onglet Raccourcis.
- **La vue affichée appartient au service.** La fenêtre envoie des commandes (« afficher la vue X ») par le canal de contrôle, qui lui annonce en retour chaque changement de vue, ceux du clavier compris. La dernière vue affichée est gardée par le service dans un petit fichier à lui, à côté de `layout.json`, qui reste écrit par la fenêtre seule ; ainsi les deux ne se disputent jamais un fichier.
- **Liaisons d'un module mis de côté** : elles gardent leur touche, qui compte pour les conflits ; elles ne font juste rien tant que le module est de côté.

### 5.11 Découpage et publication

- **Partie A** : les vues (format, service, passage, fenêtre, noms de cases, raccourcis du logiciel).
- **Partie B** : les actions des modules complémentaires (manifeste, protocole de l'hôte, SDK 1.1.0, dialogue, onglet Raccourcis, documentation, exemples).
- Publication en **1.2.0**, d'abord en bêta, essayée par Alex sur l'écran, avec la mesure du glissement et de la sobriété (une vue, puis trois).

## 6. Ce qui reste au constructeur

- Le schéma exact de `layout.json` (noms des champs des vues et de la vue affichée).
- La représentation des actions de vue dans les liaisons (aujourd'hui `ModuleCommandKind` ne connaît que des commandes de module).
- La forme exacte des nouveaux messages entre le service et l'hôte (case cachée ou visible, action demandée) et du protocole de contrôle 4.
- La technique de composition du glissement, et où mesurer sa cadence.
- La façon dont la fenêtre affiche une touche refusée par Windows.
