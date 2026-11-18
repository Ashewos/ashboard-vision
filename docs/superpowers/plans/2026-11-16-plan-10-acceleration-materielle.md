# Plan 10 — Accélération matérielle : plan de construction

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**But :** le dessin de l'image (les quatre thèmes, le verre, le glissement) et le décodage des vidéos passent sur la carte graphique par Direct3D 12, avec le choix de la carte, deux cases (« Dessin de l'image », « Décodage des vidéos ») et un repli automatique sur le processeur. La version visée est la préversion 1.2.0-beta.3.

**Architecture :**
- **Core reste sans Direct3D.** Il ne connaît qu'une interface, `IGpuDevice`, qui fabrique des surfaces Skia sur la carte et dit si la carte est perdue. `FrameComposer` reçoit (ou non) un `IGpuDevice` à sa construction, dessine sur une surface de la carte, et rapatrie l'image finie dans le `SKBitmap` qu'il rend déjà. Sans appareil, tout reste comme aujourd'hui, au pixel près.
- **Direct3D 12 vit dans `Trofeo.Display`** (`Gpu/`) : énumération des cartes par DXGI, appareil, file de commandes et `GRContext` de Skia (`GRContext.CreateDirect3D`). Le décodage vidéo (`Video/`) ouvre son propre appareil Direct3D 11 sur la même carte et partage ses images avec Direct3D 12 par une texture partagée.
- **Le service décide** : un `AccelerationController` (dans `Trofeo.Service`) choisit la carte, garde l'appareil, se replie sur le processeur en cas de panne, retente, et donne l'état à la fenêtre.

**Technologies :** .NET 10, C#, SkiaSharp 3.119.4 (`GRContext.CreateDirect3D`, `GRD3DBackendContext`, `GRBackendTexture` + `GRD3DTextureResourceInfo`), Vortice 3.8.3 (Direct3D12, DXGI, Direct3D11, MediaFoundation), Avalonia (fenêtre), xUnit.

**Conception :** `docs/superpowers/specs/2026-11-16-plan-10-acceleration-materielle-design.md`, validée par Alex le 16/11/2026. Elle prime sur ce plan.

**Essai de référence** (jetable, hors du dépôt) : `C:\Users\alexl\AppData\Local\Temp\claude\985ca34fcd702133\a2370863-bf2e-4ed0-9058-1a5b7a57ddc0\scratchpad\essai-gpu\essai.cs`. Il montre la création de l'appareil (`EnumAdapterByGpuPreference`, `D3D12CreateDevice` au niveau 11_0, `CreateCommandQueue(Direct)`, `GRD3DBackendContext { Adapter, Device, Queue }`), une surface de la carte (`SKSurface.Create(gr, false, info)`), le verre dessiné par `DrawRect` avec une matrice locale, et le retour des pixels (`ReadPixels`). Le scratchpad peut avoir été vidé : ces lignes suffisent.

## Contraintes globales

- **Projet.**
  - Les règles de `CLAUDE.md` s'appliquent.
  - Debug seulement (`dotnet build`, `dotnet test`). Jamais `-c Release`, jamais `installer/build.ps1`.
  - Le travail se fait sur la branche `plan-10-gpu`, partie de `Canary` (`67f6b2d`). Rien n'est poussé.
- **Commits.**
  - Adresse : `1345997+Ashewos@users.noreply.github.com`.
  - Messages en français, avec les préfixes `feat(…)`, `fix(…)`, `test` et `docs`.
  - Ils se terminent par `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` (ou la ligne du modèle qui a fait le travail).
  - Les fichiers sont ajoutés un par un. Jamais `docs/guide-personnalisation.md`, `.claude/launch.json`, `apercus-accueil-b/`, `apercus-*` ni `.superpowers/`.
- **Modèles.** Implémenteurs et relecteurs sur Sonnet, effort élevé. Relecture finale de la branche sur Opus (Fable n'est plus disponible).
- **Textes et code.** Les textes affichés sont en français et tutoient. Les noms de code sont en anglais, les commentaires en français.
- **Le service et la fenêtre d'Alex** ne sont jamais arrêtés ni lancés.
- **Valeurs fixées.**
  - Version du logiciel : **1.2.0-beta.3**. Protocole du canal de contrôle : **5**.
  - Direct3D 12 au niveau de fonctionnalités **11_0** ; cartes logicielles (`AdapterFlags.Software`) ignorées hors des tests.
  - Automatique : la première carte matérielle de `EnumAdapterByGpuPreference(i, GpuPreference.MinimumPower)`.
  - Carte trop lente : rendu sur la carte de plus de **83 ms** en moyenne pendant **10 s**, en Automatique seulement.
  - Nouvel essai après une panne : toutes les **5 minutes**, au plus **3** fois de suite.
  - Limite du cache de textures de Skia : **48 Mo** (`SetResourceCacheLimit`), à revoir à la tâche 9.
  - Comparaison carte / processeur : **99,9 %** des pixels à **4/255** au plus par couleur, écart moyen sous **0,5/255**. Vidéo : écart moyen sous **1/255**, 99 % des pixels à **6/255** au plus.
  - Vidéo : plafond de **12** images par seconde (`MaxCropsPerSecond`) inchangé.
- **Sans appareil, rien ne change.** Avec `IGpuDevice` absent (outils, tests, SDK, machine sans carte, accélération coupée), Classique, Liquid Glass, Pixel et Liquid Glass avancé restent identiques **au pixel près** à aujourd'hui (empreintes SHA-256 de `OtherThemesUnchangedTests`).
- **Les outils restent sur le processeur** : `Trofeo.ThemePreview`, `Trofeo.SettingsPreview`, `ModuleTestHost` du SDK et `ashboard-module check` ne reçoivent jamais d'appareil.
- **Un seul fil par `GRContext`.** Toute la composition (écran, aperçus des vues cachées, glissement) se fait déjà sur le fil de la boucle d'affichage : l'appareil de dessin n'est utilisé que là. En mode carte, le verre avancé se calcule sur ce fil (sans le fil de calcul à part).
- **Tests sans carte.** La recette GitHub n'a pas de carte graphique : les tests Direct3D 12 tournent sur WARP (`D3D12GpuDevice.CreateWarp()`), et sont sautés avec un message clair si Direct3D 12 n'existe pas. Les tests qui exigent une vraie carte (décodage vidéo matériel) sont sautés sans carte matérielle.
- **Licences.** Les paquets Vortice ajoutés (Direct3D12, DXGI, Direct3D11 ; MIT) sont cités dans `installer/LICENCES-TIERCES.txt`, comme Vortice.MediaFoundation.

## Points de vigilance

Voici les cinq cas que la conception implique sans qu'une tâche les teste naturellement. Chacun a son test dans la tâche indiquée.

1. **Pilote mis à jour ou planté en cours de route.** L'image en cours sort quand même, par le processeur ; l'écran ne reste jamais noir ni figé ; l'appareil, ses textures et les couches de verre sont jetés (tâches 3 et 5).
2. **Une machine sans carte Direct3D 12** (machine virtuelle, vieux PC, pilote de base). Le service démarre, dessine sur le processeur, et la fenêtre grise l'interrupteur avec « Aucune carte graphique compatible » (tâches 5 et 7).
3. **Des réglages changés vite et souvent** : couper et rallumer vingt fois, changer de carte pendant qu'une vidéo joue. Pas de texture qui reste (le cache de Skia revient à son niveau), pas d'image noire, la vidéo reprend à sa position (tâches 5 et 6).
4. **Une carte enregistrée qui n'existe plus** (RTX retirée, `layout.json` copié d'une autre machine). Automatique est pris, avec un avertissement au journal et dans la fenêtre (tâche 5).
5. **Les modules complémentaires sur la carte** : leur image, leurs pastilles et le voile d'un module hors ligne sortent comme sur le processeur (tâche 4).

---

### Task 1: les réglages `Acceleration`

**Fichiers :**
- Créer : `src/Trofeo.Core/Layout/AccelerationSettings.cs`
- Modifier : `src/Trofeo.Core/Layout/DashboardLayout.cs`, `src/Trofeo.Core/Layout/LayoutJson.cs`, `src/Trofeo.Core/Layout/LayoutTransfer.cs`, `src/Trofeo.Settings/ViewModels/SettingsTabViewModel.cs` (import, vers la ligne 429), `src/Trofeo.Settings/Services/EditSession.cs` (comparaison des dispositions, vers la ligne 276)
- Tests : `tests/Trofeo.Core.Tests/AccelerationSettingsTests.cs`, `tests/Trofeo.Settings.Tests/` (import)

**Interfaces :**
- Produit :
  - `public sealed record GpuAdapterId(int VendorId, int DeviceId, int SubSysId, string Name)`.
  - `public sealed record AccelerationSettings(bool Enabled = true, GpuAdapterId? Adapter = null, bool Drawing = true, bool VideoDecoding = true)` avec `public static AccelerationSettings Default { get; }`. `Adapter` null : Automatique.
  - `DashboardLayout` gagne le paramètre facultatif `AccelerationSettings? Acceleration = null`, le dernier.

- [ ] **Étape 1 : écrire les tests qui échouent**, dans `AccelerationSettingsTests` :
  - `Missing_GivesNull` : un `layout.json` sans `Acceleration` se lit avec `Acceleration == null`, sans avertissement ; `layout.Acceleration ?? AccelerationSettings.Default` vaut `(true, null, true, true)`.
  - `RoundTrip_KeepsEveryField` : `new AccelerationSettings(false, new GpuAdapterId(0x10DE, 0x2F04, 0x12345678, "NVIDIA GeForce RTX 5070"), true, false)` s'écrit puis se relit égal.
  - `Unreadable_FieldFallsBackWithWarning` : `"Enabled": "oui"`, `"Adapter": 3`, `"VideoDecoding": null` donnent chacun la valeur par défaut du champ, avec un avertissement par champ ; le reste de la disposition est gardé.
  - `Export_LeavesAccelerationOut` : `LayoutTransfer.Export` d'une disposition avec `Acceleration` ne contient pas la chaîne `"Acceleration"`.
  - `Import_DropsAcceleration` : un fichier partagé qui contient `Acceleration` s'importe avec `Acceleration == null`.
  - Côté fenêtre : importer une disposition garde l'`Acceleration` de la disposition en cours (test dans `Trofeo.Settings.Tests`, à côté des tests d'import existants).
- [ ] **Étape 2 :** lancer `dotnet test tests/Trofeo.Core.Tests --filter AccelerationSettings` ; attendu : échec de compilation (`AccelerationSettings` inconnu).
- [ ] **Étape 3 : écrire le code.** Lecture tolérante champ par champ dans `LayoutJson`, comme `AnimateViewSwitch` (avertissement en français : « Réglage d'accélération illisible (<champ>) : valeur par défaut utilisée. »). `LayoutTransfer.Export` sérialise `layout with { Acceleration = null }` ; `Import` rend `Acceleration = null`. L'import de la fenêtre remet `Acceleration` de la disposition courante sur le résultat. `EditSession` compare aussi `Acceleration` là où il compare `MaxFps`.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Core.Tests` et `dotnet test tests/Trofeo.Settings.Tests` ; attendu : tout passe.
- [ ] **Étape 5 : commit** `feat(disposition): réglages d'accélération matérielle, propres à la machine`.

### Task 2: les cartes et l'appareil Direct3D 12

**Fichiers :**
- Créer : `src/Trofeo.Core/Rendering/IGpuDevice.cs` (interface et `GpuLostException`), `src/Trofeo.Display/Gpu/GpuAdapters.cs`, `src/Trofeo.Display/Gpu/D3D12GpuDevice.cs`, `src/Trofeo.Service/GpuAdapterInfo.cs`
- Modifier : `src/Trofeo.Display/Trofeo.Display.csproj` (Vortice.Direct3D12 et Vortice.DXGI 3.8.3), `installer/LICENCES-TIERCES.txt`
- Tests : `tests/Trofeo.Display.Tests/D3D12GpuDeviceTests.cs`, `tests/Trofeo.Display.Tests/GpuTestSupport.cs`, `tests/Trofeo.TestDoubles/ImageDiff.cs`

**Interfaces :**
- Produit (Core, `Trofeo.Core.Rendering`) :
  ```csharp
  public interface IGpuDevice : IDisposable
  {
      string AdapterName { get; }
      SKSurface CreateSurface(SKImageInfo info);  // surface de la carte ; lève GpuLostException si la carte est perdue
      void Submit();                              // envoie le travail en attente à la carte, sans attendre
      bool IsLost { get; }                        // carte retirée, réinitialisée, ou contexte de Skia abandonné
      string? LostReason { get; }                 // en français, pour le journal et la fenêtre
      int CacheResources { get; }                 // GetResourceCacheUsage
      long CacheBytes { get; }
      void PurgeUnused();                         // PurgeUnlockedResources(false)
  }
  public sealed class GpuLostException(string reason) : Exception(reason);
  ```
- Produit (Service, `Trofeo.Display`) : `public sealed record GpuAdapterInfo(GpuAdapterId Id, long Luid, long DedicatedMemoryBytes)`.
- Produit (Display, `Trofeo.Display.Gpu`) :
  - `public static class GpuAdapters { public static IReadOnlyList<GpuAdapterInfo> List(); }` : les cartes matérielles, dans l'ordre `MinimumPower` (la carte intégrée d'abord).
  - `public sealed class D3D12GpuDevice : IGpuDevice` avec `public static D3D12GpuDevice Create(GpuAdapterInfo adapter)` (lève `GpuLostException` si la création échoue), `public static D3D12GpuDevice? CreateWarp()` (null si Direct3D 12 n'existe pas), `public GpuAdapterInfo Adapter { get; }`, `public ID3D12Device Device { get; }`, `public ID3D12CommandQueue Queue { get; }`, `public GRContext Context { get; }`. `IsLost` lit `Device.DeviceRemovedReason` et `Context.IsAbandoned`. Cache limité à 48 Mo à la création.
- Produit (tests) : `ImageDiff.Compare(SKBitmap expected, SKBitmap actual)` rend `(double Mean, double ShareWithin4, int Max)` (écarts par couleur, sur 255) ; `GpuTestSupport.Warp()` rend un `D3D12GpuDevice` WARP ou saute le test (`Assert.Skip("Direct3D 12 absent")`).

- [ ] **Étape 1 : écrire les tests qui échouent** (`D3D12GpuDeviceTests`, sur WARP) :
  - `Warp_DrawsAndReadsBack` : surface 64 × 32, `Clear(SKColors.Red)`, `ReadPixels` donne du rouge partout.
  - `Warp_RunsTheGlassProgram` : le programme `glass` de `liquid-glass.sksl` compile et se dessine sur la carte sans exception (le programme est lu comme le fait `LiquidGlassEffect`).
  - `Dispose_FreesTheCache` : après `Dispose`, rien ne lève, et un second `Dispose` ne fait rien.
  - `List_HasNoSoftwareAdapter` : `GpuAdapters.List()` ne contient aucune carte dont le nom est « Microsoft Basic Render Driver ».
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Display.Tests --filter D3D12GpuDevice` ; attendu : échec de compilation.
- [ ] **Étape 3 : écrire le code.** Suivre l'essai de référence. `AdapterName` est `Description1.Description`. `Luid` vient de la description (il ne sert qu'à apparier les appareils Direct3D 11 et 12 pendant une même session, jamais à l'enregistrement). Licences ajoutées.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Display.Tests` ; attendu : tout passe (ou sauté avec « Direct3D 12 absent »).
- [ ] **Étape 5 : commit** `feat(gpu): cartes graphiques et appareil Direct3D 12 pour Skia`.

### Task 3: la composition sur la carte

**Fichiers :**
- Modifier : `src/Trofeo.Core/Rendering/FrameComposer.cs`, `src/Trofeo.Core/Rendering/Backdrop.cs`, `src/Trofeo.Core/Rendering/PanelPainter.cs` (vers la ligne 242), `src/Trofeo.Core/Rendering/ViewTransition.cs`
- Tests : `tests/Trofeo.Core.Tests/FrameComposerGpuFallbackTests.cs`, `tests/Trofeo.Display.Tests/GpuCompositionTests.cs`, `tests/Trofeo.TestDoubles/FakeGpuDevice.cs`

**Interfaces :**
- Consomme : `IGpuDevice`, `GpuLostException` (tâche 2).
- Produit :
  - `FrameComposer(RateLimitedErrorLog? errors = null, bool backgroundGlass = false, IGpuDevice? gpu = null)`.
  - `public bool UsesGpu { get; }` : vrai tant que l'appareil donné marche.
  - `public event Action<string>? GpuFailed` : levé une fois, avec la raison, quand le compositeur abandonne la carte.
  - `ViewTransition.Compose(SKBitmap to, TimeSpan elapsed, IGpuDevice? gpu = null)` (même résultat, dessiné sur la carte si `gpu` est donné).
  - `FakeGpuDevice : IGpuDevice` (tests) : surfaces du processeur ; `LoseAfter(int surfaces)` et `FailCreate` simulent une carte perdue ou une création refusée.

- [ ] **Étape 1 : écrire les tests qui échouent.**
  - `FrameComposerGpuFallbackTests` (Core, aussi sous Linux) :
    - `LostMidFrame_StillReturnsTheCpuFrame` : avec `FakeGpuDevice` qui se perd à la deuxième surface, `Compose` rend une image égale au pixel près à celle d'un compositeur sans appareil, lève `GpuFailed` une fois, et `UsesGpu` devient faux.
    - `AfterFailure_StaysOnCpu` : les images suivantes ne demandent plus de surface à l'appareil.
    - `NoDevice_IsUnchanged` : sans appareil, les empreintes de `OtherThemesUnchangedTests` restent les mêmes (ce test existant doit continuer de passer sans modification).
  - `GpuCompositionTests` (Display, WARP), avec la disposition par défaut et ses modules (texte compris) :
    - `Classique_MatchesCpu`, `LiquidGlass_MatchesCpu`, `Pixel_MatchesCpu` : `ImageDiff` entre le compositeur sur WARP et le compositeur sans appareil : `ShareWithin4 >= 0.999` et `Mean < 0.5`.
    - `AnimatedBackground_MatchesCpu` : la même chose avec Aurore et avec un GIF à son image 3.
    - `Glide_MatchesCpu` : `ViewTransition.Compose` au milieu du glissement (150 ms), même tolérance.
    - `Cache_ReturnsToItsLevel` : 50 images, puis 50 autres : `CacheResources` ne grandit plus entre les deux séries.
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Core.Tests --filter FrameComposerGpu` et `dotnet test tests/Trofeo.Display.Tests --filter GpuComposition` ; attendu : échec de compilation.
- [ ] **Étape 3 : écrire le code.** Une surface de la carte 1920 × 462 gardée d'une image à l'autre. `Compose` y dessine tout ce qu'il dessine aujourd'hui, puis `ReadPixels` dans un `SKBitmap` neuf (même format que maintenant), rendu à l'appelant. Les surfaces de travail (`Backdrop`, flou du verre simple, glissement) viennent de `gpu.CreateSurface` quand le compositeur est sur la carte. Toute `GpuLostException`, ou `gpu.IsLost` vrai après `Submit`, abandonne la carte : l'image est refaite entièrement sur le processeur, `GpuFailed` est levé, et le compositeur ne touche plus à l'appareil (il ne le libère pas : il appartient au service). Liquid Glass avancé n'est pas traité ici (tâche 4) : en attendant, un compositeur sur la carte compose les images de ce thème entièrement sur le processeur, comme sans appareil, sans lever `GpuFailed`.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Core.Tests` et `dotnet test tests/Trofeo.Display.Tests` ; attendu : tout passe.
- [ ] **Étape 5 : commit** `feat(rendu): composition de l'image sur la carte graphique, repli sur le processeur`.

### Task 4: le verre avancé et les pastilles sur la carte

**Fichiers :**
- Modifier : `src/Trofeo.Core/Rendering/LiquidGlass/LiquidGlassEffect.cs`, `src/Trofeo.Core/Rendering/LiquidGlass/LiquidGlassLayers.cs`, `src/Trofeo.Core/Rendering/FrameComposer.cs` (`RenderOverSelections`)
- Tests : `tests/Trofeo.Display.Tests/GpuGlassTests.cs`, `tests/Trofeo.Core.Tests/` (tests existants du verre, inchangés)

**Interfaces :**
- Consomme : `IGpuDevice` (tâche 2), compositeur sur la carte (tâche 3).
- Produit :
  - `LiquidGlassEffect(IGpuDevice? gpu = null)` : avec un appareil, `Render` dessine sur une surface de la carte, en une fois (pas de bandes), et rend une image de la carte.
  - `LiquidGlassLayers(bool inBackground = false, Action<string>? report = null, IGpuDevice? gpu = null)` : avec un appareil, tout est calculé sur place (pas de fil à part, `inBackground` ignoré), les couches sont des images de la carte, le flou se fait sur la carte.
  - `PaintPill(SKCanvas canvas, SKSurface frame, SKRect caseRect, SKRect pill)` : la variante carte. Elle prend un instantané de la case (`frame.Snapshot(SKRectI)`) au premier appel de l'image pour cette case, et recalcule ses pastilles à chaque image, sans comparer de pixels ni garder de pastille d'une image à l'autre. La variante `SKPixmap` reste celle du processeur.

- [ ] **Étape 1 : écrire les tests qui échouent** (`GpuGlassTests`, WARP) :
  - `Advanced_EachPreset_MatchesCpu` : Liquid Glass avancé, préréglages Clair, Givré, Prisme et Discret, sur Aurore, disposition par défaut : `ShareWithin4 >= 0.999` et `Mean < 0.5` face au compositeur sans appareil (synchrone, `backgroundGlass: false`).
  - `Advanced_AnimatedBackground_EveryFrameMatchesCpu` : 5 images d'un GIF, même tolérance à chaque image.
  - `Pills_MatchCpu` : Claude Code avec son onglet choisi ; même tolérance sur la case.
  - `CommunityModule_PillsAndOfflineVeil_MatchCpu` : un module complémentaire en ligne avec deux sélections, puis hors ligne (voile 90/255) ; même tolérance (point de vigilance 5).
  - `PillBudget_StillApplies` : 20 pastilles de 3 000 px dans une case : au-delà du budget de 40 000 px, les pastilles simples sont dessinées, comme sur le processeur.
  - `Layers_NoTextureLeftAfterThemeChange` : Liquid Glass avancé puis Classique : après la première image de Classique et `PurgeUnused`, `CacheResources` revient au niveau d'un compositeur Classique neuf.
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Display.Tests --filter GpuGlass` ; attendu : échec (variante de `PaintPill` absente).
- [ ] **Étape 3 : écrire le code.** Dans `RenderOverSelections`, le compositeur sur la carte passe sa surface au lieu du `SKPixmap`. La luminance de la garde de lisibilité se mesure, comme aujourd'hui, sur une copie réduite au quart, rapatriée par `ReadPixels` (seule lecture autorisée en cours d'image, et seulement quand une couche est recalculée). Les compteurs `Computations`, `PillComputations` gardent leur sens.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Core.Tests` et `dotnet test tests/Trofeo.Display.Tests` ; attendu : tout passe, empreintes comprises.
- [ ] **Étape 5 : commit** `feat(verre): verre avancé et pastilles calculés sur la carte graphique`.

### Task 5: le contrôleur d'accélération, branché dans le service

**Fichiers :**
- Créer : `src/Trofeo.Service/AccelerationController.cs`, `src/Trofeo.Service/IGpuDeviceFactory.cs`, `src/Trofeo.Service/AccelerationStatus.cs`, `src/Trofeo.Display/Gpu/D3D12GpuDeviceFactory.cs`
- Modifier : `src/Trofeo.Service/DisplayLoop.cs` (constructeur, `ApplyNow`, `NewComposer`, `Tick`, `Status`), `src/Trofeo.Service/LoopStatus.cs`, `src/Trofeo.Display/Program.cs`
- Tests : `tests/Trofeo.Service.Tests/AccelerationControllerTests.cs`, `tests/Trofeo.Service.Tests/DisplayLoopAccelerationTests.cs`

**Interfaces :**
- Consomme : `AccelerationSettings`, `GpuAdapterId` (tâche 1) ; `IGpuDevice`, `GpuAdapterInfo`, `D3D12GpuDevice`, `GpuAdapters` (tâche 2) ; `FrameComposer(…, gpu)`, `GpuFailed`, `UsesGpu` (tâche 3).
- Produit :
  - `public interface IGpuDeviceFactory { IReadOnlyList<GpuAdapterInfo> ListAdapters(); IGpuDevice Create(GpuAdapterInfo adapter); }` (`Create` lève en cas d'échec).
  - `public sealed record AccelerationStatus(bool Available, bool Enabled, GpuAdapterInfo? Adapter, bool Drawing, bool VideoDecoding, string? Problem, IReadOnlyList<GpuAdapterInfo> Adapters)` : `Adapter` est la carte réellement utilisée ; `Drawing` et `VideoDecoding` disent ce qui y passe vraiment ; `Problem` est la phrase à montrer (null sans problème).
  - `public sealed class AccelerationController : IDisposable` :
    - `AccelerationController(IGpuDeviceFactory? factory, TimeProvider time, Action<string> log)` (factory null : jamais de carte) ;
    - `void Apply(AccelerationSettings? settings)` ;
    - `IGpuDevice? DrawingDevice { get; }` ;
    - `GpuAdapterInfo? VideoAdapter { get; }` (null : vidéos décodées par le processeur) ;
    - `void ReportFailure(string reason)` ;
    - `void ReportFrame(TimeSpan gpuRenderTime)` ;
    - `bool Tick()` (vrai si `DrawingDevice` a changé : nouvel essai, carte suivante) ;
    - `event Action? EffectiveChoiceChanged` (la carte des vidéos a changé : le service relit la disposition pour rouvrir la vidéo) ;
    - `AccelerationStatus Status { get; }`.
  - `DisplayLoop` reçoit `AccelerationController? acceleration = null` à sa construction ; `LoopStatus` gagne `AccelerationStatus? Acceleration`.
  - `GpuAdapterChoice.Resolve(AccelerationSettings settings, IReadOnlyList<GpuAdapterInfo> found, Action<string> warn) : GpuAdapterInfo?` (statique, dans `AccelerationController.cs`) : Automatique = `found[0]` ; une carte enregistrée est retrouvée par `VendorId`, `DeviceId`, `SubSysId`, puis par `Name` ; absente : Automatique, et « La carte <nom> n'est plus là : la carte automatique est utilisée. ».
  - Les phrases de `Problem` (montrées telles quelles par la fenêtre) :
    - création refusée : « La carte graphique n'a pas pu démarrer (<raison>) : dessin sur le processeur. » ;
    - carte perdue : « La carte graphique a eu un problème (<raison>) : dessin sur le processeur. » ;
    - Automatique, aucune carte assez rapide : « Aucune carte n'est assez rapide : dessin sur le processeur. » ;
    - carte choisie à la main, trop lente : « <carte> est trop lente pour tenir le rythme. » ;
    - carte enregistrée absente : la phrase de `Resolve` ci-dessus.

- [ ] **Étape 1 : écrire les tests qui échouent** (`AccelerationControllerTests`, avec une fabrique et des appareils factices, `FakeTimeProvider`) :
  - `Auto_TakesTheFirstAdapter` ; `Manual_FindsTheSavedCard` ; `SavedCardGone_FallsBackToAutoWithWarning` (point de vigilance 4).
  - `NoAdapter_StaysOnCpu` : liste vide : `Available` faux, `DrawingDevice` null, aucune exception (point de vigilance 2).
  - `CreateFails_StaysOnCpuWithProblem` : `Create` lève : `Problem` dit la raison, `DrawingDevice` null.
  - `Disabled_OrDrawingOff_HasNoDrawingDevice` ; `VideoDecodingOff_HasNoVideoAdapter`.
  - `Failure_RetriesEveryFiveMinutes_ThreeTimesAtMost` : après `ReportFailure`, `Tick` recrée l'appareil à 5 min, puis encore à 10 et 15 min si l'appareil échoue encore, et plus après le troisième essai ; un `Apply` avec d'autres réglages remet le compte à zéro.
  - `Auto_TooSlow_TriesNextCardThenCpu` : `ReportFrame(90 ms)` pendant 10 s : carte suivante, `EffectiveChoiceChanged` levé ; encore lente : processeur, avec un `Problem`.
  - `Manual_TooSlow_StaysButSaysSo` : carte choisie à la main, lente : elle est gardée, `Problem` le dit.
  - `TwentyToggles_LeaveOneDeviceAtMost` : vingt `Apply` qui coupent puis rallument : chaque appareil créé est libéré, il en reste un seul (point de vigilance 3).
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Service.Tests --filter Acceleration` ; attendu : échec de compilation.
- [ ] **Étape 3 : écrire le contrôleur**, puis **le brancher** :
  - `DisplayLoop.ApplyNow` appelle `Apply(layout.Acceleration)` ; quand `DrawingDevice` change (là, ou dans `Tick`), les compositeurs (écran et aperçus) sont recréés avec le nouvel appareil, l'ancien est libéré après eux.
  - `NewComposer` passe `DrawingDevice` et abonne `GpuFailed` à `ReportFailure`.
  - Le temps de `Compose` seul (sans le JPEG ni l'envoi) va à `ReportFrame` quand le compositeur est sur la carte.
  - `Status` donne `Acceleration`.
  - `Program` crée `AccelerationController(new D3D12GpuDeviceFactory(), TimeProvider.System, log)`, le donne à la boucle, et sur `EffectiveChoiceChanged` relit la disposition comme pour un rechargement demandé par la fenêtre.
  - `DisplayLoopAccelerationTests` : `DeviceLostMidRun_NextFrameIsSent` (l'appareil factice se perd : l'image est envoyée, par le processeur ; point de vigilance 1) et `DeviceChange_RebuildsComposers`.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Service.Tests`, puis toute la solution (`dotnet test`) ; attendu : tout passe.
- [ ] **Étape 5 : commit** `feat(service): choix de la carte, repli et nouvel essai de l'accélération matérielle`.

### Task 6: le décodage des vidéos sur la carte

**Fichiers :**
- Créer : `src/Trofeo.Display/Video/VideoDecodeOptions.cs`, `src/Trofeo.Display/Video/SharedVideoFrame.cs` (texture partagée Direct3D 11 → 12 et barrière)
- Modifier : `src/Trofeo.Display/Video/MediaFoundationBackground.cs`, `src/Trofeo.Core/Rendering/Backgrounds/IBackground.cs` (deux interfaces), `src/Trofeo.Core/Rendering/FrameComposer.cs` (`DrawBackground`), `src/Trofeo.Service/LoopConfigFactory.cs`, `src/Trofeo.Display/Program.cs`, `src/Trofeo.Display/Trofeo.Display.csproj` (Vortice.Direct3D11 3.8.3), `installer/LICENCES-TIERCES.txt`
- Tests : `tests/Trofeo.Display.Tests/GpuVideoTests.cs`, `tests/Trofeo.Service.Tests/` (`LoopConfigFactory`)

**Interfaces :**
- Consomme : `GpuAdapterInfo`, `D3D12GpuDevice` (tâche 2), `AccelerationController.VideoAdapter`, `EffectiveChoiceChanged` (tâche 5).
- Produit :
  - Core : `public interface ISeekableBackground { TimeSpan Position { get; } }` et `public interface IGpuFrameSource { SKImage? TryGetGpuFrame(IGpuDevice device); }` (null : le compositeur prend `Current`). `FrameComposer` sur la carte demande d'abord `TryGetGpuFrame`.
  - Display : `public sealed record VideoDecodeOptions(GpuAdapterInfo? Adapter, TimeSpan StartAt)` ; `MediaFoundationBackground.TryOpen(string path, Action<string>? warn, VideoDecodeOptions options)` ; `MediaFoundationBackground : ISeekableBackground, IGpuFrameSource`.
  - Service : `LoopConfigFactory.Build(DashboardLayout layout, ISystemFonts fonts, Func<string, TimeSpan, IBackground?>? video, Action<string> warn, LoopConfig? current = null, bool reopenVideo = false)`. La vidéo est rouverte (à la `Position` du fond courant) quand `reopenVideo` est vrai, ou quand `Enabled`, `Adapter` ou `VideoDecoding` de `Acceleration` ont changé ; sinon les règles de réutilisation actuelles restent.

- [ ] **Étape 1 : écrire les tests qui échouent.**
  - `GpuVideoTests` (sautés sans carte matérielle ; vidéos générées comme dans `MediaFoundationBackgroundTests`, par son `WriteVideo`, à sortir dans une aide commune ; 1920 × 462, 3 s, un dégradé coloré qui bouge) :
    - `GpuDecode_SameColoursAsCpu` : l'image à 1 s, décodée par la carte puis rapatriée, comparée à celle du décodage par le processeur : écart moyen sous 1/255, 99 % des pixels à 6/255 au plus.
    - `GpuDecode_GpuDrawing_NoCpuCopy` : avec un `D3D12GpuDevice` sur la même carte, `TryGetGpuFrame` rend une image de la carte, et `Current` n'a pas été calculé (compteur de rapatriements à 0).
    - `GpuDecode_CpuDrawing_CurrentWorks` : sans dessin sur la carte, `Current` rend l'image recadrée 1920 × 462.
    - `Reopen_ResumesAtPosition` : ouverte avec `StartAt = 1,5 s`, la première image est celle de 1,5 s (à une image près).
    - `UnsupportedOnGpu_DecodesOnCpu` : si l'activation du décodage par la carte échoue, l'ouverture réussit quand même, sur le processeur, avec une ligne au journal.
  - `LoopConfigFactory` (Service.Tests) : `AccelerationChange_ReopensVideoAtPosition` ; `ThemeChange_KeepsVideo` (inchangé) ; `ReopenFlag_ReopensVideo` (point de vigilance 3).
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Display.Tests --filter GpuVideo` ; attendu : échec de compilation.
- [ ] **Étape 3 : écrire le code.**
  - Un appareil Direct3D 11 (`D3D11CreateDevice` sur la carte dont le `Luid` est celui de `options.Adapter`, `VideoSupport`), un gestionnaire `MFCreateDXGIDeviceManager`, le lecteur ouvert avec `SourceReaderAttributeKeys.D3DManager` et le traitement vidéo avancé, sortie BGRA.
  - Pour le dessin sur la carte : chaque image recadrée est copiée sur la carte dans une texture partagée (handle NT), ouverte une fois par l'appareil Direct3D 12, synchronisée par une barrière partagée, et enveloppée par `GRBackendTexture` + `GRD3DTextureResourceInfo` puis `SKImage.FromTexture`.
  - Pour le dessin par le processeur : la même image passe par une texture de lecture et devient `Current`, seulement quand `Current` est demandé.
  - Une erreur de l'appareil Direct3D 11 en cours de lecture fait rouvrir la vidéo sur le processeur, à sa position, avec un avertissement.
  - `Program` donne à `LoopConfigFactory` un ouvreur qui prend `AccelerationController.VideoAdapter`, et passe `reopenVideo: true` sur `EffectiveChoiceChanged`. L'ancien fond reste affiché jusqu'à ce que le nouveau ait sa première image (c'est déjà le cas : `TryOpen` lit la première image avant de rendre la main).
  - Le recadrage à 12 images par seconde au plus ne change pas.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Display.Tests` et `dotnet test tests/Trofeo.Service.Tests` ; attendu : tout passe (les tests vidéo sautés sans carte matérielle).
- [ ] **Étape 5 : commit** `feat(video): décodage par la carte graphique, partagé avec le dessin`.

### Task 7: le canal de contrôle et la section de la fenêtre

**Fichiers :**
- Modifier : `src/Trofeo.Control/ControlPipe.cs` (protocole 5), `src/Trofeo.Control/Messages.cs`, `src/Trofeo.Service/ServiceControlHandler.cs`, `src/Trofeo.Settings/Views/SettingsTabView.axaml`, `src/Trofeo.Settings/ViewModels/SettingsTabViewModel.cs`, `src/Trofeo.Settings/Resources/Strings.resx`, `tools/Trofeo.SettingsPreview` (une image de la section)
- Tests : `tests/Trofeo.Control.Tests/`, `tests/Trofeo.Settings.Tests/AccelerationSectionTests.cs`

**Interfaces :**
- Consomme : `AccelerationStatus` (tâche 5), `AccelerationSettings` (tâche 1).
- Produit :
  - `public sealed record GpuAdapterEntry(int VendorId, int DeviceId, int SubSysId, string Name)`.
  - `public sealed record AccelerationInfo(bool Available, bool Enabled, string? AdapterName, bool Drawing, bool VideoDecoding, string? Problem, IReadOnlyList<GpuAdapterEntry> Adapters)`.
  - `ServiceStatus` gagne `AccelerationInfo? Acceleration` (dernier paramètre, null avec un service ancien).

- [ ] **Étape 1 : écrire les tests qui échouent.**
  - Control : `Status_CarriesAcceleration` (aller-retour sur le tube) ; `ProtocolVersion` vaut 5.
  - `AccelerationSectionTests` (modèle de vue, sans fenêtre) :
    - `Entries_AutoFirstThenEachCard` : « Automatique (Intel(R) Graphics) », « Intel(R) Graphics », « NVIDIA GeForce RTX 5070 ».
    - `SwitchOff_GraysOutTheRest` : interrupteur coupé : liste et cases désactivées.
    - `NoCard_SwitchDisabledWithMessage` : `Available` faux : interrupteur grisé, texte « Aucune carte graphique compatible : tout se calcule sur le processeur. » (point de vigilance 2).
    - `StateLine` : « Sur Intel(R) Graphics : dessin et vidéos. », « Sur Intel(R) Graphics : dessin. », « Sur Intel(R) Graphics : vidéos. », « Tout se calcule sur le processeur. ».
    - `Problem_ShownAsWarning` : `Problem` = « La carte graphique a eu un problème (pilote réinitialisé) : dessin sur le processeur. » est montré tel quel, en avertissement.
    - `Change_SavesAccelerationAndReloads` : cocher, décocher ou choisir une carte écrit `Acceleration` dans la disposition et demande au service de relire, comme le curseur des images par seconde.
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Control.Tests` et `dotnet test tests/Trofeo.Settings.Tests --filter AccelerationSection` ; attendu : échec de compilation.
- [ ] **Étape 3 : écrire le code.** La section « Accélération matérielle » vient juste après « Images par seconde », dans le style des sections voisines :
  ```
  Accélération matérielle
    Utiliser la carte graphique                          [ activé ]
    Carte        [ Automatique (Intel(R) Graphics)          ▾ ]
    ☑ Dessin de l'image (thèmes, verre, fonds animés)
    ☑ Décodage des vidéos
    Sur Intel(R) Graphics : dessin et vidéos.
  ```
  Les textes vont dans `Strings.resx` ; les phrases de problème viennent du service (tâche 5) et sont montrées telles quelles. Une image de la section est ajoutée aux aperçus de `Trofeo.SettingsPreview`, et regardée.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Control.Tests`, `dotnet test tests/Trofeo.Settings.Tests` ; attendu : tout passe.
- [ ] **Étape 5 : commit** `feat(reglages): section Accélération matérielle dans Paramètres`.

### Task 8: documentation et notes de version

**Fichiers :**
- Modifier : `CHANGELOG.md`, `docs/utilisation-avancee.md`, `README.md` (FAQ et licences tierces), `Directory.Build.props` (`<Version>1.2.0-beta.3</Version>`)

- [ ] **Étape 1 :** une section `## 1.2.0-beta.3` en tête de `CHANGELOG.md`, sur le modèle de la beta.2 :
  - l'accélération matérielle (dessin de tous les thèmes, verre compris, et décodage des vidéos sur la carte, par Direct3D 12, pour toutes les marques) ;
  - la section de Paramètres (interrupteur, carte, Automatique = carte intégrée, deux cases) ;
  - le repli sur le processeur et le nouvel essai ;
  - les réglages propres à la machine, non exportés ;
  - le coût du thème avancé sur fond animé, laissé en « à mesurer » (la tâche 9 le remplit).
- [ ] **Étape 2 :** `docs/utilisation-avancee.md` décrit le champ `Acceleration` de `layout.json` (ses quatre champs, leurs valeurs par défaut, l'identité d'une carte). Le README dit dans la FAQ que l'accélération se coupe dans Paramètres, et cite Vortice.Direct3D12, DXGI et Direct3D11 dans les licences tierces.
- [ ] **Étape 3 :** `dotnet test tests/Trofeo.Distribution.Tests` (les tests des notes de version) ; attendu : tout passe.
- [ ] **Étape 4 : commit** `docs: notes de la 1.2.0-beta.3, accélération matérielle`.

### Task 9: sur le PC d'Alex (session principale, pas de sous-agent)

- [ ] **Étape 1 : fusion.** Après la relecture finale, fusionner `plan-10-gpu` dans `Canary` (`--no-ff`) avec l'accord d'Alex. Alex construit l'installateur (`pwsh -NoExit -File installer\build.ps1 -Version 1.2.0-beta.3`) et l'installe.
- [ ] **Étape 2 : mesures**, avec l'outil du plan 9 (changer `layout.json` par `\\localhost\C$\…`, `ReloadAsync`, mesurer 30 s après 10 s, remettre le fichier d'origine), en ajoutant `Acceleration` aux scénarios et l'utilisation de la carte (compteurs « GPU Engine » du processus `Trofeo.Display`, moteurs 3D et VideoDecode) :
  - les quatre thèmes sur fond fixe ; Liquid Glass et Liquid Glass avancé sur `mesure-fond.gif`, `mesure-462.mp4` et `mesure-1080.mp4` ;
  - chaque cas : accélération coupée, Intel, RTX ;
  - processeur, images par seconde, rendu, mémoire privée, carte.
- [ ] **Étape 3 : critères** (conception, « Vérifier ») : Liquid Glass avancé sur fond animé à 2-3 % ou moins, à ≈ 10,8 images/s ; aucun thème plus cher qu'en beta.2 sur fond fixe ; mémoire ≈ +55 Mo sur l'Intel, stable pendant une heure (une mesure toutes les 5 min). Revoir la limite du cache (48 Mo) d'après les mesures.
- [ ] **Étape 4 : essais à l'écran avec Alex** : il compare à l'œil, coupe et rallume l'accélération, change de carte pendant une vidéo, coche et décoche les cases.
- [ ] **Étape 5 : version.** Compléter le coût mesuré dans `CHANGELOG.md` et, si les mesures le justifient, la carte du thème (« Verre d'iOS, plus gourmand en processeur. »). Pousser `Canary` et l'étiquette `v1.2.0-beta.3`, chacun avec l'accord d'Alex.
- [ ] **Étape 6 : bilan** `docs/superpowers/plans/AAAA-MM-JJ-plan-10-bilan.md` et ligne du plan 10 dans `CLAUDE.md`.
