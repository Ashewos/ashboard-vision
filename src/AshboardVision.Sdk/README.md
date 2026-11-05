# AshboardVision.Sdk

Le SDK pour écrire un module de la communauté d'**Ashboard Vision**, le tableau de bord de l'écran Thermalright Trofeo Vision.

Tu y trouves :

- `CommunityModule`, la classe de base d'un module : tu en hérites, tu dessines avec SkiaSharp, c'est tout ;
- `ModuleServices`, ce que le logiciel te prête (réglages, secrets, journal, capteurs, musique en cours, connexions autorisées) ;
- les permissions (`ModulePermission`), que l'utilisateur valide une par une ;
- le schéma des réglages, le thème et `ModuleDrawing`, qui viennent de `Trofeo.ModuleKit`, embarqué dans ce paquet.

## Où le trouver

Ce paquet **n'est pas sur nuget.org**. C'est un fichier `.nupkg` joint à chaque version d'Ashboard Vision sur GitHub, et posé sur ton PC par l'installateur du logiciel. Tu l'ajoutes à ton projet comme source locale NuGet.

Seule dépendance : SkiaSharp 3.119.4.

## Compatibilité

La ligne 1.x du SDK ne retire rien : un module compilé contre la 1.0 continue de se charger avec toute version 1.x du logiciel.

## Licence

GPL-3.0-or-later.

## Documentation

Le guide pour créer un module (premier module pas à pas, manifeste, permissions, banc d'essai) arrive avec la documentation du SDK. Le code et les exemples sont dans le dépôt : <https://github.com/Ashewos/ashboard-vision>.
