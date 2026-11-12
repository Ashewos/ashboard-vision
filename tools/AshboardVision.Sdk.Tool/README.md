# AshboardVision.Sdk.Tool

Le banc d'essai des modules de la communauté d'**Ashboard Vision**, le tableau de bord de l'écran Thermalright Trofeo Vision. Il installe une commande, `ashboard-module`.

## Les deux commandes

`ashboard-module check [dossier] [--out <dossier>]` essaie ton module comme le logiciel le lance : enfermé, avec les limites de mémoire et de processeur. Il vérifie ton manifeste, démarre le module avec ses permissions, dessine une image PNG par taille (ta taille minimale et chaque préréglage) et par thème (`classique`, `liquid-glass`, `pixel`) dans `--out` (par défaut `check-out`, dans le dossier où tu es), refuse tes permissions une à une pour voir si le module tient, livre à ton module chaque raccourci que ton manifeste déclare (une exception de `OnShortcutAsync` compte comme une erreur), mesure sa consommation pendant 30 secondes et vérifie qu'il s'arrête proprement. Chaque vérification tient sur une ligne (`✔` réussie, `!` à regarder, `✖` échouée), et le code de sortie est 0 si le module est compatible, 1 sinon.

`ashboard-module pack [dossier] [--out <dossier>]` vérifie ton manifeste et fabrique `<id>-<version>.avmod` et `<id>-<version>.avmod.sha256`, les deux fichiers à joindre à ta version GitHub (l'étiquette est `v<version>`).

Sans `dossier`, les deux commandes prennent le dossier où tu es : celui qui contient `manifest.json` et la DLL de ton module.

## Ce que l'outil touche

L'outil ne fait aucun appel réseau, sauf ce que ton module fait lui-même pendant `check`. Il n'utilise ni tes réglages d'Ashboard Vision, ni la liste de tes modules validés, ni ton coffre à secrets : le module essayé tourne dans son propre conteneur d'essai (`ashboard-vision.check.<id>`), avec des secrets en mémoire et de fausses données de capteurs et de musique. Le conteneur est supprimé à la fin, même si tu arrêtes l'outil avec Ctrl+C. Si le processus est tué de force, il peut en rester un : il est sans danger et la prochaine exécution le réutilise.

## Où le trouver

Ce paquet **n'est pas sur nuget.org**. C'est un fichier `.nupkg` joint à chaque version d'Ashboard Vision sur GitHub, et posé sur ton PC par l'installateur du logiciel. Tu l'installes comme outil depuis ce dossier local : `dotnet tool install --global AshboardVision.Sdk.Tool --add-source <dossier des paquets>`.

Il embarque l'hôte de module du logiciel (`Trofeo.ModuleHost.exe`, dans le sous-dossier `host`) : ton module est essayé par le même programme que celui qui le lancera chez les utilisateurs.

## Licence

GPL-3.0-or-later. Les polices embarquées sont sous licence OFL.
