# Plan 9 — Thème « Liquid Glass avancé » : plan de construction

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**But :** un quatrième thème, « Liquid Glass avancé », dont le verre adapte l'effet de liquidglass (MIT), avec 17 réglages, 4 préréglages et une pastille de verre pour l'élément choisi, identique pour les modules natifs et ceux de la communauté. La version visée est la préversion 1.2.0-beta.2.

**Architecture :**
- **Le verre.** Le programme `FS_GLASS` de liquidglass est adapté en SkSL (`SKRuntimeEffect`) et exécuté par le processeur. Pour chaque case, une couche d'ombre et une couche de verre sont calculées une fois, puis gardées tant que le fond, le rectangle, les réglages et le thème ne changent pas. La teinte et la garde de lisibilité se posent par-dessus.
- **La pastille.** Dans ce thème, le dessin de chaque module est enregistré (`SKPictureRecorder`) : la pastille est dessinée en premier, puis le dessin du module est rejoué par-dessus. `ModuleDrawing.DrawSelection` ne dessine rien lui-même : il note des rectangles. L'hôte des modules de la communauté envoie ces rectangles avec son image.

**Technologies :** .NET 10, C#, SkiaSharp 3.119.4 (SkSL, `SKRuntimeEffect`, `SKPictureRecorder`), Avalonia (fenêtre), xUnit.

**Conception :** `docs/superpowers/specs/2026-11-14-plan-9-liquid-glass-avance-design.md`, validée par Alex le 14/11/2026. Elle prime sur ce plan.

**Référence de l'effet** (en lecture seule, sous licence MIT) : [ybouane/liquidglass](https://github.com/ybouane/liquidglass), au commit `59af227103795f06421a96e2c7d3dceb081decd2`.
- Pour lire un fichier : `gh api "repos/ybouane/liquidglass/contents/src/<fichier>?ref=59af227103795f06421a96e2c7d3dceb081decd2" --jq .content | base64 -d`.
- Fichiers utiles : `shaders.ts` (`FS_GLASS`, `FS_BLUR`), `defaults.ts`, `GlassRenderer.ts` (flou et uniformes), et `site/index.html` (curseurs de la démo, lignes 1132 à 1146).
- La démo : https://liquid-glass.ybouane.com/.

## Contraintes globales

- **Projet.**
  - Les règles de `CLAUDE.md` s'appliquent.
  - Debug seulement (`dotnet build`, `dotnet test`, `dotnet pack -c Debug`). Jamais `-c Release`, jamais `installer/build.ps1`.
  - Le travail se fait sur la branche `plan-9-verre`, partie de `Canary`. Rien n'est poussé.
- **Commits.**
  - Adresse : `1345997+Ashewos@users.noreply.github.com`.
  - Messages en français, avec les préfixes `feat(…)`, `fix(…)`, `test` et `docs`.
  - Ils se terminent par `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`, ou `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>` pour une tâche faite sur Fable.
  - Les fichiers sont ajoutés un par un. Jamais `docs/guide-personnalisation.md`, `.claude/launch.json`, `apercus-accueil-b/` ni `.superpowers/`.
- **Modèles.**
  - Tâches 2, 3 et 4 (l'effet, les couches, la pastille) et relecture finale : **Fable 5.2** (`model: fable`).
  - Les autres tâches : Sonnet, effort élevé.
- **Textes et code.** Les textes affichés sont en français et tutoient. Les noms de code sont en anglais, les commentaires en français, comme le reste du dépôt.
- **Le service et la fenêtre d'Alex** ne sont jamais arrêtés ni lancés.
- **Valeurs fixées.**
  - Identifiant du thème : `liquid-glass-avance`. Nom affiché : « Liquid Glass avancé ».
  - Préréglages : `clair` (par défaut), `givre`, `prisme`, `discret`, plus l'état `personnalise`.
  - SDK **1.2.0**, manifeste `"sdk": "1.2"`. Version du logiciel : **1.2.0-beta.2**.
- **Les autres thèmes ne bougent pas.** Classique, Liquid Glass et Pixel restent identiques **au pixel près**, y compris l'onglet choisi de Claude Code / Codex.
- **Isolation.**
  - Un module de la communauté ne reçoit jamais de pixels du fond ni d'une autre case.
  - Tout ce qu'envoie un hôte est une entrée hostile (règles du plan 7).
- **Licence.**
  - Le SkSL adapté porte en tête la mention MIT de liquidglass (« Copyright (c) ybouane », et le lien vers le dépôt et le commit).
  - Le texte complet de la licence est dans `src/Trofeo.Core/Rendering/LiquidGlass/LICENSE-liquidglass.txt`, copié dans le dossier de sortie comme les licences OFL des polices.

## Points de vigilance

Voici les cinq cas que la conception implique sans qu'une tâche les teste naturellement. Chacun a son test dans la tâche indiquée.

1. **Fond animé avec la garde de lisibilité.** L'opacité de la teinte reste lissée dans le temps (`PanelAlphaSmoother`), comme aujourd'hui. Une tache claire qui passe ne fait pas clignoter la case, et la teinte n'est jamais gardée dans la couche (tâche 3).
2. **Aller-retour entre les thèmes.** Passer de Liquid Glass avancé à un autre thème, puis revenir, ou enchaîner les préréglages, ne laisse aucune couche périmée à l'écran ni en mémoire (tâche 3).
3. **Valeurs extrêmes.** Une case 1 × 1 avec un rayon de 49, une profondeur de biseau de 100, la forme Dôme et un flou de 1 : aucun NaN, aucun pixel hors de la case. Une pastille de moins de 4 px de haut est ignorée (tâches 2 et 4).
4. **Un `layout.json` abîmé.** Une valeur hors bornes, `NaN`, un préréglage inconnu ou un champ `Glass` d'un mauvais type : le reste de l'apparence est gardé, le verre retombe sur Clair ou sur la valeur bornée, avec un avertissement (tâche 1).
5. **Un module en erreur ou hors ligne dans ce thème.**
   - Le panneau d'erreur se dessine avec le verre, sans exception.
   - Un module de la communauté hors ligne garde les pastilles de sa dernière image, voilées comme elle (tâches 3 et 5).

---

## Partie A — Le moteur du verre

### Tâche 1 : les réglages du verre et le thème (Sonnet)

**Fichiers :**
- Créer :
  - `src/Trofeo.Core/Rendering/GlassSettings.cs` (contient `GlassBevel`, `GlassSettings`, `GlassParameter`) ;
  - `src/Trofeo.Core/Rendering/GlassPresets.cs` ;
  - `src/Trofeo.Core/Rendering/GlassAppearance.cs`.
- Modifier :
  - `src/Trofeo.Core/Rendering/ThemeDefinition.cs` (`PanelKind.LiquidGlass`, `PanelLook.Glass`) ;
  - `Themes.cs`, `AppearanceSettings.cs`, `AppearanceResolver.cs` ;
  - `src/Trofeo.Core/Layout/LayoutJson.cs` ;
  - `src/Trofeo.ModuleKit/Theme.cs` (le drapeau interne) et `Trofeo.ModuleKit.csproj` (InternalsVisibleTo) ;
  - `src/Trofeo.Core/Rendering/PanelPainter.cs` : en attendant la tâche 3, `PanelKind.LiquidGlass` se dessine comme `Glass`, avec le look du thème Liquid Glass ;
  - `tools/Trofeo.ReadmeHero/Program.cs` : l'image d'accueil garde ses trois thèmes et ignore ceux qui n'ont pas de nom dans sa table ;
  - `tools/Trofeo.ThemePreview/Program.cs` : le texte d'aide cite le nouveau thème.
- Tests (nouveaux) : `tests/Trofeo.Core.Tests/GlassSettingsTests.cs`, `GlassAppearanceTests.cs`.

**Interfaces :**
- Produit (espace `Trofeo.Core.Rendering`) :
  - `public enum GlassBevel { Pill, Dome }`.
  - `public sealed record GlassSettings(...)`, positionnel, avec dans cet ordre et ces valeurs par défaut (celles de liquidglass, sauf le rayon) :
    - `float Blur = 0f`, `float Refraction = 0.69f`, `float ZRadius = 40f`, `GlassBevel Bevel = GlassBevel.Pill`, `float CornerRadius = 30f`, `float Distortion = 0f` ;
    - `float EdgeHighlight = 0.05f`, `float Specular = 0f`, `float Fresnel = 1f`, `float ChromaticAberration = 0.05f` ;
    - `float Opacity = 1f`, `float Saturation = 0f`, `float Tint = 0f`, `float Brightness = 0f` ;
    - `float ShadowOpacity = 0.3f`, `float ShadowSpread = 10f`, `float ShadowOffsetY = 1f`.

    Il expose aussi :
    - `GlassSettings Clamped(GlassSettings fallback)` : borne chaque valeur. Une valeur `NaN` ou infinie prend celle de `fallback`. Un `Bevel` hors de l'enum devient `Pill`.
    - `static IReadOnlyList<GlassParameter> Parameters`.
  - `public sealed record GlassParameter(string Name, float Min, float Max, float Step)`. `Name` est le nom de la propriété C#. Ses bornes :

    | Propriété | Min | Max | Pas |
    |---|---|---|---|
    | Blur | 0 | 1 | 0,01 |
    | Refraction | 0 | 2 | 0,01 |
    | ZRadius | 1 | 100 | 1 |
    | CornerRadius | 0 | 49 | 1 |
    | Distortion | 0 | 1 | 0,01 |
    | EdgeHighlight | 0 | 1 | 0,01 |
    | Specular | 0 | 1 | 0,01 |
    | Fresnel | 0 | 2 | 0,01 |
    | ChromaticAberration | 0 | 0,3 | 0,005 |
    | Opacity | 0 | 1 | 0,01 |
    | Saturation | −1 | 1 | 0,01 |
    | Tint | 0 | 1 | 0,01 |
    | Brightness | −0,5 | 0,5 | 0,01 |
    | ShadowOpacity | 0 | 1 | 0,01 |
    | ShadowSpread | 0 | 40 | 1 |
    | ShadowOffsetY | −20 | 20 | 1 |

    `Bevel` n'est pas dans la liste : c'est un choix, pas un curseur. Les bornes viennent des curseurs de la démo. Deux exceptions :
    - `CornerRadius` (0 à 49) : la moitié du plus petit côté d'une case 1 × 1 ;
    - `Tint` et `ShadowOffsetY` : la démo n'a pas de curseur pour eux.
  - `public static class GlassPresets` :
    - les constantes `Clair = "clair"`, `Givre = "givre"`, `Prisme = "prisme"`, `Discret = "discret"`, `Custom = "personnalise"` ;
    - `IReadOnlyList<string> Ids` (les quatre, dans cet ordre) ;
    - `GlassSettings? Find(string? id)` (casse ignorée ; `null` pour `Custom` ou un identifiant inconnu).

    Les valeurs (les autres gardent leur défaut) :

    | Préréglage | Valeurs |
    |---|---|
    | Clair | Blur 0,1 ; Refraction 0,69 ; ChromaticAberration 0,05 ; EdgeHighlight 0,3 ; Specular 0,4 ; Fresnel 1 ; Saturation 0,2 ; Brightness 0,05 ; ShadowOpacity 0,25 |
    | Givré | Blur 0,6 ; Refraction 0,4 ; ChromaticAberration 0,02 ; EdgeHighlight 0,25 ; Specular 0,25 ; Fresnel 0,8 ; Saturation 0,3 ; Tint 0,1 ; Brightness 0,08 |
    | Prisme | Blur 0,15 ; Refraction 0,9 ; ChromaticAberration 0,25 ; EdgeHighlight 0,5 ; Specular 0,8 ; Fresnel 1,2 |
    | Discret | Blur 0,3 ; Refraction 0,3 ; ChromaticAberration 0 ; EdgeHighlight 0,1 ; Specular 0,1 ; Fresnel 0,5 |

    Décision du plan : les franges de Prisme passent de 0,35 (conception) à **0,25**, car la démo borne ce réglage à 0,3.
  - `public sealed record GlassAppearance(string Preset = GlassPresets.Clair, GlassSettings? Values = null)`, avec `GlassSettings Resolve(Action<string>? warn = null)` :
    - un préréglage connu donne ses valeurs, et `Values` est ignoré ;
    - `Custom` donne `Values.Clamped(clair)`, ou les valeurs de Clair si `Values` est nul ;
    - un préréglage inconnu donne les valeurs de Clair, et `warn("Préréglage du verre « x » inconnu : Clair utilisé.")`.
  - `AppearanceSettings(..., bool AnimateViewSwitch = true, GlassAppearance? Glass = null)` : le nouveau paramètre vient **en dernier**.
  - `PanelKind { Matte, Glass, Pixel, LiquidGlass }` (ajouté à la fin), et sur `PanelLook` : `public GlassSettings? Glass { get; init; }`.
  - `Themes.LiquidGlassAdvanced` (`ThemeDefinition`). `Themes.All` vaut `[Classique, LiquidGlass, Pixel, LiquidGlassAdvanced]` : le nouveau thème est ajouté à la fin.
    - Il reprend la palette, la police, `FeedTypeface`, `Shape = Rounded`, `Highlight`, `Surface`, `Track`, les écarts du damier et le fond Aurore de Liquid Glass.
    - `PanelOpacity` vaut 0,22 et `CornerRadius` 30.
    - `PanelLook(PanelKind.LiquidGlass) { Glass = GlassPresets.Find("clair") }`.
    - Le `Theme` porte `ServiceDrawsSelection = true`.
  - Dans `Trofeo.ModuleKit.Theme` : `internal bool ServiceDrawsSelection { get; init; }` (faux par défaut). Il est visible de `Trofeo.Core`, `Trofeo.Extensions`, `Trofeo.ModuleHost`, `AshboardVision.Sdk.Testing`, `Trofeo.Modules.Agents` et de leurs projets de tests (InternalsVisibleTo). Il n'entre pas dans l'API publique.
  - `AppearanceResolver.Resolve` : quand `definition.Panel.Kind == PanelKind.LiquidGlass`, il pose `glass = (settings.Glass ?? new()).Resolve(warn)`, puis `Panel = Panel with { Glass = glass }` et `Theme = Theme with { CornerRadius = glass.CornerRadius }`.
  - `LayoutJson` : le champ `Glass` de `Appearance` est lu à part, avec indulgence, comme `AnimateViewSwitch`. S'il est illisible, il vaut `null` avec l'avertissement `"Réglages du verre illisibles : préréglage Clair utilisé."`, et le reste de l'apparence est gardé. À l'écriture, `GlassBevel` est écrit en texte (`"Pill"`, `"Dome"`), comme les autres énumérations.

- [ ] **Étape 1 : tests** dans `GlassSettingsTests.cs` :
  - `Defaults_AreLiquidGlassDefaults_ExceptCornerRadius` : les 17 valeurs par défaut, dont `CornerRadius == 30`.
  - `Parameters_HaveTheDemoBounds` : les 16 lignes du tableau, avec `Name` égal à `nameof(GlassSettings.X)`.
  - `Clamped_OutOfBounds_IsBounded` : par exemple `CornerRadius = 80` donne 49, et `Saturation = -3` donne −1.
  - `Clamped_NaN_TakesTheFallback`, et `Clamped_UnknownBevel_IsPill` (`(GlassBevel)7`).
  - `Presets_HaveTheirValues` : un test par préréglage, avec les valeurs du tableau, toutes dans les bornes.
- [ ] **Étape 2 : tests** dans `GlassAppearanceTests.cs` :
  - `Resolve_KnownPreset_IgnoresValues`.
  - `Resolve_Custom_UsesClampedValues`, et `Resolve_CustomWithoutValues_IsClair`.
  - `Resolve_UnknownPreset_IsClairAndWarns`.
  - `Resolver_AdvancedTheme_AppliesGlassAndCornerRadius` : `CornerRadius` personnalisé à 20 donne `Theme.CornerRadius == 20`, et `Theme.ServiceDrawsSelection` est vrai.
  - `Resolver_OtherThemes_IgnoreGlass` : un `Glass` posé sur `liquid-glass` ne change rien, et `ServiceDrawsSelection` est faux.
  - `Json_RoundTrip_KeepsPresetAndValues`.
  - **Point de vigilance 4** : `Json_GlassOfWrongType_KeepsRestOfAppearanceAndWarns` (`"Glass": 12`), `Json_GlassValueNaN_IsClampedOnResolve`, `Json_OldFileWithoutGlass_IsClair`.
  - `Themes_All_HasFourThemes_AdvancedLast`, et `Find("LIQUID-GLASS-AVANCE")` trouve le thème.
- [ ] **Étape 3 : lancer** `dotnet test tests/Trofeo.Core.Tests --filter "FullyQualifiedName~Glass"`. Attendu : échec de compilation.
- [ ] **Étape 4 : implémenter** les interfaces ci-dessus.
- [ ] **Étape 5 : lancer** `dotnet test tests/Trofeo.Core.Tests`, puis `dotnet run --project tools/Trofeo.ThemePreview -- --check`. Attendu : tout vert, et `OK`, nouveau thème compris (il se dessine pour l'instant comme Liquid Glass). Lancer aussi `dotnet build` sur toute la solution : les outils qui parcourent `Themes.All` construisent.
- [ ] **Étape 6 : commit** `feat(theme): réglages et préréglages du verre, thème Liquid Glass avancé`.

### Tâche 2 : l'effet de verre en SkSL (Fable 5.2)

**Fichiers :**
- Créer :
  - `src/Trofeo.Core/Rendering/LiquidGlass/liquid-glass.sksl` : le verre et l'ombre, en ressource embarquée `Trofeo.Core.Rendering.LiquidGlass.liquid-glass.sksl`. Un seul fichier avec un uniforme de mode, ou deux programmes : au choix.
  - `src/Trofeo.Core/Rendering/LiquidGlass/LICENSE-liquidglass.txt` : le texte MIT complet, avec `Copyright (c) ybouane`.
  - `src/Trofeo.Core/Rendering/LiquidGlass/LiquidGlassEffect.cs`.
- Modifier : `src/Trofeo.Core/Trofeo.Core.csproj` (ressource embarquée, et `Content` de la licence copiée dans `Rendering/LiquidGlass/`, comme `OFL-*.txt`).
- Tests : `tests/Trofeo.Core.Tests/LiquidGlassEffectTests.cs` (nouveau).

**Interfaces :**
- Consomme : `GlassSettings`, `GlassBevel` (tâche 1).
- Produit, dans `Trofeo.Core.Rendering` :
  - `internal sealed class LiquidGlassEffect : IDisposable` (les deux programmes sont compilés une fois, à la construction ; une erreur de compilation lève `InvalidOperationException` avec le message de Skia).
  - `public static float BlurSigma(float blur) => 10.35f * blur;`. C'est le σ en pixels de l'écran équivalent aux 6 passes séparables du flou à 9 points de l'original, au pas `2,5 × blur`. La valeur se vérifie à la tâche 9, face à la démo.
  - `SKImage RenderGlass(SKImage sharp, SKImage blurred, SKPoint imageOrigin, SKRect shape, GlassSettings s)` :
    - `sharp` et `blurred` sont la même scène, nette et floutée, de même taille. `imageOrigin` est la position, à l'écran, de leur coin haut gauche.
    - `shape` est le rectangle de verre en coordonnées d'écran.
    - Le résultat a la taille de `shape`, arrondie au pixel supérieur. Il est en alpha prémultiplié, et son alpha vaut `masque × s.Opacity`.
    - Le rayon utilisé est `min(s.CornerRadius, moitié du plus petit côté de shape)`.
  - `SKImage? RenderShadow(SKRect shape, GlassSettings s, out SKRect area)` :
    - Il rend `null` si `s.ShadowOpacity == 0`.
    - Sinon, une image noire translucide couvrant `area = shape` élargi de `ShadowMargin(s)` (et décalé de `ShadowOffsetY`), vide à l'intérieur du rectangle arrondi.
  - `public static float ShadowMargin(GlassSettings s)` : de quoi contenir toute l'ombre visible (au moins `3 × max(spread, 1) + |offsetY| + 2`).
- Adaptation de `FS_GLASS` :
  - **Toutes les étapes sont reprises, avec les mêmes formules et les mêmes constantes** : `rrSDF` ; `bevelHeight` ; le gradient sur ±2 px ; `ior = 1,5` ; les facteurs 30 et 4 (biconvexe) et 0,35 (dôme) ; `hash` et la micro-déformation ; les franges `u_chroma × 18 × (edge × 0,7 + 0,3) × 2` ; `edgeMix = 1 − edge × 0,15` ; la luminosité, la saturation (poids 0,299 / 0,587 / 0,114) et la teinte `(0,92 ; 0,95 ; 1,05)` ; `×(1 + 0,06 × depth)` ; le Fresnel puissance 4 ; les quatre lumières (exposants 90, 50, 6, 120 ; poids 1, 0,3, 0,1, 0,6) ; le trait intérieur de 1,5 px avec `topBias` ; `rim` 0,22 ; la lueur 0,15 ; le reflet d'environnement 0,08 ; le mélange final `fres × 0,2` vers le blanc ; le masque `1 − smoothstep(−1,5 ; 0,5 ; sdf)`. Et l'ombre avec `outerShadow` 0,65 et `contactShadow` 0,35.
  - **Coordonnées.** L'original travaille en UV WebGL, avec l'origine en bas (`pxToUV = (1, −1) / u_res`). SkSL travaille en pixels, avec l'origine en haut. Les déplacements se calculent directement en pixels, et le sens de chaque axe est vérifié par les tests 2 à 4 ci-dessous.
  - **L'échantillonnage.** `sharp` et `blurred` deviennent des enfants `uniform shader`, construits par `SKImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linéaire)` avec une matrice locale `Translate(imageOrigin)`.

- [ ] **Étape 1 : tests**, dans `LiquidGlassEffectTests.cs`, sur des images de synthèse (1920 × 462, rayures verticales noir et blanc de 8 px pour la réfraction ; uni pour les couleurs). Case 200 × 120 en (300, 100), avec `GlassSettings` réduits à l'essentiel par `with` :
  1. `Compiles` : construire l'effet ne lève rien.
  2. `NoEffect_CenterPixel_IsTheBackground` : Refraction, ChromaticAberration, EdgeHighlight, Specular, Fresnel, Distortion, Brightness, Saturation et Tint à 0, Blur à 0. Le pixel du centre égale le fond à ±2 près, après le `×(1 + 0,06 × depth)` attendu, soit ×1,06 au centre.
  3. `Refraction_BendsNearEdges_NotAtCenter` : sur les rayures, avec Refraction 0,69, les colonnes à 6 px du bord gauche diffèrent de l'image sans réfraction, et le centre reste identique.
  4. `TopStroke_IsBrighterThanBottom` : sur un fond gris uni, avec EdgeHighlight 1, le trait intérieur en haut est plus clair qu'en bas (le `topBias` est à l'endroit).
  5. `Chroma_SplitsChannelsAtEdges` : sur les rayures, avec ChromaticAberration 0,3, près du bord, `R ≠ B` pour au moins un pixel. Avec 0, `R == B` partout sur un fond gris.
  6. `Mask_IsTransparentOutsideAndAntialiased` : coin extérieur alpha 0, centre alpha 255 × Opacity, alpha intermédiaire sur la courbe d'un angle.
  7. `Dome_DiffersFromPill`.
  8. **Point de vigilance 3** : `ExtremeValues_NoNaN_NoPixelOutside`. Une case de 108 × 99 avec CornerRadius 49, ZRadius 100, Dôme, Blur 1, Refraction 2 et ChromaticAberration 0,3 : aucun pixel `NaN` (aucun pixel 0/0/0/0 à l'intérieur du masque sur un fond blanc), et l'image a exactement la taille de la case.
  9. `Shadow_NullWhenOpacityZero`. `Shadow_IsOutsideOnly_AndOffsetDown` : alpha nul dans le rectangle arrondi, ombre plus forte sous le bas que sur le haut avec ShadowOffsetY 10.
  10. `BlurSigma_IsLinear` : `BlurSigma(1) == 10.35f`.
- [ ] **Étape 2 : lancer** `dotnet test tests/Trofeo.Core.Tests --filter FullyQualifiedName~LiquidGlassEffectTests`. Attendu : échec de compilation.
- [ ] **Étape 3 : écrire le SkSL et `LiquidGlassEffect`.** L'en-tête du `.sksl` porte la mention MIT, le dépôt et le commit de référence, et la liste de ce qui a changé (coordonnées en pixels, échantillonneurs SkSL).
- [ ] **Étape 4 : lancer les tests.** Attendu : tout vert. Vérifier aussi que `bin/Debug/.../Rendering/LiquidGlass/LICENSE-liquidglass.txt` existe après `dotnet build src/Trofeo.Display`.
- [ ] **Étape 5 : commit** `feat(verre): l'effet de liquidglass adapté en SkSL (MIT)`.

### Tâche 3 : les couches de verre, la garde et la composition (Fable 5.2)

**Fichiers :**
- Créer : `src/Trofeo.Core/Rendering/LiquidGlass/LiquidGlassLayers.cs`.
- Modifier :
  - `src/Trofeo.Core/Rendering/FrameComposer.cs` ;
  - `PanelPainter.cs` (retirer le dessin d'attente de la tâche 1 ; ajouter la teinte pour `LiquidGlass`) ;
  - `Backdrop.cs` si la copie floutée doit suivre σ (voir l'étape 3).
- Tests : `tests/Trofeo.Core.Tests/LiquidGlassLayersTests.cs` (nouveau), `FrameComposerTests.cs`.

**Interfaces :**
- Consomme : `LiquidGlassEffect` (tâche 2), `GlassSettings`, `Themes.LiquidGlassAdvanced` (tâche 1), `Backdrop`, `PanelAlphaSmoother`, `PanelPainter.LegibleAlpha` et `TargetAlpha`.
- Produit :
  - `internal sealed class LiquidGlassLayers : IDisposable`, possédée par `FrameComposer` (une par compositeur) :
    - `void BeginFrame(IBackground background, ThemeDefinition theme)` ;
    - `void PaintShadows(SKCanvas canvas, IReadOnlyList<SKRect> rects)` ;
    - `void PaintGlass(SKCanvas canvas, SKRect rect)` (la couche de verre, sans la teinte) ;
    - `float Luminance(SKRect rect)` : la zone la plus claire de la couche de verre de la case, mesurée une fois par calcul de la couche, sur une copie réduite, et gardée avec elle ;
    - `void EndFrame()` : libère les couches des rectangles que l'image n'a pas utilisés ;
    - `internal int Computations` (les calculs de couches, pour les tests).
  - `FrameComposer`, pour `PanelKind.LiquidGlass` :
    1. le fond ;
    2. `PaintShadows` pour toutes les cases de la vue ;
    3. puis, case par case : `PaintGlass`, la teinte `Theme.Panel` à l'opacité appliquée, puis le module.
  - **L'opacité appliquée** : sans garde, `Theme.PanelOpacity`. Avec la garde, `PanelPainter.TargetAlpha(theme, layers.Luminance(rect))`, lissée par `PanelAlphaSmoother` sur un fond animé. Pour ce type, le terme de reflet de `TargetAlpha` vaut 0 : la luminance mesurée sur la couche contient déjà les reflets. `PanelPainter` gagne la teinte seule pour ce type.
  - **La clé d'une couche** : l'instance du fond et sa `Version`, le rectangle, le `GlassSettings`, et `Theme.Panel`. **Pas l'opacité appliquée** : la teinte se pose par-dessus.
  - **Le flou.** La copie floutée suit `LiquidGlassEffect.BlurSigma(s.Blur)`. Elle peut rester au quart de la taille via `Backdrop` (σ / 4) si le test de l'étape 1.6 le permet. Sinon, elle se fait à pleine taille, gardée avec la même clé que le fond.

- [ ] **Étape 1 : tests**, dans `LiquidGlassLayersTests.cs` et `FrameComposerTests.cs`, avec `Themes.LiquidGlassAdvanced`, un `ColorBackground` ou une image fixe, et des modules factices :
  1. `FixedBackground_ComputesOnce` : 10 compositions de la même vue donnent `Computations == nombre de cases`.
  2. `NewBackgroundVersion_Recomputes` : un fond animé factice qui avance de `Version` recalcule chaque case à chaque version, et seulement les cases de la vue composée.
  3. `SettingsChange_Recomputes`, puis `ThemeSwitchAndBack_LeavesNoStaleLayer` (**point de vigilance 2**). Composer en avancé, puis en Liquid Glass, puis en avancé avec un autre préréglage : les pixels de la dernière image égalent ceux d'un compositeur neuf, et `EndFrame` a libéré les couches inutilisées (compteur interne de couches vivantes, `internal int LiveLayers`).
  4. `Shadows_AreUnderNeighbourGlass` : deux cases voisines, avec ShadowSpread 40 et ShadowOpacity 1. Dans l'écart, l'ombre est visible. Dans le verre de la case voisine (Opacity 1), le pixel égale celui obtenu sans ombre.
  5. `Legibility_PrismeOnWhite_KeepsTextReadable` : un fond blanc, Prisme, la garde activée. La luminance moyenne du panneau, une fois teinté, sous la zone du texte, est d'au plus `PanelPainter.MaxLuminance`. Avec la garde coupée, elle peut dépasser.
  6. `QuarterBlur_MatchesFullBlur` : décide de la copie au quart. Si l'écart moyen avec un flou plein est d'au plus 2 niveaux sur 255, pour Blur 0,1 et 0,6, garder le quart ; sinon passer au plein. Le test reste, dans la forme retenue.
  7. **Point de vigilance 1** : `AnimatedBackground_TintIsSmoothed_NotCached`. Un fond animé qui alterne noir et blanc : l'opacité de la teinte suit `PanelAlphaSmoother` (elle ne saute pas d'une image à l'autre), et `Computations` n'augmente qu'avec `Version`.
  8. **Point de vigilance 5** : `ErrorPanel_InAdvancedTheme_DrawsGlassAndMessage`. Un module qui lève dans `Render` donne le message d'erreur sur la case, sans exception.
  9. `OtherThemes_AreUnchanged` : les images de Classique, Liquid Glass et Pixel sont identiques avant et après la tâche, par comparaison avec des images de référence générées au début de la tâche, avant toute modification, dans un dossier de test.
- [ ] **Étape 2 : lancer** `dotnet test tests/Trofeo.Core.Tests --filter "FullyQualifiedName~LiquidGlassLayersTests|FullyQualifiedName~FrameComposerTests"`. Attendu : échec.
- [ ] **Étape 3 : implémenter** `LiquidGlassLayers` et le chemin de `FrameComposer`.
- [ ] **Étape 4 : mesurer.** Ajouter `LiquidGlassLayersBench` : un test marqué `[Trait("Category", "Bench")]` qui imprime le temps de calcul des couches de la vue par défaut (6 cases) sur Aurore, avec Clair et Givré, et le temps d'une composition une fois les couches en mémoire. Le lancer en Debug et noter les chiffres dans le rapport de tâche.
  - **Si le calcul d'une vue entière dépasse 50 ms**, ajouter le calcul en arrière-plan, dans la même tâche :
    - `FrameComposer(RateLimitedErrorLog? errors = null, bool backgroundGlass = false)`. Avec `backgroundGlass`, une couche manquante ou périmée se calcule sur un fil à part.
    - Pendant le calcul, la case montre la couche précédente de ce rectangle, ou à défaut le verre simple de `Themes.LiquidGlass` (le `PanelPainter` de `Glass`).
    - Un événement `event Action? GlassReady` prévient quand une couche est prête.
    - Le service (`src/Trofeo.Service/DisplayLoop.cs`) crée ses deux compositeurs avec `backgroundGlass: true`, et relie `GlassReady` à `_scheduler.MarkDirty()`.
    - Un fond animé calcule toujours sans attendre : chaque image est déjà nouvelle.
    - Test : `BackgroundMode_ShowsFallbackThenLayer`.
  - **Sinon**, tout reste synchrone. Noter la décision dans le rapport.
- [ ] **Étape 5 : lancer** `dotnet test tests/Trofeo.Core.Tests` et `dotnet test tests/Trofeo.Service.Tests`, puis `dotnet run --project tools/Trofeo.ThemePreview -- --check`. Attendu : tout vert, et `OK`.
- [ ] **Étape 6 : commit** `feat(verre): couches de verre gardées en mémoire, ombres et garde de lisibilité`.

### Tâche 4 : la pastille de l'élément choisi (Fable 5.2)

**Fichiers :**
- Créer : `src/Trofeo.ModuleKit/ISelectionSink.cs`.
- Modifier :
  - `src/Trofeo.ModuleKit/ModuleRenderContext.cs` et `ModuleDrawing.cs` ;
  - `src/Trofeo.Core/Rendering/LiquidGlass/LiquidGlassLayers.cs` (`PaintPill`) et `FrameComposer.cs` ;
  - `src/Trofeo.Modules.Agents/AgentPainter.cs` (`DrawTab`).
- Tests :
  - `tests/Trofeo.Core.Tests/SelectionTests.cs` (nouveau) ;
  - `tests/Trofeo.Modules.Agents.Tests` : un test d'identité de l'onglet dans les trois autres thèmes.

**Interfaces :**
- Consomme : `LiquidGlassLayers`, `LiquidGlassEffect.RenderGlass` (tâches 2 et 3), `Theme.ServiceDrawsSelection` (tâche 1).
- Produit :
  - `internal interface ISelectionSink { void Add(SKRect caseLocalRect); }`, dans `Trofeo.ModuleKit`.
  - Sur `ModuleRenderContext` : `internal ISelectionSink? Selections { get; init; }`. Le constructeur positionnel ne change pas, et ce n'est pas un ajout à l'API publique.
  - `public static void DrawSelection(SKCanvas canvas, SKRect rect, ModuleRenderContext ctx)` dans `ModuleDrawing`, avec sa documentation XML en français. `rect` est en coordonnées de la toile, comme tout dessin du module.
    - **Si `ctx.Theme.ServiceDrawsSelection` est faux**, elle dessine exactement l'onglet d'aujourd'hui :
      - `DrawRect(rect)` en `Theme.Surface`, avec un `SKPaint` sans anticrénelage, comme `AgentPainter.Fill` ;
      - puis le trait à gauche, de largeur `(Shape == Pixel ? 4 : 3) × ctx.TextScale`, en `Highlight` (Pixel) ou `Accent`.
    - **Sinon, si `ctx.Selections` existe**, elle appelle `Add(rect décalé de −ctx.Bounds.Location)` et ne dessine rien.
    - **Sinon** (approximation locale, sans service), elle dessine une pilule `rect` de rayon `h / 2` : fond `Theme.Text` à 12 %, trait intérieur de 1,5 px `Theme.Text` à 45 % en haut et à 15 % en bas.
  - `LiquidGlassLayers.PaintPill(SKCanvas canvas, SKRect caseRect, SKRect pill)` :
    - Il utilise le même programme, avec `s with { CornerRadius = h / 2, ZRadius = min(s.ZRadius, h / 2), ShadowOpacity = 0 }`.
    - Ce que la pastille déforme : l'image de la case déjà peinte (le fond, la couche de verre et la teinte), sans le contenu du module.
    - La pastille est coupée à la case, gardée en mémoire par `(clé de la case, pill)`, et ignorée si `pill.Height < 4` ou `pill.Width < 4`.
  - **Dans `FrameComposer`, quand `theme.Theme.ServiceDrawsSelection`**, pour chaque case :
    1. enregistrer `module.Render` dans un `SKPictureRecorder`, avec `ctx with { Selections = liste }` ;
    2. dessiner chaque pastille, avec `liste` ramenée en coordonnées d'écran et coupée à la case ;
    3. rejouer l'enregistrement, découpé à la forme du panneau.

    Sans ce drapeau, le chemin d'aujourd'hui ne change pas : pas d'enregistrement.
  - `AgentPainter.DrawTab` appelle `ModuleDrawing.DrawSelection(_canvas, rect, ctx)` à la place de ses deux `Fill`. `ctx` est gardé par le peintre.

- [ ] **Étape 1 : tests**, dans `SelectionTests.cs` :
  1. `OtherThemes_DrawSelection_EqualsOldTab` : pour Classique, Liquid Glass et Pixel, et pour `TextScale` 1 et 1,25, le rendu de `DrawSelection` égale pixel à pixel les deux `Fill` d'avant, réécrits dans le test.
  2. `AdvancedTheme_RecordsCaseLocalRect_DrawsNothing` : la toile reste vide, et le puits reçoit le rectangle décalé.
  3. `AdvancedTheme_NoSink_DrawsLocalPill` : des pixels non transparents dans `rect`, et aucun en dehors.
  4. `Composer_PillIsUnderEverythingTheModuleDraws` : un module factice dessine un rectangle rouge, appelle `DrawSelection` sur une zone qui le recouvre, puis dessine du texte. Le rouge et le texte sont visibles par-dessus la pastille : les pixels rouges sont intacts.
  5. `Composer_PillIsClippedToCase_AndTinyPillIgnored` (**point de vigilance 3**).
  6. `Composer_PillsAreCached` : 5 compositions, un seul calcul de pastille.

  Dans `tests/Trofeo.Modules.Agents.Tests`, `Tabs_OtherThemes_Unchanged` compare le rendu du module dans les trois autres thèmes à des images de référence générées avant la modification.
- [ ] **Étape 2 : lancer** `dotnet test tests/Trofeo.Core.Tests --filter FullyQualifiedName~SelectionTests`. Attendu : échec.
- [ ] **Étape 3 : implémenter.**
- [ ] **Étape 4 : lancer** `dotnet test tests/Trofeo.Core.Tests`, `dotnet test tests/Trofeo.Modules.Agents.Tests` et `dotnet test tests/AshboardVision.Sdk.Tests --filter FullyQualifiedName~PublicApi`. Attendu : tout vert. Le test d'API publique passe (un ajout non abstrait ne touche pas à l'instantané). L'instantané sera régénéré à la tâche 6.
- [ ] **Étape 5 : commit** `feat(verre): pastille de l'élément choisi, dessinée par le service`.

## Partie B — Les modules de la communauté et le SDK

### Tâche 5 : les pastilles des modules de la communauté (Sonnet)

**Fichiers :**
- Modifier :
  - `src/Trofeo.Extensions/Protocol/HostMessages.cs` (`Frame`) ;
  - `src/Trofeo.ModuleHost/InstanceRenderer.cs` ;
  - `src/Trofeo.Extensions/Service/RemoteModule.cs` ;
  - `src/Trofeo.ModuleKit/Trofeo.ModuleKit.csproj` (InternalsVisibleTo `Trofeo.Extensions` et `Trofeo.ModuleHost`, si la tâche 1 ne l'a pas fait).
- Tests : `tests/Trofeo.Extensions.Tests/ProtocolTests.cs`, `RemoteModuleTests.cs` (ou le fichier existant de `RemoteModule`), `tests/Trofeo.ModuleHost.Tests`.

**Interfaces :**
- Consomme : `ISelectionSink`, `ModuleRenderContext.Selections`, `DrawSelection` (tâche 4).
- Produit :
  - `public sealed record SelectionRect(float X, float Y, float Width, float Height)` dans `Trofeo.Extensions.Protocol`.
  - `Frame(Guid InstanceId, int Buffer, bool Animating, IReadOnlyList<SelectionRect>? Selections = null)` : un ajout en dernier, et un ancien message sans le champ se lit.
  - `InstanceRenderer.Draw` passe un `ISelectionSink` qui collecte au plus `MaxSelections = 16` rectangles, puis les envoie avec l'image dans `Frame`.
  - `RemoteModule` garde les rectangles avec l'image qu'ils accompagnent (`_front`).
    - À la réception, il **valide** : il ignore les `NaN`, les infinis, et les largeurs ou hauteurs ≤ 0 ; il coupe chaque rectangle à `[0, largeur] × [0, hauteur]` de l'image ; il ne garde que les 16 premiers.
    - Dans `Render`, il appelle `ctx.Selections?.Add(r)` pour chacun, **avant** de poser l'image.
    - Hors ligne, les rectangles de la dernière image sont transmis comme l'image elle-même (**point de vigilance 5**).

- [ ] **Étape 1 : tests :**
  - `Frame_RoundTrip_WithAndWithoutSelections` (protocole).
  - `RemoteModule_ForwardsValidatedSelections` : envoie `NaN`, un rectangle négatif, un rectangle qui déborde et 20 rectangles ; seuls les rectangles valides, coupés, et 16 au plus, arrivent au puits.
  - `RemoteModule_Offline_StillForwardsLastSelections`.
  - `InstanceRenderer_SendsSelectionsWithFrame`, côté hôte, avec le module d'essai qui appelle `DrawSelection`.
  - **Le test d'égalité** `NativeAndCommunity_SameDrawing_SamePixels` (dans `Trofeo.Core.Tests` ou `Trofeo.Extensions.Tests`, là où les deux chemins sont accessibles). Le même dessin (un trait avant, `DrawSelection`, du texte après) est rendu deux fois dans le thème avancé :
    - une fois comme module natif ;
    - une fois comme image et rectangles reçus d'un hôte, par un `RemoteModule` nourri à la main.

    Les deux compositions sont égales pixel à pixel, ou à ±1 près dû au rééchantillonnage, à justifier dans le rapport s'il existe.
- [ ] **Étape 2 : lancer** les tests ciblés. Attendu : échec.
- [ ] **Étape 3 : implémenter.**
- [ ] **Étape 4 : lancer** `dotnet test tests/Trofeo.Extensions.Tests`, `dotnet test tests/Trofeo.ModuleHost.Tests` et `dotnet test tests/Trofeo.Core.Tests`. Attendu : tout vert.
- [ ] **Étape 5 : commit** `feat(modules): les pastilles des modules complémentaires voyagent avec leur image`.

### Tâche 6 : le SDK 1.2.0 (Sonnet)

**Fichiers :** le même ensemble qu'au passage du SDK en 1.1.0 (commit `7374615`, à relire avec `git show --stat 7374615`) :
- les `<Version>` du SDK, de `Sdk.Testing`, de `Sdk.Tool` et des modèles, plus le modèle `MonModule` ;
- les tests de paquets ;
- `src/Trofeo.Extensions/Manifest/Compatibility.cs` (`Current = new(1, 2)`) ;
- `tools/AshboardVision.Sdk.Tool/CheckCommand.cs` (`ThemeIds`) ;
- `src/AshboardVision.Sdk.Testing/ModuleTestHost.cs` ;
- `tests/AshboardVision.Sdk.Tests/api-1.0.txt` ;
- `docs/sdk/dessin.md`, `compatibilite.md`, `contrat.md`, et les README des paquets.

**Interfaces :**
- Consomme : `DrawSelection` (tâche 4), `LiquidGlassLayers` et `FrameComposer` (tâche 3).
- Produit :
  - **La version.** Les paquets passent en `1.2.0`, et `AssemblyVersion` reste `1.0.0.0`. Le manifeste accepte `"sdk": "1.2"`.
  - **`ModuleTestHost.Render(GridSize size, string theme)`** accepte `liquid-glass-avance`, et son message d'erreur cite les quatre thèmes. Dans ce thème, l'image rendue montre la **vraie pastille** :
    - le module est enregistré avec un puits ;
    - la pastille est dessinée par `LiquidGlassLayers.PaintPill`, sur la région (0, 0) du fond par défaut du thème (Aurore) avec la couche de verre de la case ;
    - le dessin du module est ensuite rejoué par-dessus ;
    - l'image reste transparente hors des pastilles et du dessin du module, comme aujourd'hui.

    Ces aides de Core restent internes : `AshboardVision.Sdk.Testing` est ajouté aux InternalsVisibleTo de `Trofeo.Core`.
  - **`ashboard-module check`** produit une image PNG par taille pour `liquid-glass-avance` aussi.
  - **L'instantané `api-1.0.txt`** est régénéré : seule la ligne de `DrawSelection` est ajoutée.
  - **La documentation :**
    - `dessin.md` : « quatre thèmes », la ligne `DrawSelection` dans le tableau des aides, et une section courte « L'élément choisi ». Elle explique qu'il faut appeler l'aide pour un onglet ou une ligne choisis, et que la pastille passe sous tout le dessin du module, quel que soit le moment de l'appel. Son extrait de code vient d'un fichier vérifié par `DocsTests`, par exemple un module d'essai du SDK qui appelle `DrawSelection`.
    - `compatibilite.md` : la ligne 1.2, et `"sdk": "1.2"` exigé pour appeler `DrawSelection`.
    - `contrat.md` : l'exemple du champ `sdk`.

- [ ] **Étape 1 : tests :**
  - `ModuleTestHost_AdvancedTheme_ShowsPill` : un module d'essai qui appelle `DrawSelection` donne des pixels de verre dans le rectangle, différents de l'approximation locale.
  - `Check_ProducesAdvancedThemeImages`.
  - `Compatibility_Current_Is_1_2`.
  - Les tests de paquets attendent `1.2.0`.
- [ ] **Étape 2 : lancer** `dotnet test tests/AshboardVision.Sdk.Tests` et `dotnet test tests/Trofeo.Extensions.Tests`. Attendu : échec.
- [ ] **Étape 3 : implémenter, régénérer l'instantané** (`ASHBOARD_UPDATE_API_SNAPSHOT=1`, puis relire le diff : uniquement des ajouts) **et écrire la documentation.**
- [ ] **Étape 4 : lancer** les mêmes tests, puis `dotnet pack -c Debug` sur les quatre paquets du SDK, comme le font les tests de paquets. Attendu : tout vert.
- [ ] **Étape 5 : commit** `feat(sdk): SDK 1.2.0, DrawSelection et le quatrième thème`.

## Partie C — La fenêtre, les aperçus et la version

### Tâche 7 : la section Verre de l'onglet Apparence (Sonnet)

**Fichiers :**
- Modifier :
  - `src/Trofeo.Settings/ViewModels/AppearanceTabViewModel.cs` ;
  - `src/Trofeo.Settings/Views/AppearanceTabView.axaml` et son `.cs` ;
  - `src/Trofeo.Settings/Resources/Strings.resx` et `src/Trofeo.Settings/Texts.cs` ;
  - `src/Trofeo.Settings/Assets/Themes/` : la vignette du nouveau thème, faite par `dotnet run --project tools/Trofeo.ThemePreview -- --thumbnails src/Trofeo.Settings/Assets/Themes` (vérifier que seules les vignettes attendues changent) ;
  - `tools/Trofeo.SettingsPreview/Program.cs`.
- Créer : `src/Trofeo.Settings/ViewModels/GlassSectionViewModel.cs` et `GlassSliderItem.cs`.
- Tests : `tests/Trofeo.Settings.Tests/AppearanceGlassTests.cs` (nouveau).

**Interfaces :**
- Consomme : `GlassSettings`, `GlassParameter`, `GlassPresets`, `GlassAppearance`, `AppearanceSettings.Glass` (tâche 1).
- Produit :
  - **Une quatrième carte de thème** `ThemeCard("liquid-glass-avance", Texts.AppearanceThemeLiquidGlassAdvanced, Texts.AppearanceThemeLiquidGlassAdvancedText, …)`, placée après Liquid Glass. Son texte prévient de la consommation.
  - **`AppearanceTabViewModel.Glass`** (`GlassSectionViewModel`), visible seulement si le thème est `liquid-glass-avance` (`ShowsGlass`). Il contient :
    - `IReadOnlyList<GlassPresetItem> Presets` (quatre boutons, `IsSelected`) ;
    - `bool IsCustom` (l'indication « Personnalisé ») ;
    - `bool ShowsDetails` (le repli « Réglages fins », fermé par défaut) ;
    - quatre groupes `IReadOnlyList<GlassSliderItem>` (Verre, Lumière, Couleur, Ombre) ;
    - `bool IsDome`, pour la forme du biseau.
  - **`GlassSliderItem`** : `Label`, `Min`, `Max`, `Step` (pris dans `GlassSettings.Parameters`), `Value` et `ValueText`. Comme le curseur d'opacité, bouger le curseur ne sauve rien : `CommitAsync()` au lâcher écrit `Glass = new GlassAppearance(GlassPresets.Custom, valeurs)` par `EditSession` (une seule annulation partagée).
  - **Choisir un préréglage** écrit `new GlassAppearance(id)`.
  - **Les textes** (`Strings.resx`, en tutoyant) :

    | Clé | Texte |
    |---|---|
    | `AppearanceThemeLiquidGlassAdvanced` | « Liquid Glass avancé » |
    | `AppearanceThemeLiquidGlassAdvancedText` | « Le verre d'iOS : le fond se déforme sur les bords, avec reflets et franges de couleur. Il demande plus au processeur, surtout avec un fond animé ou une vidéo. » |
    | `AppearanceGlass` | « Verre » |
    | `AppearanceGlassCustom` | « Personnalisé » |
    | `AppearanceGlassDetails` | « Réglages fins » |
    | `AppearanceGlassPresetClair` / `…Givre` / `…Prisme` / `…Discret` | « Clair », « Givré », « Prisme », « Discret » |
    | Groupes | « Verre », « Lumière », « Couleur », « Ombre » |
    | Curseurs | « Flou », « Réfraction », « Profondeur du biseau », « Rayon des angles », « Micro-déformation », « Lumière des bords », « Reflets », « Fresnel », « Franges de couleur », « Opacité du verre », « Saturation », « Teinte froide », « Luminosité », « Opacité de l'ombre », « Étendue de l'ombre », « Décalage de l'ombre » |
    | Biseau | « Forme du biseau », « Pilule », « Dôme » |

  - **L'accessibilité** : chaque curseur et chaque bouton a un `AutomationProperties.Name`, et tout est atteignable au clavier.
  - **Les aperçus de la fenêtre** : `apercus-reglages/apparence-verre.png` (section ouverte, Clair) et `apparence-verre-personnalise.png`.

- [ ] **Étape 1 : tests** dans `AppearanceGlassTests.cs` :
  - `GlassSection_HiddenForOtherThemes_ShownForAdvanced`.
  - `ChoosePreset_WritesPresetOnly`.
  - `MoveSlider_DoesNotSaveUntilCommit_ThenWritesCustom`.
  - `Undo_RestoresPreviousGlass`.
  - `Sliders_HaveParameterBounds` : 16 curseurs, avec les bornes de `GlassSettings.Parameters`.
  - `CustomState_ShowsAfterSlider_ClearedByPreset`.
  - `Bevel_ToggleWritesCustomDome`.
  - Une vérification de fenêtre (`Named<...>`), comme dans les tests d'onglets existants : la section existe, le repli s'ouvre, et la carte du nouveau thème est là.
- [ ] **Étape 2 : lancer** `dotnet test tests/Trofeo.Settings.Tests --filter FullyQualifiedName~AppearanceGlassTests`. Attendu : échec.
- [ ] **Étape 3 : implémenter**, dans le style des autres sections d'Apparence (`section`, `muted`, curseurs comme `OpacitySlider`).
- [ ] **Étape 4 : lancer** `dotnet test tests/Trofeo.Settings.Tests`, puis `dotnet run --project tools/Trofeo.SettingsPreview -- --out apercus-reglages`, et regarder les deux nouvelles images. Attendu : tout vert, et une section lisible.
- [ ] **Étape 5 : commit** `feat(reglages): section Verre de l'onglet Apparence`.

### Tâche 8 : aperçus, licence, notes et numéro de version (Sonnet)

**Fichiers :**
- Modifier :
  - `tools/Trofeo.ThemePreview/Program.cs` ;
  - `README.md` ;
  - `CHANGELOG.md` ;
  - `Directory.Build.props` ;
  - `docs/utilisation-avancee.md`, si `--appearance` y est décrit : le nouvel identifiant de thème.

**Interfaces :**
- Produit :
  - **`Trofeo.ThemePreview --glass <dossier>`** écrit `avance-<préréglage>-<fond>.png`, pour les quatre préréglages et trois fonds :
    - `aurore` ;
    - `clair` : une image claire fabriquée, par exemple un dégradé blanc vers jaune pâle avec des formes ;
    - `sombre` : une image sombre fabriquée, contrastée.

    On y voit les cinq vrais modules, dont Claude Code avec son onglet choisi. Il écrit aussi `avance-clair-rayures.png`, sur un fond à rayures, pour juger la réfraction.
  - **`README.md`** : le thème dans la liste des thèmes, et une section « Licences tierces » qui cite liquidglass (MIT, lien), à côté des polices OFL si elles y sont déjà citées.
  - **`CHANGELOG.md`** : une section `## 1.2.0-beta.2`, au-dessus de `## 1.2.0-beta.1`. Elle couvre le nouveau thème, ses préréglages et ses réglages, la pastille de l'onglet choisi, le SDK 1.2.0 avec `DrawSelection`, et la mention de liquidglass sous MIT. Ton et forme : ceux de la section beta.1.
  - **`Directory.Build.props`** : `<Version>1.2.0-beta.2</Version>`.

- [ ] **Étape 1 : écrire** l'option `--glass`, et la lancer : `dotnet run --project tools/Trofeo.ThemePreview -- --glass apercus-verre`. Attendu : 13 images, sans erreur.
- [ ] **Étape 2 : écrire** le README, le CHANGELOG et le numéro de version.
- [ ] **Étape 3 : lancer** `dotnet test tests/Trofeo.Distribution.Tests` (lecture du CHANGELOG et des versions) et `dotnet test tests/AshboardVision.Sdk.Tests --filter FullyQualifiedName~Docs`. Attendu : tout vert.
- [ ] **Étape 4 : commit** `docs: notes de la 1.2.0-beta.2, aperçus du verre et licence de liquidglass`.

### Tâche 9 (PC d'Alex, session principale) : comparaison, essais, mesures, bêta, bilan

- [ ] **Comparer avec la démo, avant de montrer quoi que ce soit à Alex.**
  - Ouvrir la démo dans le navigateur intégré, et y régler les valeurs de Clair, puis de Prisme. Rendre la même chose avec `--glass` sur un fond comparable (au besoin, ajouter à `ThemePreview` une option `--glass-background <image>` pour reprendre l'image de la démo, téléchargée dans le bloc-notes, jamais ajoutée au dépôt).
  - Comparer côte à côte la forme de la déformation, les franges, les reflets, les bords, l'ombre et le flou (`BlurSigma`).
  - Corriger les écarts dans une tâche de correction sur Fable 5.2, avec tests, puis recommencer.
- [ ] **Faire relire toute la branche** sur Fable 5.2 (relecture finale), puis corriger.
- [ ] **Montrer les aperçus `apercus-verre` à Alex.** Ajuster les préréglages selon ses retours, en corrigeant aussi le tableau de la tâche 1 et ses tests.
- [ ] **L'installation.** Alex construit l'installateur (`pwsh -NoExit -File installer\build.ps1 -Version 1.2.0-beta.2`) et l'installe. Essais sur l'écran :
  - choisir le thème, chaque préréglage, quelques réglages fins ;
  - le glissement entre deux vues ;
  - l'onglet de Claude Code ;
  - un module de la communauté (Démo) ;
  - un fond clair, un GIF et une vidéo.
- [ ] **Les mesures** (comme au plan 4) : le processeur et la mémoire du service en Clair, sur Aurore, une image, un GIF 1920 × 462, une vidéo 1920 × 462 et une vidéo 1080p ; le temps du glissement (« Passage de vue ») ; le coût de l'aperçu des vues cachées dans Disposition. Si la vidéo coûte trop aux yeux d'Alex, réduire la résolution des couches sur un fond animé, avec tests.
- [ ] **La publication, avec l'accord d'Alex à chaque étape :** fusion de `plan-9-verre` dans `Canary`, poussée de `Canary`, puis étiquette annotée `v1.2.0-beta.2`. Vérifier la recette GitHub et les fichiers publiés.
- [ ] **Le bilan** `docs/superpowers/plans/2026-11-xx-plan-9-bilan.md`, les lignes du plan 9 dans `CLAUDE.md` et la mémoire.
