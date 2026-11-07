# Le contrat

Cette page dit ce qu'un module doit être et faire, et ce que le logiciel fait pour lui. Si tu lis une seule page après [Démarrer](demarrer.md), c'est celle-ci.

## Ce qu'est un module

Un module, c'est :

1. **une DLL** qui contient **exactement une classe publique** dérivée de `CommunityModule`, avec **un constructeur sans paramètre** ;
2. **un `manifest.json`** à côté de la DLL (voir [le manifeste](#le-manifeste)).

Si la DLL n'a aucune classe de ce genre, ou en a plusieurs, ou si le constructeur demande des paramètres, le module est refusé avec un message qui le dit.

*Le début de la classe du module « Démo » : `examples/Demo/Demo/DemoModule.cs`.*

```csharp
public sealed class DemoModule : CommunityModule
{
    private const int StepsPerSecond = 12;
    private const int Cells = 12;

    private bool _animate = true;
```

## La vie d'un module

Le logiciel crée **une instance par case** posée sur le damier. Deux cases du même module, ce sont deux instances, chacune avec ses réglages (deux serveurs, par exemple). Un seul processus les sert toutes les deux.

Voici ce que le logiciel appelle, dans l'ordre :

| Étape | Ce qui se passe |
|---|---|
| Le constructeur | Il crée l'objet. **Rien de lourd ici**, et `Services` n'est pas encore disponible : l'utiliser lève une exception (« Services n'est disponible qu'à partir de StartAsync, pas dans le constructeur. »). |
| `StartAsync(ct)` | Tu lis tes réglages, tu lances ta minuterie ou ta connexion. Reste **rapide** : ne fais pas attendre la première image. |
| `Render(canvas, ctx)` | Tu dessines. Il est appelé chaque fois qu'une image est nécessaire. |
| `DisposeAsync()` | La case se ferme : tu libères tout. |

La première image est dessinée dès que `StartAsync` a rendu la main, sans que tu aies à appeler `Invalidate()`.

Après cela, `Render` est rappelé :

- quand tu appelles `Invalidate()` (« redessine-moi ») ;
- tant que `IsAnimating` vaut `true` (une animation, une caméra) ;
- quand le logiciel en a besoin lui-même : le thème change, la case change de taille.

Dans tous les cas, le logiciel plafonne le rythme avec le réglage « images par seconde » de l'écran (de 1 à 12). Tu ne choisis pas la cadence : voir [Dessiner](dessin.md#animer).

Quand l'utilisateur change un réglage de la case, le logiciel **ferme l'instance et en crée une nouvelle** : `StartAsync` relit les nouveaux réglages.

## Les threads

`Render` n'est **jamais appelé en parallèle avec lui-même** pour une même instance. Mais ton état, lui, est écrit par d'autres threads : la minuterie, le réseau. Ce que `Render` lit doit donc se lire en concurrence sans casse.

La règle simple : l'état que le réseau écrit et que `Render` lit se pose dans un champ `volatile`, et on remplace l'objet entier au lieu de le modifier.

*Les champs du module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
    private readonly CancellationTokenSource _stop = new();
    private string _repository = "";
    private HttpClient? _http;
    private ITimer? _timer;
    private volatile bool _closed;
    private volatile Task _refresh = Task.CompletedTask;
    private volatile RepoStats? _stats;
```

`Invalidate()` et `SetStatus()` s'utilisent depuis n'importe quel thread.

## Dire comment ça va : `SetStatus`

`SetStatus(ModuleHealth, message)` change l'état de ta case. Il y en a trois :

| État | Quand | Ce que l'utilisateur voit |
|---|---|---|
| `Online` | Tout va bien. | Ton dessin. |
| `NotConfigured` | Il manque un réglage (une adresse, un jeton…). | Ton message dans la case. |
| `Offline` | Tu n'arrives pas à travailler (serveur injoignable, permission retirée…). | Ton message dans la case, par-dessus ta dernière image. |

Le message est du français, qui tutoie, et **qui dit quoi faire** (« Indique un dépôt GitHub… », « accorde la permission… »). Garde-le **court** : le logiciel le coupe à **200 caractères** avant de l'afficher. Il ne prend aussi que 4 changements d'état par seconde : ne le fais pas clignoter.

### Un module non configuré se dit `NotConfigured`

Si un champ obligatoire est vide, ton module se déclare `NotConfigured` avec un message, et **ne tente aucune connexion** que l'utilisateur n'a pas demandée.

Un texte grisé dans le champ (`placeholder`) **n'est pas une valeur** : c'est juste une suggestion. Si le champ « Adresse » est vide, il est vide, même si le texte grisé dit « localhost ».

*Le module « Serveur Minecraft » : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
        // « localhost » n'est que ce que le champ propose (son texte grisé) : un champ vide veut dire que l'utilisateur n'a encore rien choisi.
        var address = ReadText(settings, "adresse");
        if (!Address.IsMatch(address))
        {
```

Le banc d'essai lance ton module avec les valeurs proposées par les champs, les textes restant vides, et il **compte comme une erreur** un état `Offline` accompagné d'un message. Un module qui attend une configuration doit donc rester `NotConfigured`. Voir [Tester](tests.md#le-banc-dessai).

## Les erreurs

Une exception qui sort d'une de tes méthodes **ne fait pas tomber le logiciel**. La case affiche « Erreur du module », et le journal reçoit le détail.

- Après une exception de `Render`, la case se rétablit au prochain dessin réussi (un essai par seconde au plus).
- Après une exception du constructeur ou de `StartAsync`, l'instance reste en erreur jusqu'à ce que la case soit refermée.
- Une `OutOfMemoryException` n'est **jamais rattrapée** : elle arrête le processus de ton module.
- Une exception qui sort d'un thread que **tu** as créé (une minuterie, une tâche) arrête aussi le processus du module. Rattrape tout dans ces fonctions-là.

Quand le processus d'un module s'arrête, le logiciel le relance après 1, 2, 4, 8, 16, 32 puis 60 secondes, et la case dit « Module arrêté, relance… ». Au troisième arrêt en 10 minutes, le module est **mis de côté** : l'utilisateur le relance à la main depuis la fenêtre de réglage.

Si une case n'a reçu **aucune image 10 secondes** après son ouverture (ou après une demande de dessin), elle affiche « Le module ne répond pas ».

## Une fermeture sûre

C'est la règle qui fait le plus tomber les modules : **aucune minuterie et aucune tâche ne doit lever d'exception après `DisposeAsync`**. Sinon, le processus du module s'arrête, et toutes ses cases avec lui.

Le schéma des exemples tient en trois idées :

1. un drapeau `_closed` que la minuterie lit avant de faire quoi que ce soit ;
2. une seule actualisation à la fois, et un `try/catch` autour du lancement, parce qu'un rappel peut arriver quand tout est déjà libéré ;
3. une fermeture qui arrête la minuterie, annule le travail en cours, et **l'attend** avant de libérer le reste.

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
    private void Refresh()
    {
        try
        {
            if (!_closed && _refresh.IsCompleted)
            {
                _refresh = RefreshAsync(_stop.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.Log.Warning("Actualisation ignorée : " + ex.Message);
        }
    }
```

*Même fichier : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
    public override async ValueTask DisposeAsync()
    {
        _closed = true;
        _timer?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _refresh.ConfigureAwait(false);
        _http?.Dispose();
        _stop.Dispose();
    }
```

Après la fermeture, `Invalidate()` et `SetStatus()` ne font plus rien : un appel tardif ne casse rien. Rends la main en quelques secondes : le logiciel n'attend pas plus de 5 secondes.

## Ce que `Services` te donne

`Services` est disponible depuis `StartAsync`.

| Membre | Ce que c'est |
|---|---|
| `Settings` | Les réglages de **cette case**, un objet JSON (`JsonElement`). Voir [les réglages](reglages.md). |
| `Secrets` | Ton coffre à secrets (jetons, mots de passe), chiffré par le logiciel et propre à ton module. `Get(clé)` et `Set(clé, valeur)`. Une clé fait 200 caractères au plus, une valeur 65 536. Le coffre est plafonné : 64 clés et 256 Kio en tout (la taille en octets UTF-8 des clés et des valeurs). Une écriture qui dépasserait est refusée **sans erreur** : `Set` rend la main comme si tout allait bien et `Get` rend ensuite l'ancienne valeur (ou `null`). Remplacer une clé par une valeur de même taille ou plus courte, ou l'effacer, reste permis. |
| `Log` | Ton journal : `Info`, `Warning`, `Error`. Il va dans le journal d'Ashboard Vision (`logs\display.log`, dans `%AppData%\ashboard-vision`), préfixé du type de module. 20 messages par seconde au plus. **N'y écris jamais un secret.** |
| `Time` | Ta source de temps (`TimeProvider`). Utilise-la plutôt que `DateTime.Now` : tes tests peuvent alors faire avancer l'heure. |
| `DataDirectory` | Un dossier à toi, privé, où tu peux lire et écrire. Il est effacé avec le module. |
| `Has(permission)` | Vrai si l'utilisateur a accordé la permission. Voir [les permissions](permissions.md). |
| `GetSensorsAsync`, `GetNowPlayingAsync` | Les capteurs et la musique en cours, lus par le logiciel. |
| `ConnectLocalAsync`, `CreateHttpClient` | Joindre le réseau, ou un service de ce PC. |

## Le manifeste

`manifest.json` est lu avant que ton code tourne. Un manifeste douteux (JSON cassé, valeur hors limites) est refusé avec la liste de ce qui ne va pas, en français.

Voici celui du module « Dépôt GitHub ».

*`examples/GitHubRepo/GitHubRepo/manifest.json`.*

```json
{
  "id": "ashewos.github",
  "name": "Dépôt GitHub",
  "description": "Affiche les étoiles, la dernière version et les téléchargements d'un dépôt GitHub.",
  "author": "Ashewos",
  "version": "1.0.0",
  "sdk": "1.0",
  "entry": "GitHubRepo.dll",
  "permissions": ["internet"],
  "minSize": { "columns": 3, "rows": 1 },
  "presets": {
    "compact": { "columns": 3, "rows": 1 },
    "ideal": { "columns": 3, "rows": 2 }
  },
  "settings": [
    { "kind": "text", "key": "repo", "label": "Dépôt", "placeholder": "Ashewos/ashboard-vision", "maxLength": 100, "help": "Écris-le sous la forme propriétaire/nom." },
    { "kind": "secret", "key": "token", "label": "Jeton GitHub (facultatif)", "help": "Sans jeton, GitHub limite à 60 requêtes par heure : c'est assez, le module en fait 12." }
  ]
}
```

| Champ | Obligatoire | Ce que c'est |
|---|---|---|
| `id` | oui | L'identifiant `auteur.module`. Minuscules, chiffres et tirets, avec **un point** au milieu, 40 caractères au plus. Le point le distingue des modules d'origine (`clock`, `monitoring`…). |
| `name` | oui | Le nom affiché, 40 caractères au plus. |
| `description` | oui | Une phrase de description, 300 caractères au plus. |
| `author` | oui | Ton nom, 40 caractères au plus. |
| `version` | oui | La version de ton module, au format SemVer : `1.2.3` ou `1.2.3-beta.1`, **sans `v`**. |
| `sdk` | oui | La version **minimale** du SDK dont tu as besoin, `majeur.mineur` (`1.0`). Voir [la compatibilité](compatibilite.md). |
| `entry` | oui | Le nom de ta DLL, à la racine du module, sans dossier. |
| `permissions` | non | Les permissions que tu demandes : `reseau-local`, `internet`, `ce-pc`, `capteurs`, `musique`. Une permission inconnue est refusée. Voir [les permissions](permissions.md). |
| `repository` | non | Ton dépôt GitHub, `propriétaire/dépôt`. Sans lui, pas de mise à jour automatique. Voir [Publier](publier.md). |
| `icon` | non | Le nom d'un fichier PNG à la racine du module. Le manifeste l'accepte ; la fenêtre de réglage de la version 1.0 ne l'affiche pas encore. |
| `minSize` | oui | La plus petite taille de ta case : `{ "columns": 1 à 16, "rows": 1 à 4 }`. |
| `presets` | non | Les tailles proposées : `compact`, `ideal` et `large`, chacune facultative, de la même forme que `minSize`. |
| `settings` | non | Les champs de réglage. Voir [les réglages](reglages.md). |

Un module qui demande `ce-pc` **doit** avoir au moins un champ `port` dans ses réglages, sinon le manifeste est refusé.

Pourquoi tout cela est-il dans un fichier et pas dans le code ? Parce que **le code d'un module non validé ne tourne jamais**, même enfermé. L'utilisateur décide d'après le manifeste, avant.

## Ce qui ne fait pas partie du contrat

`Trofeo.ModuleKit`, que le paquet `AshboardVision.Sdk` embarque, contient des types publics que les **six modules d'origine** utilisent : `IModule`, `IModuleFactory`, `ICommandableModule`, `ModuleContext`, `ModuleDescriptor`, `ModuleCommand`, `ModuleCommandKind`, `ModuleStatus`, `IConfigurableModuleFactory`, `IModuleNotices`, `SettingsDocument`, `CertificatePinning` et `ModulePermissions`.

Ils ne font **pas** partie du contrat des modules de la communauté. Un module qui s'en sert n'a **aucune garantie de compatibilité** : ils peuvent changer sans prévenir. Écris ton module avec `CommunityModule` et ce que cette documentation décrit.

## Pour aller plus loin

- Les permissions et l'accès au réseau : [permissions](permissions.md).
- Le dessin : [dessin](dessin.md).
- Les réglages : [reglages](reglages.md).
