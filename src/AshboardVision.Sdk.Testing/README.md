# AshboardVision.Sdk.Testing

Les aides de test pour écrire un module de la communauté d'**Ashboard Vision**, le tableau de bord de l'écran Thermalright Trofeo Vision.

Avec `ModuleTestHost<TonModule>`, tu fais tourner ton module **dans le processus de ton test**, sans enfermement ni service : c'est rapide, et tu peux déboguer pas à pas. Tu lui donnes ton `manifest.json`, des réglages et, si tu veux, une partie seulement des permissions ; tu démarres le module, tu le dessines dans une image (`SKBitmap`) avec l'un des thèmes du logiciel (`classique`, `liquid-glass`, `pixel`) et tu vérifies le résultat.

```csharp
await using var host = new ModuleTestHost<MonModule>("manifest.json");
host.SetSensors(new SensorReading("CPU", null, new Dictionary<string, double> { ["cpu.temp"] = 61 }));
await host.StartAsync();

using var image = host.Render(new GridSize(4, 2), "classique");
Assert.Equal(ModuleHealth.Online, host.Health);
```

Tu disposes aussi de faux services : les capteurs (`SetSensors`), la musique (`SetNowPlaying`), le coffre à secrets (`Secrets`) et une horloge que tu fais avancer (`Time`, un `FakeTimeProvider`). Une permission que tu n'accordes pas renvoie « refusé » à ton module, comme dans le logiciel, et `ConnectLocalAsync` n'ouvre que les ports saisis dans les champs `port` de tes réglages.

Ce que ces aides ne font pas : elles n'enferment pas ton module. Pour l'essayer comme le logiciel le lance (enfermé, avec ses limites de mémoire et de processeur), utilise le banc d'essai.

Elles ne vérifient pas non plus tout ton manifeste : elles n'en lisent que les permissions et les champs `port`. C'est le banc d'essai, `ashboard-module`, qui le valide en entier.

## Limites du faux

- Les capteurs et la musique que tu poses sont rendus tels quels au module, sans passer par le JSON du vrai service.
- Le message d'état (`SetStatus`) n'est pas tronqué, alors que le logiciel le raccourcit.
- Il n'y a pas de plafond de 16 connexions simultanées pour `ConnectLocalAsync`.
- `Invalidate` ne redessine rien : c'est toi qui appelles `Render`.
- La taille minimale (`minSize`) n'est pas vérifiée : `Render` accepte n'importe quelle taille du damier.
- L'heure du `FakeTimeProvider` est en UTC et ne bouge que si tu la fais avancer.

## Où le trouver

Ce paquet **n'est pas sur nuget.org**. C'est un fichier `.nupkg` joint à chaque version d'Ashboard Vision sur GitHub, et posé sur ton PC par l'installateur du logiciel, à côté de `AshboardVision.Sdk`. Tu l'ajoutes à ton projet de tests comme source locale NuGet.

Il dépend du paquet `AshboardVision.Sdk` 1.1.0 et de `Microsoft.Extensions.TimeProvider.Testing` (le `FakeTimeProvider`). Il embarque `Trofeo.Core.dll`, qui lui fournit les thèmes et les polices du logiciel.

## Licence

GPL-3.0-or-later. Les polices embarquées sont sous licence OFL : leurs textes sont dans le dossier `licenses` du paquet.
