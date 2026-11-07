# Créer un module pour Ashboard Vision

Ashboard Vision affiche des modules sur l'écran Thermalright Trofeo Vision : l'heure, l'état du PC, la musique en cours… Les six modules d'origine sont dans le logiciel. Avec le **SDK**, tu peux en écrire un autre, en C#, pour toi seul ou pour les autres : l'état d'un serveur, les statistiques d'un dépôt GitHub, la maison, une caméra…

Un module de la communauté est :

- **une DLL .NET** qui contient une classe, et qui dessine dans sa case avec SkiaSharp ;
- **un fichier `manifest.json`** qui le décrit : son nom, ses permissions, ses tailles, ses réglages ;
- **un fichier `.avmod`** (une archive ZIP de tout cela) que l'utilisateur installe en double-cliquant dessus.

Chaque module tourne dans **son propre processus, enfermé par Windows**. S'il plante, le reste du tableau de bord continue. Il ne peut faire que ce que l'utilisateur a accepté, permission par permission.

Cette documentation est écrite pour quelqu'un qui découvre. Elle suppose seulement que tu sais lire un peu de C#.

## Par où commencer

1. [Démarrer](demarrer.md) : installer le SDK, créer un module avec une commande, le voir sur l'écran.
2. [Le contrat](contrat.md) : ce qu'un module doit faire, et ce que le logiciel fait pour lui.
3. [Dessiner](dessin.md), puis [les réglages](reglages.md) et [les permissions](permissions.md), selon ce que ton module fait.
4. [Tester](tests.md), avec les tests rapides et avec le banc d'essai `ashboard-module check`.
5. [Publier](publier.md) sur GitHub, avec `ashboard-module pack`.

## Toutes les pages

| Page | Tu y apprends |
|---|---|
| [Démarrer](demarrer.md) | Installer le SDK, créer ton premier module, l'essayer dans Ashboard Vision. |
| [Le contrat](contrat.md) | La classe `CommunityModule`, la vie d'un module, le manifeste, les erreurs. |
| [Les permissions](permissions.md) | Réseau local, Internet, Cet ordinateur, Capteurs, Musique en cours. |
| [Dessiner](dessin.md) | La case, le thème, le texte, les images, l'animation. |
| [Les réglages](reglages.md) | Les champs du formulaire, les secrets, les ports, les boutons. |
| [Tester](tests.md) | Les tests xUnit sans réseau, et le banc d'essai. |
| [Publier](publier.md) | Fabriquer le `.avmod`, la version GitHub, les mises à jour. |
| [La compatibilité](compatibilite.md) | Pourquoi ton module ancien continue de marcher. |
| [La sécurité](securite.md) | Ce que l'enfermement empêche, ce qu'il n'empêche pas, la licence. |

## Les modules d'exemple

Le dépôt contient trois vrais modules, avec leurs tests. Les extraits de cette documentation viennent d'eux, mot pour mot.

| Module | Dossier | Ce qu'il montre |
|---|---|---|
| Démo | [`examples/Demo`](../../examples/Demo) | Une animation sans réseau ni permission : le dessin, le thème, `IsAnimating`. |
| Dépôt GitHub | [`examples/GitHubRepo`](../../examples/GitHubRepo) | Internet, un secret facultatif, une actualisation régulière, les états. |
| Serveur Minecraft | [`examples/Minecraft`](../../examples/Minecraft) | « Cet ordinateur » et un port choisi par l'utilisateur, une image, les pannes. |

## Ce que la première version ne fait pas

- Un module **affiche** : il ne lance pas d'action depuis l'écran et n'a pas de raccourci clavier.
- Un module n'a pas accès aux fichiers de l'utilisateur.
- Les modules sont écrits en C# (.NET). D'autres langages pourront venir plus tard.
- Il n'y a pas de boutique : tu publies ton fichier où tu veux, en pratique dans les versions GitHub de ton dépôt.
- L'overlay Discord n'est pas possible : Discord se parle par un tube local et une interface sur `localhost`, que l'enfermement interdit.

## Version

Cette documentation décrit le **SDK 1.0**. Les numéros du SDK ne suivent pas ceux du logiciel. Voir [la compatibilité](compatibilite.md).
