# Les réglages

Un module a presque toujours quelque chose à régler : une adresse, un jeton, un thème de couleur… Tu **décris** les champs dans le manifeste ; Ashboard Vision **dessine le formulaire** dans sa fenêtre de réglage, comme pour ses propres modules. Tu n'écris aucune interface.

Tout est dans le manifeste, et pas dans le code, parce que le formulaire s'affiche **avant** que ton module soit validé par l'utilisateur, donc avant que ton code tourne.

## Chaque case a ses propres réglages

Les réglages sont ceux **d'une case**, pas ceux du module. Si l'utilisateur pose deux cases « Serveur Minecraft », chacune a son adresse et son port : **deux serveurs, deux cases**. Chaque case a sa propre instance de ton module, qui lit ses réglages à elle.

Quand l'utilisateur change un réglage, le logiciel ferme l'instance de cette case et en crée une nouvelle : `StartAsync` relit les nouveaux réglages.

Seuls les **secrets** sont partagés : ils appartiennent au module, pas à la case (voir plus bas).

## Les champs

Tous les champs ont :

- `kind` : le genre du champ ;
- `key` : le nom du réglage, celui que tu lis dans le code ;
- `label` : le texte affiché devant le champ ;
- `help` (facultatif) : une phrase d'aide sous le champ ;
- `visibleWhen` (facultatif) : le champ n'apparaît que si un autre champ a une certaine valeur.

| `kind` | Ce que l'utilisateur voit | Ce que tu lis dans `Settings` |
|---|---|---|
| `text` | Une zone de texte d'une ligne. | Un texte. |
| `toggle` | Une case à cocher. | `true` ou `false`. |
| `choice` | Une liste déroulante. | Le `value` de l'option choisie. |
| `list` | Des options à cocher, en ordre. | Un tableau de `value`. |
| `number` | Une zone pour un nombre entier. | Un nombre. |
| `port` | Une zone pour un numéro de port (1 à 65535). | Un nombre. |
| `secret` | Une zone de saisie masquée. | **Rien** : voir [les secrets](#les-secrets). |
| `lookup` | Une recherche avec des propositions. | La valeur de la proposition choisie. |
| `action` | Un bouton. | **Rien** : le bouton lance une action. |

### `text`

`maxLength` (80 si tu ne le dis pas) est le nombre de caractères que la fenêtre accepte. `placeholder` est le texte grisé tant que le champ est vide : **ce n'est pas une valeur** (voir [le contrat](contrat.md#un-module-non-configuré-se-dit-notconfigured)).

```json
{ "kind": "text", "key": "adresse", "label": "Adresse du serveur", "placeholder": "localhost", "maxLength": 100 }
```

### `toggle`

`default` est obligatoire.

```json
{ "kind": "toggle", "key": "animate", "label": "Animer", "default": true }
```

### `choice`

Une liste d'`options` (chacune a un `value`, écrit dans les réglages, et un `label`, montré à l'utilisateur) et un `default`, qui est le `value` d'une des options.

```json
{
  "kind": "choice", "key": "unit", "label": "Unité",
  "options": [ { "value": "c", "label": "Degrés Celsius" }, { "value": "f", "label": "Degrés Fahrenheit" } ],
  "default": "c"
}
```

### `list`

Comme `choice`, mais l'utilisateur en retient plusieurs, dans l'ordre de son choix. `default` est un tableau de `value`.

```json
{
  "kind": "list", "key": "stats", "label": "Informations à afficher",
  "options": [ { "value": "stars", "label": "Étoiles" }, { "value": "release", "label": "Dernière version" }, { "value": "downloads", "label": "Téléchargements" } ],
  "default": ["stars", "release"]
}
```

### `number`

Un nombre entier entre `min` et `max`, bornes comprises, proposé à `default`.

```json
{ "kind": "number", "key": "interval", "label": "Minutes entre deux actualisations", "min": 1, "max": 60, "default": 10 }
```

### `port`

Un numéro de port TCP, de 1 à 65535, proposé à `default`. C'est ce champ qui permet à ton module de joindre un service de **ce PC** : voir [les champs `port` et `nombre`](#les-champs-port-et-nombre).

```json
{ "kind": "port", "key": "port", "label": "Port", "default": 25565 }
```

### `secret`

Pour un jeton, un mot de passe. Voir [les secrets](#les-secrets).

```json
{ "kind": "secret", "key": "token", "label": "Jeton d'accès" }
```

### `lookup`

Une recherche : l'utilisateur tape, ton module propose. `searchable` vaut `true` par défaut ; à `false`, la liste est demandée à l'ouverture. `placeholder` est le texte grisé de la zone, `labelPath` le nom du champ qui porte le libellé dans la valeur choisie (`name` par défaut), `emptyLabel` le texte quand rien n'est choisi. Voir [les recherches et les boutons](#les-recherches-et-les-boutons).

```json
{ "kind": "lookup", "key": "city", "label": "Ville", "placeholder": "Tape un nom de ville", "emptyLabel": "Aucune ville choisie" }
```

### `action`

Un bouton. `confirm` est une question de confirmation à poser avant de lancer l'action ; sans lui, pas de confirmation.

```json
{ "kind": "action", "key": "test", "label": "Tester la connexion" }
```

### `visibleWhen`

Un champ peut ne s'afficher que si un autre a une valeur donnée. La valeur s'écrit comme du texte : `"true"` ou `"false"` pour une case à cocher. Quand l'autre champ n'est pas encore écrit, c'est sa valeur proposée qui compte.

```json
[
  { "kind": "toggle", "key": "auth", "label": "Utiliser un compte", "default": false },
  { "kind": "text", "key": "user", "label": "Nom du compte", "visibleWhen": { "key": "auth", "value": "true" } }
]
```

Chaque `key` ne sert qu'à un seul champ du schéma.

## Lire les réglages

`Services.Settings` est un `JsonElement` : un objet JSON, parfois vide. Ne suppose jamais qu'une clé est là. Lis-la avec prudence, et prévois une valeur par défaut.

*Le module « Démo » : `examples/Demo/Demo/DemoModule.cs`.*

```csharp
    public override Task StartAsync(CancellationToken ct)
    {
        var settings = Services.Settings;
        if (settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("animate", out var animate)
            && animate.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _animate = animate.GetBoolean();
        }

        return Task.CompletedTask;
    }
```

Pour un texte :

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
    private static string ReadRepository(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.String
            ? repo.GetString()!.Trim()
            : "";
```

Et pense à valider ce que tu lis : un utilisateur peut écrire n'importe quoi, ou modifier `layout.json` à la main. Une valeur illisible n'est pas une raison de planter : c'est `NotConfigured`, avec un message qui dit quoi corriger.

## Les champs `port` et `nombre`

Tant que l'utilisateur n'a pas touché un champ `number` ou `port`, **la clé n'est pas dans les réglages**. La zone affiche la valeur proposée, mais rien n'est écrit. Ton code doit donc connaître la valeur proposée (la même que dans le manifeste) et s'en servir quand la clé manque.

*Le module « Serveur Minecraft » : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
    private static bool TryReadPort(JsonElement settings, out int port)
    {
        port = DefaultPort;
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("port", out var written))
        {
            return true;
        }

        return written.ValueKind == JsonValueKind.Number && written.TryGetInt32(out port) && port is >= 1 and <= 65535;
    }
```

Le relais « Cet ordinateur » applique la même règle : il accepte la valeur du champ, ou sa valeur proposée si rien n'est écrit. Voir [les permissions](permissions.md#cet-ordinateur).

Si l'utilisateur écrit un nombre hors des bornes, la fenêtre refuse et affiche « Écris un nombre entier entre … et … ». Elle montre « De 1 à 65535, 25565 par défaut. » sous le champ. Un champ vidé revient à sa valeur proposée.

Le module « Serveur Minecraft » a un champ texte et un champ `port` : l'adresse et le numéro de port ne vont pas dans le même champ.

*Son manifeste : `examples/Minecraft/Minecraft/manifest.json`.*

```json
  "settings": [
    { "kind": "text", "key": "adresse", "label": "Adresse du serveur", "placeholder": "localhost", "maxLength": 100, "help": "Le nom ou l'adresse du serveur. Écris localhost pour un serveur lancé sur ce PC." },
    { "kind": "port", "key": "port", "label": "Port", "default": 25565, "help": "Le port du serveur Minecraft (25565 par défaut). Pour un serveur de ce PC, c'est ce port que le logiciel relaie au module." }
  ]
```

### Les réglages ne voyagent pas avec une disposition

Une disposition peut être exportée, puis importée sur un autre PC. Pour un module qui a un champ `port` (ou un champ `secret`), **tous ses réglages restent ceux de ce PC** à l'import, ou sont vides si ce PC n'en a pas : un fichier reçu ne choisit jamais où le logiciel relaie une connexion, ni où part un jeton. Le logiciel le dit à l'utilisateur après l'import.

## Les secrets

Un jeton ne doit pas traîner dans `layout.json`. Un champ `secret` n'écrit **rien** dans `Settings` : la valeur va dans le **coffre à secrets** de ton module, chiffré par le logiciel. Tu la lis avec `Services.Secrets.Get("clé")`, où la clé est le `key` du champ.

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
        // Le jeton est lu dans le coffre à secrets à chaque actualisation (toutes les 10 minutes) : le module ne le garde pas en mémoire.
        var token = Services.Secrets.Get("token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new("Bearer", token.Trim());
        }
```

À savoir :

- Chaque appel passe par le logiciel : lis le secret quand tu en as besoin, pas à chaque image.
- Le coffre est **propre à ton module**, partagé par toutes ses cases. Deux cases « Dépôt GitHub » utilisent le même jeton.
- Une clé fait 200 caractères au plus, une valeur 65 536.
- Les secrets ne sont pas dans une disposition exportée.
- Ils sont **effacés à toute désinstallation du module**, même quand l'utilisateur garde ses réglages.
- N'écris jamais un secret dans le journal.

## Les recherches et les boutons

Un champ `lookup` appelle `LookupAsync(fieldKey, query, ct)` de ton module. Un champ `action` appelle `RunActionAsync(actionKey, ct)`.

- `LookupAsync` rend une liste de `LookupResult(label, valeur)` : le libellé que l'utilisateur voit, et la valeur JSON écrite dans les réglages s'il le choisit.
- `RunActionAsync` rend un `ActionResult(ok, message)`, dont le message s'affiche sous le bouton.

Ces deux méthodes tournent sur une **instance à part**, créée pour l'occasion : `StartAsync` n'a pas été appelé, et `Invalidate()` et `SetStatus()` n'ont aucun effet. `Services` est disponible (secrets, journal, données du service). Pour une recherche, `Services.Settings` est vide (`{}`) ; pour une action, ce sont les réglages courants de la fenêtre, même s'ils ne sont pas encore enregistrés. N'attends donc pas un état préparé par `StartAsync`.

Ces appels n'existent qu'une fois le module **validé** : avant, la fenêtre n'a que le manifeste, et ton code ne tourne pas.

Une action qui ouvre une connexion vers ce PC (`ConnectLocalAsync`) est vérifiée contre les réglages envoyés avec l'action : l'utilisateur peut donc tester un port qu'il vient de saisir.
