# La sécurité

Un module de la communauté, c'est du code écrit par quelqu'un d'autre, qui tourne sur le PC de l'utilisateur. Ashboard Vision part de là : **un module peut être malveillant**, et le logiciel est construit pour que ce soit le moins grave possible.

Cette page s'adresse à toi, créateur, pour que tu comprennes ce qui se passe autour de ton module. Elle dit aussi ce que la protection ne fait **pas** : l'honnêteté fait partie du contrat.

## Ce que le logiciel fait pour protéger l'utilisateur

### Un avertissement, toujours

À l'installation, la fenêtre affiche : « Ce module a été créé par la communauté, pas par Ashboard Vision. Il peut contenir du code malveillant : installe seulement un module dont tu connais et fais confiance à l'auteur. » La fenêtre de réglage ajoute « Module complémentaire : créé par la communauté, pas par Ashboard Vision » dans les réglages du module, et un **point d'exclamation jaune** partout où il apparaît (dans la liste des modules à ajouter, dans l'éditeur de disposition).

Sur l'écran Trofeo, **rien ne distingue** un module complémentaire d'un module d'origine : l'écran est fait pour regarder. Un module y dessine ce qu'il veut.

### Des permissions, une par une

Aucun code de module ne tourne avant que l'utilisateur ait **validé** le module. Il coche ensuite chaque permission, une par une. Ce que tu n'as pas déclaré est refusé d'office. Voir [les permissions](permissions.md).

### Un processus enfermé

Chaque module tourne dans **son propre processus**. Si le tien plante, les autres modules et le logiciel continuent. Windows enferme ce processus dans un **AppContainer**, le même mécanisme que celui des applications du Microsoft Store. Concrètement :

- il **ne voit pas les fichiers** de l'utilisateur. Il lit son propre dossier et écrit dans un dossier privé qui n'est qu'à lui ;
- le **réseau** ne lui est ouvert que par les permissions accordées (réseau local, Internet) ;
- il **ne peut pas joindre `localhost` directement** : pour un service de ce PC, c'est le logiciel qui relaie la connexion, sur les seuls ports que l'utilisateur a choisis ;
- son conteneur porte le nom `ashboard-vision.mod.<id>`, propre à chaque module.

### Des limites de ressources

Chaque module est aussi dans un **job object** Windows, qui plafonne :

- la **mémoire** à 512 Mio (au-delà, Windows l'arrête) ;
- le **processeur** à 25 % de la machine ;
- le nombre de processus à **un seul** : le module ne peut lancer aucun programme.

Le job object arrête aussi le module avec le service, et s'il lève une exception que personne n'attrape.

Ce sont des garde-fous contre les énormités. Le repère « Consommation élevée » est plus bas : 2 % du processeur ou 150 Mio de mémoire, en moyenne sur une minute.

### Les secrets

Le coffre à secrets d'un module est **chiffré par le logiciel** (avec la protection de données de Windows) et **propre au module** : un module ne lit jamais les secrets d'un autre. Les secrets ne sont pas dans la disposition de l'utilisateur et ne voyagent pas avec elle.

Ils sont **effacés à toute désinstallation du module**, même quand l'utilisateur garde ses réglages. Désinstaller Ashboard Vision efface aussi les secrets des modules.

## Ce que l'enfermement n'empêche pas

La protection est forte, mais **pas absolue**.

- **Un module qui a la permission « Internet » peut y envoyer les données qu'on lui confie.** Si l'utilisateur lui donne un jeton, ou s'il a aussi la permission « Capteurs » ou « Musique en cours », il peut les envoyer n'importe où. L'enfermement ne lit pas ce qui sort. C'est la raison de l'avertissement permanent, et la raison pour laquelle **tu ne demandes que les permissions dont tu as vraiment besoin**.
- **Un module dessine ce qu'il veut sur l'écran**, y compris un faux message.
- **Un PC déjà infecté n'est pas protégé.** Un programme malveillant qui tourne déjà sous le compte de l'utilisateur peut faire bien pire que déposer un module. Les protections de cette page empêchent un module de s'installer en douce ; elles ne soignent pas un PC malade.
- **Un module peut consommer** jusqu'aux limites ci-dessus avant d'être arrêté.

## L'empreinte et le verrou : un module ne change pas en route

Ashboard Vision garantit qu'**un module ne peut pas être modifié entre son installation et son exécution**, et que la vérification marche **hors ligne**.

1. **À l'installation**, le logiciel calcule l'**empreinte SHA-256** de tout le contenu du module (les noms et les octets de tous ses fichiers) et la range, avec les permissions accordées, dans une liste que **seul le logiciel écrit**. Cette liste est dans `%AppData%\ashboard-vision\modules.json`, hors du dossier des modules, que d'autres programmes peuvent écrire.
2. **La liste est scellée** par une clé que Windows protège (`modules.key`, sous la protection DPAPI, comme le coffre à secrets). Une liste modifiée par un autre programme est refusée ; l'utilisateur voit « La liste des modules validés a été modifiée hors du logiciel : valide de nouveau tes modules. »
3. **À chaque lancement**, avant de démarrer le module, le logiciel ouvre chaque fichier du module **en interdisant l'écriture et la suppression**, recalcule l'empreinte sur ces fichiers déjà ouverts, et la compare à celle de l'installation. Un seul octet changé, et le module **n'est pas lancé** : la case dit « Fichiers du module modifiés : valide-le de nouveau ».
4. **Les fichiers restent verrouillés** tant que le module tourne. Personne ne peut les changer entre la vérification et l'exécution.

Rien de tout cela ne demande Internet.

Conséquences pour toi :

- tu ne peux pas reconstruire un module **installé** pendant qu'il tourne ; arrête-le, ou utilise le **mode développeur**, où le logiciel ne vérifie pas l'empreinte et ne verrouille rien (ce mode sert à ça) ;
- si le dossier des modules est dans Documents et que OneDrive le synchronise, les fichiers peuvent n'être présents qu'en ligne. Si le logiciel n'arrive pas à les ouvrir, la case l'indique (« Fichiers du module indisponibles (OneDrive ?), nouvel essai… ») et il réessaie toutes les 15 secondes ; l'utilisateur peut aussi choisir un dossier non synchronisé dans les Paramètres.

## Pourquoi pas de signature par clé ?

Ashboard Vision n'utilise **pas de signature par clé**. Une signature du créateur (une clé que tu garderais, et dont le logiciel vérifierait la signature) a été envisagée, puis **écartée** : trop compliquée à ce stade du projet. Le choix est assumé :

- l'empreinte SHA-256 publiée à côté d'un `.avmod` protège d'un **fichier abîmé** ou d'un **téléchargement détourné** ;
- elle ne protège pas d'un **dépôt de créateur piraté** : un pirate qui prend ton dépôt peut publier un faux paquet **et** la bonne empreinte du faux paquet ;
- c'est pour cela que l'avertissement est permanent, que les permissions sont validées une à une, et qu'une permission nouvelle est redemandée à la mise à jour.

## Pourquoi les mises à jour ne sont jamais silencieuses

Une mise à jour est le moment où un module sûr peut devenir un module malveillant (un dépôt piraté, un compte volé). C'est pourquoi :

- le logiciel **propose** : il montre la version et ses notes, il n'installe rien tout seul ;
- le paquet n'est **téléchargé que sur un clic** de l'utilisateur, puis vérifié par son empreinte ;
- les permissions **nouvelles** sont lues dans le fichier téléchargé et présentées à valider, décochées ;
- c'est **ce fichier** qui est installé, pas un autre.

Voir [Publier](publier.md#les-mises-à-jour).

## Ce que tu peux faire pour tes utilisateurs

- Ne demande que les permissions dont tu as besoin, et explique-les dans la `description` de ton manifeste.
- N'écris jamais un secret dans le journal ni dans un message d'état.
- Utilise le coffre à secrets pour les jetons (`secret` dans les réglages), pas un champ texte : un champ texte est écrit dans `layout.json`.
- Garde ton dépôt GitHub protégé (double authentification), puisque c'est de lui que vient la confiance de tes utilisateurs.
- Dis dans la version GitHub ce qui change, et signale toute permission nouvelle.

## La licence du SDK

Le SDK (`AshboardVision.Sdk` et ses compagnons) est sous licence **GPL-3.0-or-later**. En une phrase, sans que ce soit un conseil juridique : un module qui s'appuie sur le SDK est en général considéré comme un travail dérivé, donc il se distribue lui aussi sous la GPL, avec son code source ; en cas de doute, lis la licence ou demande à un juriste.

Les polices embarquées sont sous licence OFL, dont les textes accompagnent les paquets.
