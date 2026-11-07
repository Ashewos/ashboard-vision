# Démarrer

Dans cette page, tu installes le SDK, tu crées un module avec une seule commande, puis tu le vois dans Ashboard Vision. Compte une vingtaine de minutes.

## Ce qu'il te faut

- **Windows 10 ou 11, 64 bits.** Le banc d'essai (`ashboard-module`) ne marche que sous Windows, comme le logiciel.
- **Le SDK .NET 10.** L'installateur d'Ashboard Vision le télécharge et l'installe s'il manque (voir « Avec l'installateur » plus bas). Sinon, tu le télécharges sur <https://dotnet.microsoft.com/download/dotnet/10.0>. Pour savoir s'il est installé, tape `dotnet --list-sdks` dans un terminal : une ligne doit commencer par `10.`.
- **Un éditeur de code** : Visual Studio, Rider, VS Code…
- **Ashboard Vision**, si tu veux voir ton module sur l'écran. Pour écrire et tester, il n'est pas obligatoire.
- **Une connexion Internet la première fois.** Le SDK lui-même n'est jamais sur nuget.org (voir plus bas), mais la restauration d'un projet va chercher SkiaSharp et les paquets de test sur nuget.org, comme n'importe quel projet .NET.

## Installer le SDK

Le SDK tient en quatre paquets `.nupkg` (un `.nupkg` est un paquet NuGet, le format de bibliothèques de .NET) :

| Paquet | Il sert à |
|---|---|
| `AshboardVision.Sdk` | écrire le module (`CommunityModule`, les services, les permissions) |
| `AshboardVision.Sdk.Testing` | le tester sans le lancer pour de vrai |
| `AshboardVision.Sdk.Templates` | créer un projet avec `dotnet new ashboard-module` |
| `AshboardVision.Sdk.Tool` | la commande `ashboard-module` : `check` et `pack` |

Ils ne sont **pas sur nuget.org**. Deux façons de les avoir.

### Avec l'installateur d'Ashboard Vision

Pendant l'installation, la page des options propose **« Outils pour créer des modules (SDK) »**. La case est décochée par défaut. Cochée, l'installateur :

1. copie les quatre paquets dans le dossier `sdk` du dossier d'installation (par défaut `C:\Program Files\Ashboard Vision\sdk`) ;
2. si le SDK .NET 10 est installé sur le PC, ajoute ce dossier comme source de paquets NuGet de ton compte (elle s'appelle `AshboardVision`), installe le modèle de projet et l'outil `ashboard-module`, avec tes droits (pas ceux de l'administrateur) ;
3. sinon, **télécharge et installe le SDK .NET 10** (environ 220 Mo, depuis le site de Microsoft), pour tout l'ordinateur, puis fait comme au point 2. Avant de le lancer, l'installateur vérifie le fichier : son empreinte est celle que Microsoft publie, et sa signature est celle de Microsoft. Un fichier qui échoue à l'une des deux n'est jamais lancé. Pour ne garder que le SDK d'Ashboard Vision, décoche la case et installe le SDK .NET 10 toi-même.
4. si le téléchargement ou l'installation échoue (pas d'Internet, par exemple), te le dit en une phrase, avec l'adresse <https://dotnet.microsoft.com/download>. Les quatre paquets sont tout de même copiés dans `sdk` ; le modèle et l'outil ne sont pas installés. Pour finir, installe le SDK .NET 10, puis soit tu relances l'installateur d'Ashboard Vision en cochant la case, soit tu tapes les trois commandes de « À la main » ci-dessous en prenant `C:\Program Files\Ashboard Vision\sdk` comme dossier des paquets (au lieu de `C:\paquets-ashboard`).

Si `dotnet` ou `ashboard-module` n'est pas reconnu juste après l'installation, ouvre un nouveau terminal : Windows ne relit le `PATH` qu'à l'ouverture d'un terminal.

Lors d'une mise à jour d'Ashboard Vision, la case reprend ton choix précédent : cochée si le SDK était déjà installé. Décochée alors qu'elle était cochée, elle fait retirer la source, le modèle et l'outil. Quand la mise à jour est lancée par le logiciel (mode silencieux), l'installateur n'affiche **aucune boîte** et ne télécharge **rien** : si le SDK .NET 10 manque, seul son journal le dit.

La désinstallation d'Ashboard Vision retire la source, le modèle et l'outil, si tu avais installé le SDK. Le SDK .NET 10, lui, n'est **jamais retiré** : c'est un programme à part, que tu désinstalles dans « Applications installées » si tu n'en veux plus. Une limite : elle le fait avec le compte qui a validé la demande d'administrateur. Si c'est un autre compte que le tien (un administrateur qui a saisi son mot de passe), ils restent dans ton profil : retire-les toi-même avec `dotnet tool uninstall -g AshboardVision.Sdk.Tool`, `dotnet new uninstall AshboardVision.Sdk.Templates` et `dotnet nuget remove source AshboardVision`.

### À la main, depuis une version GitHub

Chaque version d'Ashboard Vision sur GitHub porte ces quatre fichiers `.nupkg`, à côté de l'installateur. Télécharge-les dans un dossier (ici `C:\paquets-ashboard`), puis, dans un terminal :

```powershell
dotnet nuget add source "C:\paquets-ashboard" --name AshboardVision
dotnet new install "C:\paquets-ashboard\AshboardVision.Sdk.Templates.1.0.0.nupkg"
dotnet tool install -g AshboardVision.Sdk.Tool --source "C:\paquets-ashboard"
```

La première commande dit à .NET où trouver les paquets du SDK. La deuxième installe le modèle de projet. La troisième installe la commande `ashboard-module` ; elle utilise `--source` (et non `--add-source`) pour ne chercher que dans ce dossier : ça marche hors ligne, et aucun paquet du même nom ne peut venir d'une autre source.

Pour vérifier, tape `ashboard-module --help` : l'aide s'affiche en français.

## Créer ton premier module

```powershell
dotnet new ashboard-module -n MonModule --id moi.mon-module --param:author "Ton nom"
```

- `-n` : le nom du projet et de la DLL (sans espace). Il en sort une classe C# qui porte ce nom. Un point ou un tiret dans le nom devient « _ » dans le nom de la classe (`Moi.Essai` donne la classe `Moi_Essai`). Un mot réservé du C# (`class`, `event`…) donne une classe qui ne compile pas : choisis un autre nom.
- `--id` : l'**identifiant** du module, de la forme `auteur.module` : minuscules, chiffres et tirets, avec un point au milieu, 40 caractères au plus (par exemple `moi.mon-module`). Il ne change plus après la publication : c'est lui qui relie la disposition et les secrets de l'utilisateur à ton module. Le modèle ne le vérifie pas, mais le banc d'essai le refuse s'il est mal formé.
- `--param:author` : ton nom, tel qu'il s'affichera dans la fenêtre de réglage. Écris bien `--param:author` (ou `-au`) et pas `--author` : `dotnet new` garde ce nom pour ses recherches.

Tu obtiens ce dossier :

```text
MonModule/
  MonModule.slnx           la solution
  README.md                un mémo : construire, tester, essayer, publier
  MonModule/               le module
    MonModule.cs           la classe du module
    MonModule.csproj       le projet
    manifest.json          la carte d'identité du module
  MonModule.Tests/         les tests
```

Le module généré affiche un message (« Bonjour » par défaut) et l'heure, aux couleurs du thème. Il ne demande aucune permission. Tu peux déjà le construire et le tester :

```powershell
cd MonModule
dotnet build
dotnet test
```

## Les trois fichiers à connaître

### Le manifeste

`manifest.json` est lu **avant** que le moindre code de ton module tourne. C'est ce que la fenêtre de réglage montre à l'utilisateur quand il décide d'installer ou non ton module.

*Le modèle : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule/manifest.json`.*

```json
{
  "id": "auteur.mon-module",
  "name": "MonModule",
  "description": "Dit bonjour et affiche l'heure.",
  "author": "Ton nom",
  "version": "1.0.0",
  "sdk": "1.0",
  "entry": "MonModule.dll",
  "permissions": [],
  "minSize": { "columns": 2, "rows": 1 },
  "presets": {
    "compact": { "columns": 2, "rows": 1 },
    "ideal": { "columns": 3, "rows": 1 }
  },
  "settings": [
    { "kind": "text", "key": "message", "label": "Message", "placeholder": "Bonjour", "maxLength": 40 }
  ]
}
```

Dans ce fichier, `id` et `author` prennent les valeurs que tu as données à la commande. Tous les champs sont expliqués dans [le contrat](contrat.md#le-manifeste).

### Le projet

*Le modèle : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule/MonModule.csproj`.*

```xml
<PackageReference Include="AshboardVision.Sdk" Version="1.0.0" />
```

Le SDK est fixé à la version **exacte** `1.0.0`, sans étoile. Une version flottante (`1.0.*`) obligerait chaque restauration à interroger les sources de paquets, donc à échouer hors ligne. Quand un SDK 1.x plus récent sort, tu changes ce numéro **à la main**, et tu changes en même temps le champ `sdk` du manifeste. Voir [la compatibilité](compatibilite.md).

### La classe du module

*Le modèle : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule/MonModule.cs`. Le nom de la classe est celui de ton projet.*

```csharp
    public override Task StartAsync(CancellationToken ct)
    {
        var settings = Services.Settings;
        if (settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()))
        {
            _message = message.GetString()!.Trim();
        }

        // L'heure change une fois par minute : un seul minuteur, réglé sur la prochaine minute pleine. Sans lui, l'image ne serait
        // redessinée que quand le logiciel le décide (changement de thème, de taille…).
        var now = Services.Time.GetUtcNow();
        var untilNextMinute = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromTicks(now.Ticks % TimeSpan.TicksPerSecond);
        _minuteTimer = Services.Time.CreateTimer(_ => Invalidate(), null, untilNextMinute, TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }
```

`StartAsync` est appelée une fois, quand la case s'ouvre. Elle lit les réglages de la case (`Services.Settings`) et lance une minuterie. `Invalidate()` veut dire « redessine-moi ».

Le dessin se fait dans `Render`, appelé chaque fois qu'une image est nécessaire.

*Début de `Render`, dans le même fichier : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule/MonModule.cs`.*

```csharp
        var time = TimeZoneInfo.ConvertTime(ctx.Now, Services.Time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

        var scale = ctx.TextScale;
        var area = SKRect.Inflate(ctx.Bounds, -Padding * scale, -Padding * scale);
```

`ctx.Bounds` est le rectangle de ta case, en pixels. `ctx.TextScale` est un facteur que l'utilisateur règle (texte petit, normal ou grand) : multiplie tes tailles de texte par lui. Tout le détail est dans [Dessiner](dessin.md).

## Essayer ton module comme le logiciel le lance

Les tests rapides font tourner ton module dans le processus du test. Le **banc d'essai** le lance enfermé, comme Ashboard Vision :

```powershell
ashboard-module check MonModule/bin/Debug/net10.0 --out check-out
```

Il vérifie le manifeste, démarre le module, dessine une image PNG par taille et par thème dans `check-out`, refuse tes permissions une à une, mesure la consommation (30 secondes), et conclut « Compatible » ou dit ce qui coince. Voir [Tester](tests.md).

## Le voir dans Ashboard Vision

Le **mode développeur** charge un module depuis son dossier de travail, sans fabriquer de `.avmod`. Il sert à essayer pendant que tu écris, pas à utiliser durablement.

1. Ouvre la fenêtre de réglage, onglet **Paramètres**, section **Modules complémentaires**.
2. Allume **Mode développeur**. Le bandeau « Mode développeur actif » reste affiché tant qu'il est allumé.
3. Clique sur **Ajouter un dossier…** et choisis le dossier de sortie de ta construction : celui où `manifest.json` est à côté de la DLL (`MonModule/bin/Debug/net10.0`, dans le dossier du module).
4. La fenêtre « Tester un module en développement » te montre l'avertissement et les permissions, comme pour une installation. Coche celles que tu veux essayer, puis clique sur **Ajouter**.
5. Ton module apparaît dans la liste des modules à poser sur le damier, avec le point d'exclamation jaune de tous les modules de la communauté. Pose-le sur l'écran.
6. Après chaque construction, clique sur **Recharger**. Le logiciel ne surveille pas ton dossier.

Éteindre le mode développeur retire ces modules de l'écran. Pour garder un module, fabrique son `.avmod` et installe-le normalement ([Publier](publier.md)).

## Et ensuite

- Comprendre ce que tu viens de lancer : [le contrat](contrat.md).
- Lire les trois exemples du dépôt : [`examples`](../../examples).
- Ajouter des réglages : [les réglages](reglages.md).
