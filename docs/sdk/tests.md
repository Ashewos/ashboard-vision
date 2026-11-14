# Tester

Deux outils, pour deux questions.

| Outil | Question | Vitesse |
|---|---|---|
| **Les aides de test** (`AshboardVision.Sdk.Testing`) | « Mon code fait-il ce que je crois ? » | Quelques millisecondes. Tu peux déboguer pas à pas. |
| **Le banc d'essai** (`ashboard-module check`) | « Mon module marchera-t-il dans Ashboard Vision ? » | Une quarantaine de secondes. |

Utilise les premières à chaque modification, et le second avant de publier.

## Les tests rapides avec `ModuleTestHost`

`ModuleTestHost<TonModule>` fait tourner ton module **dans le processus du test**, sans enfermement et sans le logiciel. Il lui prête de faux services (réglages, secrets, capteurs, musique, heure) et le dessine dans une image avec les vrais thèmes d'Ashboard Vision. Le projet généré par `dotnet new ashboard-module` en contient déjà.

Tu lui donnes le chemin de ton `manifest.json` : il y lit les permissions et les champs `port`. Le projet de tests référence celui du module, ce qui copie `manifest.json` à côté des tests à chaque construction.

*Le modèle : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule.Tests/MonModuleTests.cs`.*

```csharp
    // Le projet de tests référence le module : son manifest.json est copié à côté des tests à chaque construction.
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifest.json");

    private static bool HasDrawing(SKBitmap image) => image.Pixels.Any(pixel => pixel.Alpha > 0);
```

Un premier test : le module démarre, se dessine dans les trois thèmes, et reste en ligne.

*Même fichier : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule.Tests/MonModuleTests.cs`.*

```csharp
    [Theory]
    [InlineData("classique")]
    [InlineData("liquid-glass")]
    [InlineData("pixel")]
    public async Task Render_InEveryTheme_DrawsSomething(string theme)
    {
        await using var host = new ModuleTestHost<ModuleClassName>(ManifestPath);
        await host.StartAsync();

        using var image = host.Render(new GridSize(3, 1), theme);

        Assert.True(HasDrawing(image));
        Assert.Equal(ModuleHealth.Online, host.Health);
    }
```

Dans ce fichier du modèle, `ModuleClassName` est la place du nom de la classe de ton module : `dotnet new` le remplace par le nom de ton projet (`MonModule`, par exemple).

Ce que tu as là :

- `new ModuleTestHost<TonModule>(chemin, réglages, permissionsAccordées)` : les réglages (un `JsonElement`) et les permissions sont facultatifs. Sans permissions précisées, **toutes celles que le manifeste déclare** sont accordées.
- `await host.StartAsync()` crée le module et appelle son `StartAsync`.
- `host.Render(taille, thème)` rend une `SKBitmap` de la taille que ta case aurait sur le damier. Les thèmes sont `classique`, `liquid-glass` et `pixel`.
- `host.Health` et `host.StatusMessage` : l'état que ton module a annoncé avec `SetStatus`.
- `await using` : libère le module et supprime son dossier de données.

### Les réglages

*Même fichier : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule.Tests/MonModuleTests.cs`.*

```csharp
        var settings = JsonDocument.Parse("""{ "message": "Salut tout le monde" }""").RootElement;
        await using var withMessage = new ModuleTestHost<ModuleClassName>(ManifestPath, settings);
```

### Les faux services

| Membre | Ce qu'il fait |
|---|---|
| `host.Secrets` | Le coffre à secrets, en mémoire. Mets-y un jeton **avant** `StartAsync` pour que le module le trouve : `host.Secrets.Set("token", "abc")`. |
| `host.SetSensors(...)`, `host.SetNowPlaying(...)` | Posent les capteurs et la musique que le module recevra (s'il a la permission). `null` : ils ne sont pas disponibles. |
| `host.Time` | Un `FakeTimeProvider`. `Advance(durée)` fait avancer l'heure et déclenche les minuteries ; `SetUtcNow(date)` la fixe. |

Une permission que tu n'accordes pas renvoie « refusé » à ton module, comme dans le logiciel. Et `ConnectLocalAsync` n'ouvre que les ports saisis dans les champs `port` de tes réglages.

### Faire passer le temps

Sans horloge à toi, un test qui dépend de l'heure est un test qui attend. Avec `host.Time`, tu avances à la main.

*Le module « Démo » : `examples/Demo/Demo.Tests/DemoTests.cs`.*

```csharp
        // Un pas dure 83,3 ms : à 0 et à 40 ms on est dans le même pas, à 84 ms dans le suivant.
        host.Time.SetUtcNow(Start);
        using var atStart = host.Render(Size, "classique");
        host.Time.SetUtcNow(Start.AddMilliseconds(40));
        using var samePace = host.Render(Size, "classique");
        host.Time.SetUtcNow(Start.AddMilliseconds(84));
        using var nextPace = host.Render(Size, "classique");

        Assert.Equal(atStart.Bytes, samePace.Bytes);
        Assert.NotEqual(atStart.Bytes, nextPace.Bytes);
```

### Un raccourci

`host.TriggerShortcutAsync("identifiant")` appelle `OnShortcutAsync` de ton module et attend qu'elle ait fini. Un test complet est dans [les raccourcis](raccourcis.md#tester-un-raccourci).

### Une permission refusée

Accorde une liste vide pour voir ce que fait ton module sans rien. Ici : sans Internet, il le dit et ne fait aucune requête.

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo.Tests/GitHubRepoTests.cs`.*

```csharp
    [Fact]
    public async Task GitHub_InternetDenied_SaysSo()
    {
        var github = FakeGitHubHandler.Working();
        await using var host = Host(github, Repo(), granted: []);

        await host.StartAsync();

        Assert.Equal(ModuleHealth.Offline, host.Health);
        Assert.Contains("Internet", host.StatusMessage);
        Assert.Empty(github.Requests);
    }
```

Accorder une permission que le manifeste ne déclare pas est une erreur du test (`ArgumentException`) : le logiciel n'accorde jamais plus que ce qui est déclaré.

### Attendre le travail de fond

Ton module actualise ses données en tâche de fond. Le test doit **attendre** ce qu'il vérifie, sans supposer que c'est déjà là. Un petit assistant suffit.

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo.Tests/GitHubRepoTests.cs`.*

```csharp
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "La condition attendue n'est pas arrivée en cinq secondes.");
    }
```

## Tester sans réseau

Un test ne doit jamais appeler la vraie API de GitHub ou un vrai serveur : il serait lent, et il casserait le jour où le service tombe. L'astuce est simple.

1. Dans ton module, passe l'accès au réseau par une **méthode `internal virtual`**. Elle fait ce qu'il faut en vrai.
2. Dans ton projet de tests, une **sous-classe** de ton module la redéfinit et rend un faux.
3. Dans le `.csproj` de ton module, ajoute `<InternalsVisibleTo Include="TonModule.Tests" />` dans un `<ItemGroup>` : les tests voient alors tes membres `internal`.

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
    internal virtual HttpClient CreateHttpClient() => Services.CreateHttpClient();
```

*La sous-classe de test : `examples/GitHubRepo/GitHubRepo.Tests/FakeGitHub.cs`.*

```csharp
internal sealed class TestableGitHubRepoModule : GitHubRepoModule
{
    public static readonly AsyncLocal<HttpMessageHandler?> Handler = new();

    internal override HttpClient CreateHttpClient() =>
        new(Handler.Value ?? throw new InvalidOperationException("Pose TestableGitHubRepoModule.Handler avant StartAsync."), disposeHandler: false);
}
```

Ton module doit alors être une classe **non scellée** (`public class`, pas `public sealed class`), pour qu'on puisse en hériter. Le gestionnaire est posé dans un `AsyncLocal` : deux tests qui tournent en même temps ne se voient pas.

Le test pose le faux, puis crée l'hôte :

*Les tests du module : `examples/GitHubRepo/GitHubRepo.Tests/GitHubRepoTests.cs`.*

```csharp
    private static ModuleTestHost<TestableGitHubRepoModule> Host(FakeGitHubHandler github, JsonElement? settings, IReadOnlyList<ModulePermission>? granted = null)
    {
        TestableGitHubRepoModule.Handler.Value = github;
        return new ModuleTestHost<TestableGitHubRepoModule>(ManifestPath, settings, granted);
    }
```

Et, pour tester une panne puis le retour à la normale, il fait avancer l'horloge du module :

*Les tests du module : `examples/GitHubRepo/GitHubRepo.Tests/GitHubRepoTests.cs`.*

```csharp
        github.Respond = FakeGitHubHandler.Working().Respond;
        host.Time.Advance(TimeSpan.FromMinutes(10));

        await WaitUntilAsync(() => host.Health == ModuleHealth.Online);
        Assert.Null(host.StatusMessage);
```

Le module « Serveur Minecraft » fait de même avec deux méthodes, une pour les serveurs de ce PC et une pour les autres :

*Le module « Serveur Minecraft » : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
    internal virtual Task<DataResult<Stream>> ConnectLocalAsync(int port, CancellationToken ct) => Services.ConnectLocalAsync(port, ct);
```

*Même fichier : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
    internal virtual Task<Stream> ConnectRemoteAsync(string host, int port, CancellationToken ct) =>
        ServerListPing.ConnectAsync(host, port, ConnectTimeout, ct);
```

### Les limites du faux

Les aides de test ne sont **pas** le logiciel. Elles ne t'enferment pas, ne vérifient pas tout ton manifeste (elles n'en lisent que les permissions et les champs `port`) et simplifient plusieurs points : le message d'état n'est pas tronqué, il n'y a pas de plafond de 16 connexions, `Invalidate` ne redessine rien (c'est toi qui appelles `Render`), la taille minimale n'est pas vérifiée. La liste complète est dans [le README des aides de test](../../src/AshboardVision.Sdk.Testing/README.md#limites-du-faux).

Pour tout ce qui touche à l'enfermement, utilise le banc d'essai.

## Le banc d'essai

`ashboard-module check` lance ton module **dans les mêmes conditions que le logiciel** : avec le vrai hôte de module (livré dans l'outil, tu n'as pas besoin d'installer Ashboard Vision), enfermé, avec les permissions de ton manifeste.

```powershell
ashboard-module check MonModule/bin/Debug/net10.0 --out check-out
```

- Le premier argument est le dossier de ton module : celui où `manifest.json` est à côté de la DLL. Sans lui, la commande prend le dossier où tu es.
- `--out` est le dossier où écrire les images PNG (`check-out` si tu ne le dis pas).

Elle fait huit vérifications, dans l'ordre. Chaque ligne commence par `✔` (réussi), `!` (à regarder) ou `✖` (échoué).

| # | Vérification |
|---|---|
| 1 | **Le manifeste** : champs obligatoires, identifiant, version du SDK, permissions connues… avec les règles du logiciel. |
| 2 | **La compatibilité** : le SDK que ton module demande, face à celui que parle le logiciel. |
| 3 | **Le démarrage enfermé** : ton module doit envoyer sa première image en moins de 10 secondes. |
| 4 | **Les images** : une image PNG pour ta taille minimale et chacun de tes préréglages, dans les trois thèmes. Les fichiers s'appellent `<id>_<thème>_<colonnes>x<lignes>.png`. |
| 5 | **Chaque permission refusée, l'une après l'autre** : le module ne doit pas s'arrêter. |
| 6 | **Les raccourcis** (si ton manifeste en déclare) : chacun est livré à ton module enfermé, et ni une exception de `OnShortcutAsync` ni l'arrêt de l'hôte ne sont permis. Voir [les raccourcis](raccourcis.md#tester-un-raccourci). |
| 7 | **La mesure** : le temps de dessin, le processeur, la mémoire. |
| 8 | **L'arrêt propre** : l'hôte s'arrête en moins de 5 secondes. |

Elle conclut « Compatible avec Ashboard Vision (SDK 1.1). », avec le code de sortie 0, ou « Pas compatible : … » avec la raison de chaque échec, en français, et le code 1. Un `!` ne rend jamais le module incompatible.

Voici à quoi ressemble la sortie. Les chiffres changent d'une machine à l'autre.

```text
✔ Manifeste : ashewos.demo 1.0.0 (Démo)
✔ Compatibilité : le module demande le SDK 1.1, Ashboard Vision parle le 1.1.
✔ Démarrage enfermé : première image en 0,6 s (sans permission).
✔ Images : 6 images PNG écrites dans « C:\dev\demo\check-out » (2 tailles × 3 thèmes).
✔ Raccourcis : 1 livré au module (colour), sans erreur.
✔ Dessin : 1,8 ms en moyenne, 4,1 ms au plus (120 dessins demandés, de la demande à l'image reçue).
✔ Processeur : 0,3 % de la machine en moyenne sur 30,0 s
✔ Mémoire : 58,4 Mio au plus (mémoire privée de l'hôte)
✔ Arrêt propre : l'hôte s'est arrêté en 0,1 s.

Compatible avec Ashboard Vision (SDK 1.1).
```

### Les repères de consommation

Le banc utilise les mêmes repères que la fenêtre de réglage : au-dessus de **2 % du processeur de la machine** ou de **150 Mio de mémoire**, il écrit une ligne `!` qui le dit. Ce n'est pas un échec : le module est « compatible », mais il sera signalé « Consommation élevée » chez l'utilisateur.

### Ce qu'il faut savoir de la mesure

- **Elle dure 30 secondes**, avec quatre demandes de dessin par seconde.
- **Le temps de dessin est un aller-retour** : la demande part du logiciel (ici, le banc), l'hôte dessine, l'image revient. C'est plus que le temps de ton `Render` seul.
- **La mémoire est comparée en pic**, alors que le logiciel fait une **moyenne sur 60 secondes**. Un module un peu au-dessus du repère au banc peut rester dessous chez l'utilisateur.
- **Le passage « permission refusée » n'observe le module que 2 secondes** après sa première image. Une panne qui arrive plus tard n'y est pas vue.
- **Le banc lance ton module avec les valeurs proposées par les champs de ton manifeste**, les textes restant vides et les secrets absents. Un module qui demande une configuration (une adresse, un jeton) reste donc `NotConfigured` pendant le banc : il passe, mais le banc ne montre pas le vrai travail. **Essaie aussi ton module dans Ashboard Vision**, avec le [mode développeur](demarrer.md#le-voir-dans-ashboard-vision), en remplissant ses réglages.
- **Un état `Offline` avec un message est compté comme une erreur**, sauf pour le test d'une permission refusée, où dire « permission refusée » est justement la bonne réponse. Un module non configuré doit rester `NotConfigured` (voir [le contrat](contrat.md#un-module-non-configuré-se-dit-notconfigured)).
- Le banc donne de **fausses données** pour les capteurs et la musique. Il n'appelle aucun réseau lui-même : seul ton module le fait, avec ses permissions.

### Ce que le banc touche, et ne touche pas

Le banc n'utilise ni tes réglages d'Ashboard Vision, ni la liste de tes modules validés, ni ton coffre à secrets.

Il crée son propre **conteneur d'essai** Windows, nommé `ashboard-vision.check.<id>` (jamais `ashboard-vision.mod.<id>`, qui est celui du logiciel), et le **supprime à la fin**, même en cas d'échec ou de Ctrl+C.

Si le processus est tué brutalement (le gestionnaire des tâches, une coupure de courant), le conteneur peut rester. Il est **sans danger**. Il n'y a pas de commande simple pour le retirer à la main ; la prochaine exécution de `check` pour le même module le réutilise, puis le supprime.

### Quand ça échoue

Une ligne `✖` dit ce qui coince. Quand l'hôte s'arrête, le banc affiche les dernières lignes du journal de ton module avant la conclusion :

```text
✖ Sans la permission « internet » : l'hôte du module s'arrête (code E0434352). L'utilisateur peut refuser n'importe quelle permission : le module doit s'en passer.

Dernières lignes du journal du module :
    [les dernières lignes du journal de ton module]

Pas compatible : le module s'arrête quand la permission « internet » est refusée.
```

Dans un cas comme celui-ci, le module ne vérifie pas `Services.Has(ModulePermission.Internet)` et laisse une exception sortir d'une minuterie ou d'une tâche. Voir [le contrat](contrat.md#une-fermeture-sûre).
