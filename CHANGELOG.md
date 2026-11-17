# Journal des changements

Les versions sont numérotées selon [SemVer](https://semver.org/lang/fr/) : les bêtas s'écrivent `1.0.0-beta.N`, les stables `1.0.0`, `1.1.0`, etc. Le texte de chaque section sert de notes à la version GitHub du même numéro.

## 1.2.0-beta.3

Troisième bêta de la 1.2.0 : le logiciel peut maintenant **dessiner l'image et décoder les vidéos avec la carte graphique**, par Direct3D 12, quelle que soit la marque de la carte. C'est ce qui doit alléger le thème Liquid Glass avancé sur un fond animé. Les vues, les noms de cases, les raccourcis et les thèmes des bêtas précédentes sont inchangés.

### L'accélération matérielle

- Le **dessin de l'image** passe sur la carte pour les quatre thèmes : fond (image, GIF, Aurore, vidéo), panneaux, modules, verre de Liquid Glass et de Liquid Glass avancé (ombres et pastilles comprises), glissement entre les vues avec son flou. Le verre avancé se calcule alors sur la carte, au lieu d'être réparti sur les cœurs du processeur.
- Le **décodage des vidéos** passe aussi sur la carte (Media Foundation, sur la même carte que le dessin). Quand les deux sont activés, l'image décodée va au dessin sans repasser par le processeur.
- La compression JPEG de l'image envoyée à l'écran, elle, reste sur le processeur : elle coûte peu.
- C'est une API standard de Windows, pas du code propre à un fabricant : Intel, NVIDIA, AMD ou autre, toute carte compatible Direct3D 12 convient (niveau de fonctionnalités 11_0 au moins).
- Sans carte compatible (machine virtuelle, vieux PC, pilote de base), rien ne change : tout reste calculé sur le processeur, comme avant.
- Sans accélération, Classique, Liquid Glass, Pixel et Liquid Glass avancé restent identiques au pixel près à la beta.2. Sur la carte, l'image ne diffère de celle du processeur que de quelques 255<sup>es</sup> par couleur : ça ne se voit pas.

### La section « Accélération matérielle » dans Paramètres

- Dans **Paramètres**, une nouvelle section, après « Images par seconde », est activée par défaut.
- L'interrupteur **Utiliser la carte graphique** coupe ou rallume tout. Coupé, la liste et les cases sont grisées et tout se calcule sur le processeur.
- La liste **Carte** propose « Automatique » suivi de chaque carte trouvée. **Automatique** prend la première carte matérielle dans l'ordre « économie d'énergie » de Windows, en général **la carte intégrée** : la carte dédiée n'est pas tenue éveillée pour rien.
- Deux cases : **Dessin de l'image (thèmes, verre, fonds animés)** et **Décodage des vidéos**. Tu peux les cocher dans toutes les combinaisons.
- Une ligne d'état dit ce que le logiciel fait vraiment (« Sur Intel(R) Graphics : dessin et vidéos. »). En cas de problème, une phrase en couleur d'avertissement en donne la raison. Sans carte compatible, l'interrupteur est grisé : « Aucune carte graphique compatible : tout se calcule sur le processeur. »
- Les changements s'appliquent tout de suite, sans redémarrer le service. Si une vidéo joue, elle est rouverte avec le nouveau décodeur et reprend à sa position, sans image noire entre les deux.
- Une carte que tu as choisie est retrouvée d'un démarrage à l'autre par son fabricant, son modèle et son nom. Si elle a disparu (carte retirée, `layout.json` copié d'un autre PC), le logiciel prend Automatique et te le dit dans la fenêtre.
- Le canal entre la fenêtre et le service passe au **protocole 5** : la fenêtre et le service doivent être de la même version, ce qui est toujours le cas avec l'installateur.

### Le repli sur le processeur

- Si la carte a un problème (pilote mis à jour ou planté, carte désactivée, programme de verre refusé), l'image en cours est **refaite sur le processeur** : l'écran ne reste jamais noir ni figé. La fenêtre le dit, avec la raison.
- Le logiciel **retente la carte toutes les 5 minutes**, au plus 3 fois de suite. Le compte repart de zéro après une heure sans problème. Un changement des réglages relance aussi un essai.
- En **Automatique**, une carte qui met plus de 83 ms par image en moyenne pendant 10 s (le temps d'une image à 12 images par seconde) est jugée trop lente : le logiciel passe à l'autre carte matérielle s'il y en a une, puis au processeur. Une carte que tu as choisie toi-même est toujours gardée ; la fenêtre signale seulement qu'elle est lente.
- Une vidéo que la carte refuse de décoder (format non pris en charge) est décodée par le processeur, sans toucher au dessin, jusqu'à ce que tu changes de carte.

### Des réglages propres à ce PC

- Les réglages sont dans le champ `Acceleration` de `layout.json` (voir `docs/utilisation-avancee.md`). Un fichier qui n'en a pas prend les valeurs par défaut : activée, carte Automatique, dessin et décodage cochés.
- Ils sont propres à la machine : **« Exporter la disposition » ne les emporte pas**, et importer une disposition garde les réglages d'accélération de ce PC.

### Ce que ça change pour le processeur

- Le coût du thème Liquid Glass avancé sur un fond animé avec l'accélération est **à mesurer** sur le PC d'Alex (pour mémoire : environ 14,5 % du processeur sans accélération, à la beta.2). Les chiffres seront ajoutés ici.

### Licences

- Le dessin et le décodage sur la carte passent par [Vortice.Direct3D12, Vortice.DXGI et Vortice.Direct3D11](https://github.com/amerkoleci/Vortice.Windows) (MIT), cités dans le README, section « Licences tierces », et dans le fichier de licences copié à côté du programme.

## 1.2.0-beta.2

Deuxième bêta de la 1.2.0 : un quatrième thème, **Liquid Glass avancé**, dont le verre courbe vraiment le fond sur ses bords. Les vues, les noms de cases et les raccourcis de la beta.1 sont inchangés.

### Le thème Liquid Glass avancé

- Dans **Personnalisation**, choisis le thème « Liquid Glass avancé ». Chaque case devient une plaque de verre : le fond se courbe sur les bords (réfraction), avec un léger flou, un reflet sur l'arête, une ombre et, au besoin, des franges colorées.
- Quatre **préréglages** : *Clair* (par défaut), *Givré* (plus flou, plus laiteux), *Prisme* (réfraction forte, franges colorées) et *Discret* (un verre à peine marqué).
- Dans **Personnalisation**, une section **Verre** apparaît sous le fond quand ce thème est choisi. Elle propose les préréglages, et un repli « Réglages fins » avec la forme du biseau (Pilule ou Dôme) et seize curseurs en quatre groupes : verre (flou, réfraction, profondeur du biseau, rayon des angles, micro-déformation), lumière (lumière des bords, reflets, Fresnel, franges de couleur), couleur (opacité du verre, saturation, teinte froide, luminosité) et ombre (opacité, étendue, décalage). Dès que tu lâches un curseur, le verre passe sur « Personnalisé » ; choisir un préréglage rétablit ses valeurs.
- La garde de lisibilité garde son rôle : sur un fond clair, la teinte s'assombrit pour que le texte reste lisible, sans clignoter quand le fond bouge. Tu peux la couper comme pour les autres thèmes.
- Le verre se calcule une fois par case, puis il est gardé tant que le fond, la taille de la case et les réglages ne changent pas. Sur un fond animé ou une vidéo, il est recalculé à chaque image du fond, réparti sur plusieurs cœurs du processeur pour rester fluide : ce thème y demande alors nettement plus de processeur que les autres.
- Classique, Liquid Glass et Pixel ne changent pas, au pixel près.
- Si tu construis le logiciel depuis le dépôt, tu peux aussi le choisir en ligne de commande, avec `--appearance theme=liquid-glass-avance` (voir `docs/utilisation-avancee.md`).

### Une pastille de verre pour l'élément choisi

- Dans ce thème, l'onglet choisi de Claude Code et de Codex est surligné par une **vraie pastille de verre**, pas par un simple aplat. Elle passe sous le texte, qui reste net.
- Les modules complémentaires qui appellent `DrawSelection` (nouveau dans le SDK 1.2.0) ont la même pastille : c'est le logiciel qui la dessine, jamais le module. Un module ne reçoit toujours ni les pixels du fond ni ceux d'une autre case.

### SDK 1.2.0

- `ModuleDrawing.DrawSelection` est public : un module complémentaire note l'élément choisi (un onglet, une ligne…), et le thème décide de la façon de le montrer. Dans les autres thèmes, le dessin reste celui d'avant.
- Un module qui appelle `DrawSelection` annonce `"sdk": "1.2"` dans son manifeste ; les autres gardent leur `"sdk"`. Un module construit avec le SDK 1.0.0 ou 1.1.0 tourne sans rien changer.
- Les aides de test dessinent la pastille comme le logiciel, et la documentation (`docs/sdk`) décrit `DrawSelection`.

### Licence

- Le verre adapte l'effet de [liquidglass](https://github.com/ybouane/liquidglass) (licence MIT, « Copyright (c) ybouane »). Le texte de la licence est copié à côté du programme et cité dans le README, section « Licences tierces ».

## 1.2.0-beta.1

Première bêta de la 1.2.0 : les **vues multiples**, des **noms de cases**, et des **raccourcis pour les modules complémentaires**.

### Vues

- Tu peux préparer jusqu'à **9 vues**, chacune avec ses propres cases. L'écran en affiche une à la fois.
- `Ctrl+Alt+Gauche` et `Ctrl+Alt+Droite` passent à la vue précédente ou suivante, en boucle. Les actions « Vue n° 1 » à « Vue n° 9 » sont dans l'onglet Raccourcis, sans touche par défaut.
- Le passage d'une vue à l'autre glisse, avec un léger flou de mouvement. Tu peux le couper dans **Personnalisation** : « Animer le passage entre les vues ».
- Dans **Disposition**, les vues sont des onglets au-dessus de l'éditeur. Un point vert montre celle qui est sur l'écran. « + Nouvelle vue » en ajoute une. Le menu d'un onglet permet de la renommer, de la dupliquer ou de la supprimer, et tu réordonnes les onglets en les faisant glisser. Annuler et Rétablir marchent aussi sur les vues.
- Cliquer sur l'onglet d'une vue fait passer l'écran sur elle. Décoche « L'écran suit la vue choisie » pour préparer une vue sans la montrer.
- Seule la vue affichée est dessinée. Les modules des autres vues continuent de récupérer leurs données : en revenant sur une vue, tout est à jour. Avec une seule vue, rien ne change.
- Au démarrage, l'écran reprend la dernière vue affichée.
- Ta disposition actuelle devient la vue « Vue 1 ». L'étagère, les raccourcis et l'apparence sont communs à toutes les vues.

### Raccourcis

- Les onglets d'un module passent sur `Ctrl+Alt+Haut` et `Ctrl+Alt+Bas`. `Ctrl+Alt+Gauche` et `Ctrl+Alt+Droite` vont aux vues.
- Si tu avais changé ces raccourcis toi-même, tes choix sont gardés. Les vues ne reçoivent Gauche et Droite que si ces touches sont libres.
- Un raccourci n'agit que sur la vue affichée : les modules d'une vue cachée ne reçoivent rien.

### Noms de cases

- Chaque case peut recevoir un nom : clique sur le crayon à côté de son titre, dans Disposition ou dans Modules. Le nom s'affiche partout dans la fenêtre, avec le nom du module en petit dessous. C'est pratique quand un même module est placé deux fois. Le nom ne s'affiche pas sur l'écran.
- Avec plusieurs vues, l'onglet **Modules** range les cases en dossiers, un par vue. Un clic sur un dossier le replie.

### Raccourcis des modules complémentaires

- Un module complémentaire peut déclarer des actions, chacune avec une touche proposée. À l'installation, la fenêtre te montre les **raccourcis proposés**. Une touche libre arrive cochée. Une touche déjà prise arrive décochée, avec le nom de ce qui la tient, même quand c'est un autre logiciel de Windows.
- Les raccourcis des modules complémentaires sont dans l'onglet **Raccourcis**, avec le point d'exclamation jaune. Tu peux en ajouter, en retirer, ou viser une case précise par son nom.
- Un module ne voit jamais ton clavier : c'est le service qui écoute les touches et qui transmet l'action, au plus 5 fois par seconde.
- Désinstaller un module retire ses raccourcis.

### SDK 1.1.0

- `CommunityModule.OnShortcutAsync` reçoit les actions déclarées dans le manifeste (`"shortcuts"`). Les aides de test permettent de les déclencher, et `ashboard-module check` vérifie qu'aucune ne fait planter le module.
- Un module construit avec le SDK 1.0.0 tourne sans rien changer.
- Nouvelle page de documentation : `docs/sdk/raccourcis.md`. L'exemple Démo reçoit « Changer de couleur » (`Ctrl+Alt+Maj+D`), et Dépôt GitHub reçoit « Rafraîchir », sans touche par défaut.

## 1.1.0

La 1.1.0 apporte les **modules complémentaires**, créés par la communauté, et le **SDK** pour en écrire. Elle reprend la `1.1.0-beta.1`, essayée sur l'écran.

### Modules complémentaires

- Un module complémentaire est un fichier `.avmod`. Tu l'installes d'un double-clic, ou depuis **Paramètres, section Modules complémentaires, « Installer un module… »**.
- Avant l'installation, la fenêtre te montre ce que le module demande. Tu accordes ses permissions **une par une** : réseau local, Internet, un service de ce PC sur les ports que tu choisis, capteurs, musique en cours.
- Chaque module tourne dans **son propre processus, enfermé par Windows** (AppContainer et job object). S'il plante, l'écran et les autres modules continuent.
- Le logiciel enregistre l'empreinte du module à l'installation et la revérifie à chaque lancement, même hors ligne. Un module modifié depuis ta validation ne démarre pas. Ses fichiers sont verrouillés pendant qu'il tourne.
- Un **point d'exclamation jaune** signale les modules complémentaires partout dans la fenêtre de réglage.
- Les mises à jour d'un module sont cherchées sur GitHub et **te sont proposées**. Elles ne se téléchargent que si tu cliques.
- Paramètres montre ce que consomme chaque module, et signale ceux qui consomment beaucoup. Un module qui ne s'anime pas ne coûte presque rien. Un module animé fait tourner l'écran à sa cadence maximale, ce qui demande un peu plus de processeur.
- Les modules sont rangés dans `Documents\Ashboard Vision\Modules`. Tu peux changer ce dossier.
- Un mode développeur permet d'essayer un module en cours d'écriture.
- Sans module complémentaire, rien ne tourne en plus : le logiciel reste aussi discret qu'avant.
- À la désinstallation, si tu effaces tes réglages, le dossier des modules est effacé aussi, s'il est à son emplacement par défaut. Les secrets des modules (clés, jetons) sont effacés dans tous les cas, même si tu gardes tes réglages.

> **Avertissement.** Un module complémentaire n'est pas fait par Ashboard Vision et **peut contenir du code malveillant**. L'enfermement limite les dégâts, mais ne les exclut pas. Installe seulement un module dont tu connais et fais confiance à l'auteur.

### SDK pour créer des modules

- Le SDK permet d'écrire un module en C# : un modèle de projet (`dotnet new ashboard-module`), des aides de test et un banc d'essai (`ashboard-module check` et `ashboard-module pack`).
- L'installateur le propose à la page des options : « Outils pour créer des modules (SDK) », décoché par défaut. S'il ne trouve pas de SDK .NET 10 sur ton PC, il télécharge la dernière version chez Microsoft et l'installe, après avoir vérifié son empreinte et sa signature. La barre de l'installateur suit l'installation paquet par paquet.
- Les quatre paquets du SDK sont aussi joints à chaque version GitHub. Ils ne sont pas publiés sur nuget.org.
- Toute la documentation est en français, dans `docs/sdk`.
- Le dépôt contient trois modules d'exemple, dans `examples` : Dépôt GitHub, Démo et Serveur Minecraft.

### Corrections

- Dans l'onglet Disposition, cliquer sur un module de la liste « Ajouter un module » l'ajoute de nouveau. Depuis la 1.0.0, le clic refermait la liste sans rien ajouter.

## 1.1.0-beta.1

Première bêta de la 1.1.0 : les **modules complémentaires**, créés par la communauté, et le **SDK** pour en écrire.

### Modules complémentaires

- Un module complémentaire est un fichier `.avmod`. Tu l'installes d'un double-clic, ou depuis **Paramètres, section Modules complémentaires, « Installer un module… »**.
- Avant l'installation, la fenêtre te montre ce que le module demande. Tu accordes ses permissions **une par une** : réseau local, Internet, un service de ce PC sur les ports que tu choisis, capteurs, musique en cours.
- Chaque module tourne dans **son propre processus, enfermé par Windows** (AppContainer et job object). S'il plante, l'écran et les autres modules continuent.
- Le logiciel enregistre l'empreinte du module à l'installation et la revérifie à chaque lancement, même hors ligne. Un module modifié depuis ta validation ne démarre pas. Ses fichiers sont verrouillés pendant qu'il tourne.
- Un **point d'exclamation jaune** signale les modules complémentaires partout dans la fenêtre de réglage.
- Les mises à jour d'un module sont cherchées sur GitHub et **te sont proposées**. Elles ne se téléchargent que si tu cliques.
- Paramètres montre ce que consomme chaque module, et signale ceux qui consomment beaucoup.
- Les modules sont rangés dans `Documents\Ashboard Vision\Modules`. Tu peux changer ce dossier.
- Un mode développeur permet d'essayer un module en cours d'écriture.
- Sans module complémentaire, rien ne tourne en plus : le logiciel reste aussi discret qu'avant.
- À la désinstallation, si tu effaces tes réglages, le dossier des modules est effacé aussi, s'il est à son emplacement par défaut. Les secrets des modules (clés, jetons) sont effacés dans tous les cas, même si tu gardes tes réglages.

> **Avertissement.** Un module complémentaire n'est pas fait par Ashboard Vision et **peut contenir du code malveillant**. L'enfermement limite les dégâts, mais ne les exclut pas. Installe seulement un module dont tu connais et fais confiance à l'auteur.

### SDK pour créer des modules

- Le SDK permet d'écrire un module en C# : un modèle de projet (`dotnet new ashboard-module`), des aides de test et un banc d'essai (`ashboard-module check` et `ashboard-module pack`).
- L'installateur le propose à la page des options : « Outils pour créer des modules (SDK) », décoché par défaut. S'il ne trouve pas de SDK .NET 10 sur ton PC, il télécharge la dernière version chez Microsoft et l'installe, après avoir vérifié son empreinte et sa signature. La barre de l'installateur suit l'installation paquet par paquet.
- Les quatre paquets du SDK sont aussi joints à chaque version GitHub. Ils ne sont pas publiés sur nuget.org.
- Toute la documentation est en français, dans `docs/sdk`.
- Le dépôt contient trois modules d'exemple, dans `examples` : Dépôt GitHub, Démo et Serveur Minecraft.

### Corrections

- Dans l'onglet Disposition, cliquer sur un module de la liste « Ajouter un module » l'ajoute de nouveau. Depuis la 1.0.0, le clic refermait la liste sans rien ajouter.
- À la désinstallation du logiciel, quand tu effaces tes réglages, le dossier des modules est bien supprimé, même dans un dossier Documents synchronisé par OneDrive.

## 1.0.0

Première version stable d'Ashboard Vision. Elle réunit tout ce que les quatre bêtas ont apporté (voir leurs sections ci-dessous) :

- un installateur Windows qui installe tout, propose le démarrage avec Windows et se désinstalle proprement, en gardant tes réglages ou en les effaçant ;
- les mises à jour depuis GitHub en un clic, sur le canal de ton choix (Stable ou Bêta), vérifiées par leur empreinte SHA-256 ;
- le passage de trofeo-HB-dashboard à Ashboard Vision, réglages compris ;
- le mod des quotas de Claude Code, installé même quand Claude Code n'est fourni que par l'application Claude.

Si tu as mis à jour depuis la bêta 2 ou une plus ancienne sans redémarrer Windows depuis, le service tourne peut-être encore en administrateur : un redémarrage de Windows suffit à le remettre avec tes droits normaux.

## 1.0.0-beta.4

- Le mod des quotas s'installe aussi quand Claude Code n'est fourni que par l'application Claude, sans commande `claude` dans le PATH. Merci à Macorou pour ce correctif !
- La mise à jour du mod à chaque nouvelle version et son retrait à la désinstallation trouvent aussi ce Claude Code.
- Depuis la bêta 3, une mise à jour en un clic relance le service avec tes droits normaux : cette version est la première à le montrer.

## 1.0.0-beta.3

- Après une mise à jour en un clic, le service repart avec tes droits normaux, et non plus en administrateur. C'est maintenant l'installateur qui demande l'autorisation à Windows.
- Si tu refuses cette autorisation, Paramètres indique que la mise à jour est annulée.
- Si l'installateur s'arrête sans rien installer, Paramètres l'indique comme un échec.

Note : la mise à jour depuis la bêta 2 passe encore par l'ancienne méthode. Juste après, le service tourne donc en administrateur, jusqu'à sa prochaine relance (redémarrage de Windows, par exemple).

## 1.0.0-beta.2

Deuxième bêta, surtout pour essayer la mise à jour depuis la bêta 1.

- L'ancien dossier `Program Files\trofeo-HB-dashboard`, resté vide après le passage à Ashboard Vision, est maintenant retiré.

## 1.0.0-beta.1

Première bêta publique. Le projet, qui s'appelait trofeo-HB-dashboard, devient Ashboard Vision.

### Nouveau nom

- Le logiciel, sa fenêtre, ses menus, ses fichiers et le mod des quotas de Claude Code s'appellent Ashboard Vision.
- Tes réglages déménagent de `%AppData%\trofeo-HB-dashboard` vers `%AppData%\ashboard-vision` au premier lancement. Rien n'est perdu : le dossier est renommé d'un bloc, tout ou rien, et si le déménagement échoue (un fichier ouvert, par exemple), le démarrage s'arrête plutôt que de repartir de zéro.
- Un nouveau logo, une icône qui suit le thème de Windows et des images aux couleurs de la marque pour l'installateur.

### Installateur

- `Ashboard-Vision-Setup-<version>.exe` installe tout dans `Program Files\Ashboard Vision`, demande les droits d'administrateur et propose le démarrage avec Windows.
- PawnIO, nécessaire à la lecture de certains capteurs, n'est plus embarqué : l'installateur le télécharge pendant l'installation, depuis le dépôt officiel, et vérifie son empreinte SHA-256. Une connexion Internet n'est nécessaire que pour ce pilote : sans elle, ou si tu annules le téléchargement, l'installation continue sans lui, et tu pourras l'installer plus tard depuis la fenêtre de réglage.
- La désinstallation retire le logiciel, le mod des quotas et le démarrage automatique, te demande si tu gardes tes réglages, et journalise ses étapes.
- Le bouton « Lancer » de la dernière page ouvre la fenêtre de réglage, qui démarre le service s'il ne tourne pas.
- L'installateur n'est pas signé : Windows SmartScreen affichera un avertissement la première fois. Le README explique comment le passer.

### Mises à jour

- L'onglet Paramètres cherche les nouvelles versions sur GitHub, une fois au démarrage du service (après 60 secondes), puis toutes les 24 heures. Rien ne tourne entre deux.
- Tu choisis ton canal : Stable ou Bêta. Si tu choisis Stable alors que tu as une bêta, le logiciel te propose de revenir à la dernière version stable dès qu'il y en a une, après avoir sauvegardé tes réglages ; la case « Attendre la prochaine version stable » te garde sur ta bêta jusqu'à ce qu'une stable plus récente sorte.
- Une mise à jour se télécharge, se vérifie par son empreinte SHA-256, puis s'installe sans que tu aies à retourner sur GitHub.

### Écran reconnu

- Le logiciel lit le modèle de l'écran branché. Seul le Thermalright Trofeo Vision 9,16" est pris en charge pour l'instant ; un autre écran est nommé comme non pris en charge au lieu de recevoir des images qu'il ne saurait pas afficher. La recherche d'un écran non pris en charge se fait au plus toutes les 30 secondes.

### Autres changements

- Les programmes livrés portent le nom Ashboard Vision (produit, titre, éditeur).
- Le mod des quotas déjà installé est rafraîchi lors d'une mise à jour du logiciel.
