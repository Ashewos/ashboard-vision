# Les raccourcis

Un module peut proposer des **actions au clavier** : « Rafraîchir », « Changer de couleur », « Passer au joueur suivant »… Tu les déclares dans le manifeste, tu les reçois dans une méthode, et c'est **l'utilisateur** qui décide quelle touche déclenche quoi. Tu peux sauter cette page si ton module n'en a pas besoin : elle décrit ce que le **SDK 1.1** ajoute au SDK 1.0.

## Le principe

- **Ton module ne voit jamais le clavier.** C'est Ashboard Vision qui écoute les touches (comme pour ses propres raccourcis, même quand une autre application a le focus). Quand l'utilisateur appuie sur la touche d'un de tes raccourcis, le logiciel appelle une méthode de ton module avec **l'identifiant** de l'action, et rien d'autre : pas la touche, pas l'application qui avait le focus.
- **Tu déclares, l'utilisateur choisit.** Tu peux proposer une touche par défaut ; l'utilisateur l'accepte, la change ou la refuse. Le logiciel vérifie les conflits à sa place.
- **Un raccourci n'agit que sur la vue affichée.** Ashboard Vision peut avoir plusieurs vues (des dispositions que l'utilisateur change au clavier). Ton module reçoit le raccourci seulement pour les cases qu'il a dans la vue qui est sur l'écran. Une case rangée à l'étagère ou dans une vue cachée ne reçoit rien.

## Déclarer un raccourci

Dans `manifest.json`, un tableau facultatif `shortcuts`. Chaque raccourci a un `id`, un `name` et, si tu veux en proposer une, des `keys`.

*`examples/Demo/Demo/manifest.json`.*

```json
  "shortcuts": [
    { "id": "colour", "name": "Changer de couleur", "keys": "Ctrl+Alt+Shift+D" }
  ]
```

Le module « Dépôt GitHub » en déclare un aussi, **sans touche** :

*`examples/GitHubRepo/GitHubRepo/manifest.json`.*

```json
  "shortcuts": [
    { "id": "refresh", "name": "Rafraîchir" }
  ]
```

| Champ | Obligatoire | Ce que c'est |
|---|---|---|
| `id` | oui | L'identifiant que ton module reçoit : lettres, chiffres, points, tirets et soulignements, sans commencer par un point ou un tiret. **Au-delà de 40 caractères, le manifeste est refusé.** Il est comparé tel quel, majuscules comprises. Deux raccourcis ne partagent pas le même. |
| `name` | oui | Le nom que l'utilisateur lit (dans la fenêtre d'installation et dans l'onglet Raccourcis). **Au-delà de 40 caractères, le logiciel le coupe**. Écris-le comme un verbe : « Rafraîchir ». |
| `keys` | non | La touche que tu proposes par défaut, par exemple `Ctrl+Alt+Shift+R`. Au moins un modificateur (`Ctrl`, `Alt`, `Shift`, `Win`) puis une seule touche : une lettre, un chiffre, une flèche (`Left`, `Right`, `Up`, `Down`) ou `F1` à `F24`. |

Règles du manifeste :

- **10 raccourcis au plus** par module.
- Un module qui déclare des raccourcis **doit demander `"sdk": "1.1"` au moins**. Sinon le manifeste est refusé : « Ce module déclare des raccourcis : il doit demander le SDK 1.1 au moins. » Un logiciel plus ancien que la 1.1 refuse alors le module avec le message de [la compatibilité](compatibilite.md), au lieu de l'installer sans que ses raccourcis marchent.
- Le banc d'essai (`ashboard-module check`) et le logiciel lisent le manifeste avec les mêmes règles.

### Choisir une touche par défaut

- **Propose rarement, et jamais une touche qui sert déjà.** Les raccourcis du logiciel sont `Ctrl+Alt+↑` et `Ctrl+Alt+↓` (l'onglet précédent ou suivant d'un module), `Ctrl+Alt+←` et `Ctrl+Alt+→` (la vue précédente ou suivante) et `Ctrl+Alt+Maj+1` à `9` (l'onglet n° 1 à 9). Les exemples de ce dépôt prennent `Ctrl+Alt+Shift+<lettre>`.
- **Évite `Ctrl+Alt+<chiffre ou lettre>` sans `Shift`** : sur un clavier AZERTY, `Ctrl+Alt` est `AltGr`, et `AltGr+<touche>` sert à taper `~`, `#`, `{`, `[`, `|`, `\` et `@`. Prendre une de ces combinaisons empêcherait l'utilisateur d'écrire.
- **Une action sans touche par défaut est un choix honnête.** Le module « Dépôt GitHub » n'en propose pas : l'utilisateur lui en donne une dans l'onglet Raccourcis s'il en veut une.

## Recevoir un raccourci

`CommunityModule` a une méthode virtuelle, `OnShortcutAsync(string shortcutId, CancellationToken ct)`. Redéfinis-la et réagis à l'identifiant que tu as déclaré.

*`examples/Demo/Demo/DemoModule.cs`.*

```csharp
    public override Task OnShortcutAsync(string shortcutId, CancellationToken ct)
    {
        if (shortcutId == "colour")
        {
            _colour = (_colour + 1) % (HeadColours.Length + 1);
            Invalidate();
        }

        return Task.CompletedTask;
    }
```

La méthode change un état (ici le rang de la couleur, dans un champ `volatile` que `Render` lit) puis demande une image avec `Invalidate()` : sans cela, un module qui n'est pas animé ne se redessinerait pas. Voir [le contrat](contrat.md#les-threads).

Ce que le logiciel garantit, et ce qu'il ne garantit pas :

- **Elle est appelée sur l'instance de la case**, celle qui a ses propres réglages, après `StartAsync`. Pas sur une instance à part.
- **Hors du fil de dessin**, et **jamais deux fois en même temps** pour une instance : les appuis arrivent dans l'ordre, un à la fois.
- **Si ta méthode est lente**, les appuis en attente s'accumulent dans une file de **8**. Au-delà, les suivants sont **jetés** (et le journal le dit). Reste donc rapide, ou lance le travail long à part.
- **Le logiciel limite à 5 appuis par seconde et par module**, qu'une touche soit tenue enfoncée ou répétée. Un appui de plus est ignoré.
- **Une exception** sortie de ta méthode est écrite dans le journal du module (`Exception dans OnShortcutAsync (raccourci « id »)`, avec l'identifiant du raccourci). Elle ne met pas la case en erreur et n'arrête rien ; le banc d'essai, lui, la compte contre ton module (voir plus bas).
- **Un identifiant inconnu doit être ignoré** : ne lève pas d'exception pour un `shortcutId` que tu ne connais pas.
- **Si le module n'a aucune case dans la vue affichée**, rien n'est envoyé. Si l'hôte de ton module est en train de redémarrer ou mis de côté, l'appui est abandonné : il n'y a pas de file d'attente qui rejouerait tes raccourcis plus tard.
- **Si l'utilisateur a deux cases de ton module dans la vue affichée**, elles reçoivent toutes les deux l'appui, sauf s'il a lié le raccourci à une case précise.

Ton module peut aussi limiter lui-même ce qu'il fait d'un appui. Le module « Dépôt GitHub » compte les requêtes qu'il envoie vraiment, minuterie et raccourci ensemble, et se limite à 50 par heure glissante, sous les 60 que GitHub accorde sans jeton : un « Rafraîchir » qui ferait dépasser ce budget est ignoré. Il ignore aussi un appui qui suit le précédent de moins d'une minute, sauf quand la dernière tentative a échoué sans que GitHub réponde (réseau coupé) : l'utilisateur qui réessaie ne doit pas attendre.

*`examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
    public override Task OnShortcutAsync(string shortcutId, CancellationToken ct)
    {
        if (shortcutId == "refresh" && _http is not null && _budget is { } budget && budget.CanAfford(Volatile.Read(ref _requestsPerRefresh)))
        {
            var now = Services.Time.GetUtcNow().UtcTicks;
            var lastManual = Interlocked.Read(ref _lastManualTicks);
            if ((lastManual == 0 || now - lastManual >= MinGapBetweenManualRefreshes.Ticks || _failedWithoutAnswer) && Refresh())
            {
                Interlocked.Exchange(ref _lastManualTicks, now);
            }
        }

        return Task.CompletedTask;
    }
```

## Ce que l'utilisateur voit

- **À l'installation**, la fenêtre montre une section « Raccourcis proposés » : chaque raccourci qui a une touche, avec une case à cocher. Une touche **libre** arrive cochée. Une touche **déjà prise** (par le logiciel, par un autre module ou par une autre application de Windows) arrive décochée, avec le nom de ce qui la tient : « Touche déjà prise par « … ». » ou « Touche déjà prise par un autre logiciel. ». Les raccourcis ne sont pas des permissions : ils ne donnent aucun accès à l'ordinateur, c'est pourquoi la touche libre est cochée d'avance.
- **Un raccourci sans touche** n'est pas proposé à l'installation. L'utilisateur peut quand même lui en donner une dans l'onglet **Raccourcis** de la fenêtre de réglage, où tous les raccourcis des modules complémentaires apparaissent avec le **point d'exclamation jaune** qui marque ces modules partout.
- **À une mise à jour**, seuls les raccourcis **nouveaux** sont proposés ; les liaisons que l'utilisateur a déjà sont gardées. Un raccourci que ton manifeste ne déclare plus perd ses liaisons.
- **À la désinstallation**, les liaisons du module sont retirées. Un module mis de côté (arrêté après trois pannes) garde les siennes, qui ne font rien tant qu'il est de côté.

Ne compte donc jamais sur une touche : écris ton module pour qu'il soit **aussi utilisable sans**.

## Tester un raccourci

Avec `ModuleTestHost`, `TriggerShortcutAsync` appelle `OnShortcutAsync` de ton module comme le logiciel le ferait, et attend qu'elle ait fini.

*`examples/Demo/Demo.Tests/DemoTests.cs`.*

```csharp
        await using var host = await StartedAsync();
        host.Time.SetUtcNow(Start);
        using var before = host.Render(Size, "classique");

        // Le raccourci « colour » (celui que le manifeste déclare) passe la tête à la couleur suivante : même heure, autre dessin.
        await host.TriggerShortcutAsync("colour");
        using var after = host.Render(Size, "classique");

        Assert.NotEqual(before.Bytes, after.Bytes);
```

Ce que l'aide de test ne fait pas : elle ne compare pas l'identifiant à la liste du manifeste (c'est à toi de donner un identifiant déclaré), elle n'applique ni la file de 8 ni la limite de 5 par seconde, et une exception de ton module sort telle quelle vers le test. Appelle-la avant `StartAsync` et elle lève une `InvalidOperationException`.

Le banc d'essai `ashboard-module check` fait, lui, le trajet complet. Pour chaque raccourci du manifeste, il le livre à ton module enfermé, par le chemin du logiciel, en espaçant les appuis de 300 ms. Il écrit une ligne :

```text
✔ Raccourcis : 1 livré au module (colour), sans erreur.
```

Il **compte comme une erreur** une exception de `OnShortcutAsync` (même si le logiciel, lui, la garde au journal) et un hôte qui s'arrête pendant ce temps. Voir [Tester](tests.md#le-banc-dessai).

## Et un module plus ancien ?

Un module écrit pour le SDK 1.0 n'a pas de raccourcis, tourne sans rien changer dans un logiciel plus récent, et n'a pas besoin d'être recompilé. Ne passe `"sdk"` à `1.1` qu'au moment où tu déclares un raccourci. Voir [la compatibilité](compatibilite.md).
