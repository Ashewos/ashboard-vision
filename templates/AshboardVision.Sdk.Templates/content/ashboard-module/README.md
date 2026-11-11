# MonModule

Un module pour **Ashboard Vision**, le tableau de bord de l'écran Thermalright Trofeo Vision. Il affiche un message (« Bonjour » par défaut) et l'heure, aux couleurs du thème, et ne demande aucune permission.

## Ce qu'il y a dans ce dossier

- `MonModule/MonModule.cs` : le module. Une classe qui dérive de `CommunityModule` ; `Render` dessine avec SkiaSharp, `StartAsync` lit les réglages de la case.
- `MonModule/manifest.json` : la carte d'identité du module (identifiant, version, permissions, tailles, réglages). Le logiciel la lit avant de lancer le moindre code.
- `MonModule.Tests/` : les tests, qui font tourner le module dans le processus du test avec `ModuleTestHost`.

## Construire et tester

```
dotnet build
dotnet test
```

## L'essayer comme le logiciel le lance

La commande `ashboard-module check` lance ton module enfermé, comme le logiciel, dessine une image PNG par taille et par thème, refuse tes permissions une à une et mesure la consommation (une trentaine de secondes) :

```
ashboard-module check MonModule/bin/Debug/net10.0 --out check-out
```

Les images sont dans `check-out`. La commande dit à la fin si le module est compatible, ou ce qui coince.

## Le publier

```
ashboard-module pack MonModule/bin/Debug/net10.0 --out pack-out
```

Tu obtiens `<identifiant>-<version>.avmod` et son empreinte `.sha256` : joins les deux à une version GitHub de ton dépôt, dont l'étiquette est `v<version>`.

## Aller plus loin

- **Les réglages** : la liste `settings` du manifeste décrit les champs que l'utilisateur remplit dans la fenêtre de réglage (texte, choix, interrupteur, nombre, port, secret…). Le module les lit dans `Services.Settings`.
- **Les permissions** : la liste `permissions` du manifeste (`reseau-local`, `internet`, `ce-pc`, `capteurs`, `musique`). L'utilisateur les valide une par une ; un module ne reçoit jamais plus que ce qu'il déclare, ni plus que ce qui est accordé.
- **La version du SDK** : le paquet `AshboardVision.Sdk` (voir `MonModule/MonModule.csproj`) et le champ `sdk` du manifeste vont ensemble. Quand tu passes à un SDK plus récent, change les deux.
- **L'identifiant** : de la forme `auteur.module` (minuscules, chiffres et tirets, avec un point au milieu, 40 caractères au plus). Il ne change plus une fois le module publié : c'est lui qui relie la disposition et les secrets de l'utilisateur à ton module.
