# AshboardVision.Sdk.Templates

Le modèle de projet pour créer un module de la communauté d'**Ashboard Vision**, le tableau de bord de l'écran Thermalright Trofeo Vision.

```
dotnet new ashboard-module -n MonModule --id moi.mon-module --param:author "Ton nom"
```

Tu obtiens un module qui affiche « Bonjour » et l'heure, suit le thème du logiciel, ne demande aucune permission et propose un réglage « Message » ; avec son `manifest.json`, un projet de tests (`ModuleTestHost`) et un `README.md` qui explique la suite : construire, tester, essayer avec `ashboard-module check`, puis publier avec `ashboard-module pack`.

- `-n` : le nom du projet et de la DLL du module (sans espace).
- `--id` : l'identifiant du module, de la forme `auteur.module` (minuscules, chiffres et tirets, avec un point au milieu, 40 caractères au plus). Le modèle ne le vérifie pas : c'est le banc d'essai (`ashboard-module check`) qui refuse un identifiant mal formé, en disant pourquoi.
- `--param:author` (ou `-au`) : ton nom, tel qu'il s'affichera dans la fenêtre de réglage. L'option `--author` n'existe pas : `dotnet new` la réserve à ses recherches (`dotnet new search --author`), il préfixe donc le nom de ce paramètre par `param:`.

## Où le trouver

Ce paquet **n'est pas sur nuget.org**. C'est un fichier `.nupkg` joint à chaque version d'Ashboard Vision sur GitHub, et posé sur ton PC par l'installateur du logiciel (case « Outils pour créer des modules (SDK) »), qui lance aussi `dotnet new install AshboardVision.Sdk.Templates`. À la main : `dotnet new install <chemin du fichier .nupkg>`.

Le module généré dépend des paquets `AshboardVision.Sdk` et `AshboardVision.Sdk.Testing` (même ligne de numéros, 1.0.0), à trouver dans la même source locale NuGet.

## Licence

GPL-3.0-or-later.
