# Dessiner

Un module dessine **ce qu'il veut dans sa case**, pixel par pixel, avec [SkiaSharp](https://learn.microsoft.com/dotnet/api/skiasharp) (la bibliothèque de dessin 2D de .NET). Cette page explique où tu dessines, avec quelles couleurs, et comment faire tenir du texte dans une petite case.

## Où tu dessines

L'écran fait **1920 × 462 pixels**. Le logiciel le découpe en un damier de **16 colonnes sur 4 lignes**. Une case de ton module couvre un certain nombre de colonnes et de lignes (entre ta taille minimale, `minSize`, et tout le damier).

À titre d'ordre de grandeur, avec les écarts du thème Classique :

| Taille de la case | Environ |
|---|---|
| 1 colonne × 1 ligne | 107 × 100 pixels |
| 3 colonnes × 1 ligne | 345 × 100 pixels |
| 4 colonnes × 1 ligne | 464 × 100 pixels |
| 16 colonnes × 4 lignes (tout l'écran) | 1 892 × 434 pixels |

Les écarts dépendent du thème. **N'écris jamais ces nombres dans ton code** : lis la taille dans `ctx.Bounds`.

Quand tu dessines :

- **Le canevas est transparent.** Le logiciel pose ton image **par-dessus le panneau de la case**, déjà peint, avec son fond et ses coins arrondis. Ne peins pas de fond, sauf si tu veux vraiment cacher le panneau.
- **Tu dessines toujours à l'endroit.** La rotation de 180° de l'écran et le découpage des bords arrondis sont l'affaire du logiciel.
- **Tu dessines dans `ctx.Bounds`.** Ce rectangle commence en (0, 0) et fait la taille exacte de ta case.

## Ce que `Render` reçoit

La signature est `public override void Render(SKCanvas canvas, ModuleRenderContext ctx)`. `ctx` contient :

| Membre | Ce que c'est |
|---|---|
| `Bounds` | Le rectangle de ta case, en pixels. |
| `Size` | La taille de ta case sur le damier (`Columns`, `Rows`). |
| `Theme` | Le thème : couleurs, police, forme. Voir plus bas. |
| `TextScale` | Le facteur de taille du texte que l'utilisateur a choisi : 0,85 (petit), 1 (normal) ou 1,2 (grand). **Multiplie tes tailles de texte par lui.** |
| `Now` | L'heure de ce dessin. |

## Le thème

Ashboard Vision a trois thèmes : `classique`, `liquid-glass` et `pixel`. L'utilisateur en choisit un pour tout l'écran. Ton module n'a pas à y ressembler, mais il est plus agréable de s'y accorder. Prends les couleurs dans `ctx.Theme` :

| Membre | Pour quoi faire |
|---|---|
| `Text` | Le texte principal. |
| `TextMuted` | Le texte secondaire. |
| `Highlight` | Les titres mis en avant. |
| `Accent` | La couleur d'accent. |
| `Warning` | Les avertissements. |
| `Track` | La piste d'une barre ou d'une jauge. |
| `Surface` | Une zone en relief. |
| `Background`, `Panel`, `PanelOpacity` | Le fond et le panneau (peints par le logiciel). |
| `CornerRadius` | L'arrondi des panneaux. |
| `Typeface` | La police du thème (utilise plutôt `ModuleDrawing.Font`, voir plus bas). |
| `Shape` | `ThemeShape.Rounded` (coins arrondis, barres pleines, points ronds) ou `ThemeShape.Pixel` (angles vifs, points carrés). |

Le module « Démo » suit la forme du thème : son rayon est nul en `Pixel`.

*Le module « Démo » : `examples/Demo/Demo/DemoModule.cs`.*

```csharp
        // La tête a la couleur d'accent du thème, suivie d'une traînée qui s'éteint ; les autres cases ont la couleur de piste.
        byte[] trail = [255, 150, 80, 40];
        using var paint = new SKPaint { IsAntialias = true };
        for (var cell = 0; cell < Cells; cell++)
        {
            var behind = (head - cell + Cells) % Cells;
            paint.Color = behind < trail.Length ? theme.Accent.WithAlpha(trail[behind]) : theme.Track;
            var left = track.Left + cell * (cellWidth + gap);
            canvas.DrawRoundRect(new SKRect(left, top, left + cellWidth, top + cellHeight), radius, radius, paint);
        }
```

N'écris pas de couleur en dur pour le texte. Les trois thèmes n'ont ni les mêmes couleurs, ni la même forme : regarde ton module dans chacun.

## Le texte

Le texte demande trois précautions : la **police**, la **taille** et la **place**.

`ModuleDrawing` (dans `Trofeo.ModuleKit`) les gère pour toi :

| Fonction | Ce qu'elle fait |
|---|---|
| `ModuleDrawing.Font(theme, taille)` | La police du thème, à la taille donnée. Le thème peut agrandir les caractères sans toucher à ta mise en page. |
| `ModuleDrawing.DrawText(canvas, texte, x, ligneDeBase, alignement, police, pinceau)` | Dessine le texte. Un caractère que la police n'a pas (un emoji, un caractère japonais) est dessiné avec une police de secours du système. |
| `ModuleDrawing.MeasureText(texte, police)` | La largeur du texte, polices de secours comprises. |
| `ModuleDrawing.Ellipsize(texte, police, largeurMax)` | Raccourcit le texte avec « … » pour qu'il tienne. |
| `ModuleDrawing.Wrap(texte, police, largeurMax, lignesMax)` | Coupe le texte en lignes. |
| `ModuleDrawing.DrawBar(canvas, rect, fraction, couleur, theme)` | Une barre de progression dans le style du thème (pleine en `Rounded`, en segments en `Pixel`). |
| `ModuleDrawing.BarRect(rect, theme)` | Le rectangle de la barre, ajusté à la forme du thème. |
| `ModuleDrawing.DrawActionMessage(canvas, zone, theme, textScale, message)` | Un message centré, en couleur d'avertissement, sur trois lignes au plus. |

Dessine **toujours** avec `ModuleDrawing.DrawText`, pas avec `canvas.DrawText` : un caractère que la police du thème n'a pas ne s'afficherait pas correctement sans la police de secours.

*Le module « Démo » : `examples/Demo/Demo/DemoModule.cs`.*

```csharp
        var theme = ctx.Theme;
        var scale = ctx.TextScale;
        var area = SKRect.Inflate(ctx.Bounds, -16f * scale, -12f * scale);

        using var font = ModuleDrawing.Font(theme, 18f * scale);
        using var textPaint = new SKPaint { Color = theme.TextMuted, IsAntialias = true };
        var baseline = area.Top - font.Metrics.Ascent;
        var sentence = _animate ? $"Animation à {StepsPerSecond} images par seconde" : "Animation arrêtée";
        ModuleDrawing.DrawText(canvas, ModuleDrawing.Ellipsize(sentence, font, area.Width), area.Left, baseline, SKTextAlign.Left, font, textPaint);
```

Quatre habitudes à prendre, qu'on voit dans cet extrait :

1. une **marge** autour du dessin (`SKRect.Inflate` avec un nombre négatif), multipliée par `TextScale` ;
2. une **taille de texte** multipliée par `TextScale` ;
3. `Ellipsize` pour tout texte dont tu ne maîtrises pas la longueur ;
4. un `using` sur chaque `SKFont` et `SKPaint` : tu crées des objets natifs à chaque image, ils doivent être libérés.

### Faire tenir plusieurs lignes dans la case

Une case de 3 × 1 est basse. Quand tu empiles plusieurs lignes, calcule un **facteur de réduction** commun, pour que l'ensemble tienne en hauteur.

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
        var lines = Lines(_repository, stats, ctx.Theme, Services.Time.LocalTimeZone);
        var scale = ctx.TextScale;
        var area = SKRect.Inflate(ctx.Bounds, -16f * scale, -12f * scale);
        var fit = Math.Min(1f, area.Height / (lines.Sum(line => line.Size) * 1.25f * scale));
        if (fit <= 0f || area.Width <= 0f)
        {
            return;
        }

        var top = area.Top;
        foreach (var (text, size, color) in lines)
        {
            using var font = ModuleDrawing.Font(ctx.Theme, size * scale * fit);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            ModuleDrawing.DrawText(canvas, ModuleDrawing.Ellipsize(text, font, area.Width), area.Left, top - font.Metrics.Ascent, SKTextAlign.Left, font, paint);
            top += font.Spacing * 1.05f;
        }
```

Remarque : `Lines(...)` est une fonction à part, qui rend le texte de chaque ligne, sa taille et sa couleur. Cela permet de tester **ce qui est affiché** sans lire de pixels (voir [Tester](tests.md)).

### Quand il n'y a rien à dessiner

Avant la première réponse du réseau, ton module n'a rien à montrer. Ne dessine rien : le logiciel affiche lui-même le message de l'état (non configuré, hors ligne…).

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
        // Pas encore de réponse : on ne dessine rien. Le logiciel affiche lui-même le message de l'état (non configuré, hors ligne…).
        if (_stats is not { } stats)
        {
            return;
        }
```

## Les images

Une image (une pochette, une icône de serveur, une capture de caméra) se dessine avec `canvas.DrawImage`. Deux règles :

- **Décode-la une fois, pas à chaque image.** Garde l'`SKImage` décodé tant que les octets sources ne changent pas.
- **Méfie-toi de ce que tu décodes.** Un petit PNG peut se décompresser en gigaoctets. Vérifie la taille avant de décoder.

*Le module « Serveur Minecraft » : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
    private SKImage? IconFor(byte[]? png)
    {
        if (!ReferenceEquals(png, _iconSource))
        {
            _icon?.Dispose();
            _icon = Decode(png);
            _iconSource = png;
        }

        return _icon;
    }

    private static SKImage? Decode(byte[]? png)
    {
        if (png is null)
        {
            return null;
        }

        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        return codec is { Info: { Width: >= 1 and <= MaxIconSide, Height: >= 1 and <= MaxIconSide } } ? SKImage.FromEncodedData(data) : null;
    }
```

Puis le dessin : l'icône est coupée aux coins du thème, et lissée sauf en `Pixel`, où l'on veut des pixels nets.

*Même fichier : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
        var left = area.Left;
        if (IconFor(status.FaviconPng) is { } icon)
        {
            var side = Math.Min(area.Height, area.Width / 3f);
            var target = new SKRect(area.Left, area.MidY - side / 2f, area.Left + side, area.MidY + side / 2f);
            var radius = theme.Shape == ThemeShape.Pixel ? 0f : 8f * scale;
            var sampling = theme.Shape == ThemeShape.Pixel ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear);
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(target, radius, radius), antialias: true);
            canvas.DrawImage(icon, target, sampling);
            canvas.Restore();
            left = target.Right + 12f * scale;
        }
```

Le module décode l'image dans `Render`, qui est le seul à la lire : l'image n'est donc jamais libérée pendant qu'il la dessine. Elle est libérée dans `DisposeAsync`, après le dernier dessin.

## Animer

Un module est redessiné **quand il le demande** :

- avec `Invalidate()`, pour un dessin de plus (une donnée est arrivée) ;
- avec `IsAnimating`, tant que tu veux être redessiné en continu (une animation, une caméra).

*Le module « Démo » : `examples/Demo/Demo/DemoModule.cs`.*

```csharp
    public override bool IsAnimating => _animate;
```

Tu ne choisis pas la cadence : tant que `IsAnimating` est vrai, le logiciel redemande une image dès qu'il peut, dans la limite du réglage « images par seconde » de l'écran, qui va de 1 à 12. Une caméra en direct écrit donc plusieurs images par seconde, dans cette limite, et pas plus.

Écris ton animation en fonction de **l'heure** (`ctx.Now`), pas du nombre d'images dessinées : elle avance alors à la même vitesse que le plafond soit de 5 ou de 12 images.

*Le module « Démo » : `examples/Demo/Demo/DemoModule.cs`.*

```csharp
        // Le pas ne dépend que de l'heure : deux images dessinées dans le même douzième de seconde sont identiques. Sans animation, la tête reste en place.
        var step = ctx.Now.ToUnixTimeMilliseconds() * StepsPerSecond / 1000;
        var head = _animate ? (int)(step % Cells) : 0;
        var top = track.MidY - cellHeight / 2f;
        var radius = theme.Shape == ThemeShape.Pixel ? 0f : cellWidth * 0.3f;
```

Un module qui ne s'anime pas n'a aucune raison de laisser `IsAnimating` à `true` : ne demande des images que si tu en as besoin.

### Ce que ça coûte

- Chaque module actif est un processus de plus, avec sa propre mémoire : le banc d'essai te dit combien le tien en prend (la ligne « Mémoire »), et la limite à ne pas dépasser est le repère de 150 Mio ci-dessous.
- La fenêtre de réglage affiche la consommation de chaque module (Paramètres, **Consommation des modules**) et signale **Consommation élevée** quand un module dépasse **2 % du processeur de la machine** ou **150 Mio de mémoire**, en moyenne sur une minute. C'est un avertissement, pas un blocage.
- Windows arrête de force un module qui dépasse **512 Mio de mémoire**, et lui plafonne le processeur à **25 %** de la machine. Ce sont des garde-fous contre les énormités, pas des objectifs.

Le banc d'essai mesure ces trois choses pour toi : voir [Tester](tests.md#le-banc-dessai).

## Les images de contrôle

La commande `ashboard-module check` écrit une image PNG par taille et par thème. Regarde-les : c'est le plus rapide pour voir qu'un texte dépasse, qu'un contraste est mauvais, ou que le thème `pixel` change ton rendu.
