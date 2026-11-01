# Plan 1 — Fondations : le service d'affichage pilote l'écran

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un service de fond Windows qui affiche un premier module (Heure + date) sur le Trofeo Vision via USB : damier 16 × 4, rendu Skia, envoi JPEG plafonné à 12 images/s, et garde-fou qui relance l'écran s'il se fige.

**Architecture:** Solution .NET 10 en plusieurs projets.
- `Trofeo.ModuleKit` : le contrat public de module.
- `Trofeo.Core` : damier, disposition, rendu et cadence. Code pur, testable sans écran.
- `Trofeo.Device` : protocole LY et WinUSB.
- `Trofeo.Modules.Clock` : le premier module.
- `Trofeo.Display` : l'exécutable de fond avec son icône de notification.

Les tests xUnit couvrent toute la logique avec un faux transport USB. Un outil console vérifie le tout sur le vrai écran.

**Tech Stack:** .NET 10 (LTS), C#, SkiaSharp, xUnit, Microsoft.Extensions.TimeProvider.Testing, WinUSB (P/Invoke `winusb.dll` / `setupapi.dll`), System.Text.Json, WinForms `NotifyIcon` (icône seulement).

**Spec:** `docs/superpowers/specs/2026-10-24-trofeo-dashboard-design.md`

## Place dans la feuille de route

Le document de conception couvre plusieurs sous-systèmes indépendants. Ils sont découpés en six plans. Chacun produit un logiciel qui marche et qui se teste seul, et chacun sera écrit quand le précédent sera livré.

| Plan | Livre | Dépend de |
|---|---|---|
| **1. Fondations** (ce plan) | Service de fond qui affiche Heure + date sur l'écran, avec le garde-fou USB | — |
| 2. Modules de données | Monitoring (+ assistant capteurs élevé et PawnIO), En écoute, Météo dans le module Heure, Réseau (Freebox / UniFi / carte du PC) | 1 |
| 3. Claude Code & Codex | Lecture des sessions, flux, barre du bas, quotas via la barre d'état, moteur d'animation, mascottes Claude et Codex | 1 |
| 4. Apparence | Thèmes Classique / Liquid Glass / Pixel, transparence, polices, taille du texte, fonds image/GIF/vidéo | 1 |
| 5. Logiciel de réglage | Fenêtre Avalonia : damier éditable, mode émulé, modules, raccourcis, paramètres, assistant de première ouverture | 1–4 |
| 6. Distribution | Installateur Inno Setup (Ashboard Vision), mises à jour GitHub en un clic avec Stable et Bêta, README soigné, releases construites par GitHub | 1–5 |

## Global Constraints

- Identité de l'appli : **`trofeo-HB-dashboard`**. C'est le nom du dépôt GitHub public, du dossier de réglages `%AppData%\trofeo-HB-dashboard\` et du mutex d'instance unique. Il ne doit jamais changer.
- .NET 10 LTS. Publication finale *self-contained* `win-x64`, sans aucun runtime à installer.
- Le service d'affichage tourne **sans droits administrateur**.
- Image envoyée : **JPEG 1920 × 462 en paysage**, qualité 88, sous-échantillonnage 4:4:4, **450 000 octets maximum**.
- USB `0416:5408`, interface WinUSB `{3876E417-E089-4BCC-BBF8-47ABA55E46FB}`, bulk OUT `0x09`, bulk IN `0x81`.
- **Une seule image en cours à la fois.** Écriture par blocs de 4096 octets avec un délai de 5000 ms, puis lecture d'une validation de 512 octets avec un délai de 1000 ms.
- **Plafond de 12 images/s.** Rien n'est envoyé si rien n'a changé.
- Damier de **16 × 4 cases**, marge extérieure de **14 px**, espace entre modules de **12 px**.
- Rotation **0° ou 180°**. Par défaut 180°, la valeur du montage d'Alex.
- Pas de chevauchement entre modules, et chaque module respecte sa taille minimum.
- Textes visibles par l'utilisateur en **français**.
- Licence **GPL-3.0**.

## Review Focus

1. **Écran débranché puis rebranché pendant que le service tourne.** Le service repasse en Prêt en moins de 3 s et renvoie la dernière image. Testé en tâche 5 (`Reconnects_AndResendsLastFrame`).
2. **Écran déjà pris par le logiciel Thermalright ou un autre programme.** Pas de plantage : l'état passe à Déconnecté avec le motif « Écran utilisé par un autre programme (fermez TRCC) », puis le service réessaie. Testé en tâche 5 (`OpenDenied_ReportsBusy`).
3. **`layout.json` corrompu ou illisible.** Le service démarre sur la disposition par défaut et met le fichier fautif de côté en `layout.json.bad`. Testé en tâche 3 (`Load_CorruptFile_FallsBackAndKeepsBackup`).
4. **Image trop lourde** (> 450 000 octets, cas d'un futur fond très détaillé). On réencode avec une qualité plus basse au lieu d'échouer. Testé en tâche 4 (`Encode_TooLarge_LowersQuality`).
5. **Un module qui plante pendant son dessin.** Seul ce module affiche un panneau d'erreur, les autres continuent et le service reste vivant. Testé en tâche 4 (`Compose_ModuleThrows_DrawsErrorPanelOnly`).

---

### Task 0: Dépôt, solution et outils

**Files:**
- Create: `.gitignore`, `LICENSE` (texte GPL-3.0), `README.md` (court : nom, une phrase, « en construction »), `Directory.Build.props`, `trofeo-HB-dashboard.sln`
- Create: `src/Trofeo.ModuleKit/Trofeo.ModuleKit.csproj`, `src/Trofeo.Core/Trofeo.Core.csproj`, `src/Trofeo.Device/Trofeo.Device.csproj`, `src/Trofeo.Modules.Clock/Trofeo.Modules.Clock.csproj`, `src/Trofeo.Display/Trofeo.Display.csproj`, `tools/Trofeo.HardwareCheck/Trofeo.HardwareCheck.csproj`
- Create: `tests/Trofeo.Core.Tests/…csproj`, `tests/Trofeo.Device.Tests/…csproj`, `tests/Trofeo.Modules.Clock.Tests/…csproj`, `tests/Trofeo.Display.Tests/…csproj` (`net10.0-windows10.0.19041.0`), `tests/Trofeo.TestDoubles/…csproj` (bibliothèque partagée de faux objets : `FakeUsbTransport`, `FakeModule`)

**Interfaces:**
- Produces: projets en `net10.0`. Exceptions : `Trofeo.Device`, `Trofeo.Display` et `Trofeo.HardwareCheck` en `net10.0-windows10.0.19041.0`. `Nullable` et `TreatWarningsAsErrors` sont activés dans `Directory.Build.props`.

- [ ] **Step 1: Installer le SDK .NET 10** (seul le SDK 8 est présent sur la machine)

Run: `winget install --id Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements`
Then: `dotnet --list-sdks`
Expected: une ligne `10.0.x`.

- [ ] **Step 2: `git init`, puis `.gitignore`**

Le `.gitignore` contient `bin/`, `obj/`, `.vs/`, `*.user`, `.superpowers/`, `publish/`.

- [ ] **Step 3: Créer la solution et les projets**

Utiliser `dotnet new sln` / `classlib` / `xunit` / `console` / `winforms`, puis `dotnet sln add`.

Références :
- Core → ModuleKit ;
- Device : aucune référence ;
- Modules.Clock → ModuleKit ;
- Display → Core, Device, Modules.Clock ;
- chaque projet de tests → le projet qu'il teste, plus TestDoubles ;
- TestDoubles → ModuleKit, Device ;
- HardwareCheck → Core, Device.

Paquets :
- `SkiaSharp` dans ModuleKit ;
- `Microsoft.Extensions.TimeProvider.Testing` dans les tests ;
- `UseWindowsForms=true` uniquement dans Display.

- [ ] **Step 4: Vérifier que tout compile et que les tests (vides) passent**

Run: `dotnet build` puis `dotnet test`
Expected: `Build succeeded`, `0 Warning(s)`, et les tests passent (0 test pour l'instant).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: solution .NET 10, projets, licence GPL-3.0, spec et plan 1"
```

---

### Task 1: Le contrat de module (`Trofeo.ModuleKit`)

**Files:**
- Create: `src/Trofeo.ModuleKit/IModule.cs`, `IModuleFactory.cs`, `ModuleDescriptor.cs`, `ModuleStatus.cs`, `GridSize.cs`, `ModuleRenderContext.cs`, `Theme.cs`, `TextSize.cs`
- Test: `tests/Trofeo.Core.Tests/ModuleKitTests.cs`

**Interfaces:**
- Produces (exact) :

```csharp
public readonly record struct GridSize(int Columns, int Rows);
public enum SizePreset { Compact, Ideal, Large }
public enum TextSize { Small, Normal, Large }          // facteurs 0.85 / 1.0 / 1.2
public static class TextSizeExtensions { public static float Factor(this TextSize s); }
public enum ModuleHealth { NotConfigured, Online, Offline }
public sealed record ModuleStatus(ModuleHealth Health, string? ActionMessage = null);
public sealed record ModuleDescriptor(string TypeId, string DisplayName, GridSize MinSize,
    IReadOnlyDictionary<SizePreset, GridSize> Presets);
public sealed record Theme(string Id, SKColor Background, SKColor Panel, float PanelOpacity,
    float CornerRadius, SKColor Text, SKColor TextMuted, SKColor Accent, SKColor Warning, SKTypeface Typeface);
public sealed record ModuleRenderContext(SKRect Bounds, GridSize Size, Theme Theme, float TextScale, DateTimeOffset Now);
public interface IModule : IDisposable
{
    ModuleDescriptor Descriptor { get; }
    ModuleStatus Status { get; }
    bool IsAnimating { get; }
    event EventHandler? Invalidated;          // "redessine-moi"
    Task StartAsync(CancellationToken ct);
    void Render(SKCanvas canvas, ModuleRenderContext ctx); // dessine dans ctx.Bounds, panneau déjà peint
}
public interface IModuleFactory
{
    ModuleDescriptor Descriptor { get; }
    IModule Create(JsonElement settings, TimeProvider time);
}
```

- [ ] **Step 1: Test qui échoue**

`TextSize_Factors` vérifie Small = 0.85f, Normal = 1.0f, Large = 1.2f. `GridSize_Equality` vérifie que `new GridSize(4,2) == new GridSize(4,2)`.

- [ ] **Step 2: Lancer le test** — `dotnet test --filter ModuleKitTests`. Expected: FAIL (types absents).
- [ ] **Step 3: Écrire les types ci-dessus.** Le module dessine avec `ctx.Theme` et multiplie toutes ses tailles de police par `ctx.TextScale`. C'est ce qui rendra la taille du texte réglable par module.
- [ ] **Step 4: Lancer le test** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(modulekit): contrat public de module"`

---

### Task 2: Géométrie du damier (`Trofeo.Core/Layout/GridGeometry.cs`)

**Files:**
- Create: `src/Trofeo.Core/Layout/GridGeometry.cs`
- Test: `tests/Trofeo.Core.Tests/GridGeometryTests.cs`

**Interfaces:**
- Produces: `public static class GridGeometry { public const int Width = 1920, Height = 462, Columns = 16, Rows = 4, Inset = 14, Gap = 12; public static SKRectI ModuleRect(int column, int row, int columns, int rows); }`

Formule (arrondi `MidpointRounding.AwayFromZero`) :
- `x0 = 14 + round(column × 1904 / 16)`
- `x1 = 14 + round((column + columns) × 1904 / 16) − 12`
- `y0 = 14 + round(row × 446 / 4)`
- `y1 = 14 + round((row + rows) × 446 / 4) − 12`

Le 1904 vaut 1892 + 12 et le 446 vaut 434 + 12 : c'est la zone intérieure, plus un espace.

- [ ] **Step 1: Tests qui échouent** (`ModuleRect_AlexLayout`) avec la disposition d'Alex :

| Module | Appel | Rectangle attendu |
|---|---|---|
| Claude Code | `ModuleRect(0,0,8,4)` | `(14,14,954,448)` |
| Monitoring | `ModuleRect(8,0,4,2)` | `(966,14,1430,225)` |
| En écoute | `ModuleRect(8,2,4,2)` | `(966,237,1430,448)` |
| Heure | `ModuleRect(12,0,4,2)` | `(1442,14,1906,225)` |
| Réseau | `ModuleRect(12,2,4,2)` | `(1442,237,1906,448)` |

- [ ] **Step 2: Lancer** — Expected: FAIL.
- [ ] **Step 3: Implémenter `ModuleRect`** avec la formule ci-dessus.
- [ ] **Step 4: Lancer** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(core): géométrie du damier 16x4"`

---

### Task 3: Disposition et sauvegarde (`Trofeo.Core/Layout`)

**Files:**
- Create: `src/Trofeo.Core/Layout/ModulePlacement.cs`, `DashboardLayout.cs`, `LayoutValidator.cs`, `LayoutStore.cs`, `src/Trofeo.Core/AppPaths.cs`
- Test: `tests/Trofeo.Core.Tests/LayoutValidatorTests.cs`, `LayoutStoreTests.cs`

**Interfaces:**
- Consumes: `GridSize`, `TextSize`, `ModuleDescriptor` (Task 1).
- Produces:

```csharp
public sealed record ModulePlacement(Guid InstanceId, string TypeId, int Column, int Row,
    int Columns, int Rows, TextSize TextSize, JsonElement Settings);
public sealed record DashboardLayout(int Rotation, IReadOnlyList<ModulePlacement> Placements)
{ public static DashboardLayout Default { get; } }   // Rotation 180, une Heure en (12,0,4,2)
public enum PlacementError { None, OutOfBounds, Overlap, BelowMinSize, UnknownType }
public static class LayoutValidator
{ public static PlacementError Check(DashboardLayout layout, ModulePlacement candidate,
      IReadOnlyDictionary<string, ModuleDescriptor> types); } // ignore candidate.InstanceId lui-même
public sealed class LayoutStore
{
    public LayoutStore(string filePath);
    public DashboardLayout Load();          // absent → Default ; corrompu → Default + copie en .bad
    public void Save(DashboardLayout layout); // écriture atomique : .tmp puis File.Move(overwrite)
}
public static class AppPaths { public static string SettingsDir { get; } } // %AppData%\trofeo-HB-dashboard
```

- [ ] **Step 1: Tests qui échouent**
  - `Check_OutOfBounds` : colonne 14 avec une largeur de 4 → `OutOfBounds`.
  - `Check_Overlap` : une Heure (12,0,4,2) existe déjà, on place un candidat en (11,0,2,2) → `Overlap`.
  - `Check_BelowMin` : taille minimum 4 × 2, candidat 2 × 2 → `BelowMinSize`.
  - `Check_MovingSelf_IsAllowed` : même `InstanceId`, déplacé d'une colonne → `None`.
  - `Load_Missing_ReturnsDefault`.
  - `SaveThenLoad_RoundTrips` : la rotation, les placements et `Settings` (`{"format":"12h"}`) sont identiques après relecture.
  - `Load_CorruptFile_FallsBackAndKeepsBackup` : un fichier contenant `{nope` → `Default`, et le fichier `layout.json.bad` existe avec le contenu d'origine.
- [ ] **Step 2: Lancer** — Expected: FAIL.
- [ ] **Step 3: Implémenter** avec System.Text.Json (enums écrits en texte) et des tests sur un dossier temporaire.
- [ ] **Step 4: Lancer** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(core): disposition, validation et sauvegarde"`

---

### Task 4: Rendu et encodage (`Trofeo.Core/Rendering`)

**Files:**
- Create: `src/Trofeo.Core/Rendering/FrameComposer.cs`, `JpegEncoder.cs`, `Themes.cs`, `FontLoader.cs`
- Create: `src/Trofeo.Core/Assets/Fonts/BarlowSemiCondensed-Regular.ttf`, `-SemiBold.ttf`, avec `OFL.txt` (ressources embarquées, depuis le dépôt google/fonts, `ofl/barlowsemicondensed`)
- Test: `tests/Trofeo.Core.Tests/FrameComposerTests.cs`, `JpegEncoderTests.cs`

**Interfaces:**
- Consumes: `GridGeometry.ModuleRect`, `DashboardLayout`, `IModule`, `Theme`.
- Produces:

```csharp
public static class Themes { public static Theme Classique { get; } }
// Classique : Background #0E1116, Panel #111317 opacité 0.86, rayon 14, Text #ECEEF0,
// TextMuted #8C929C, Accent #7FD1C7, Warning #F2B84B, typo Barlow Semi Condensed
public static class FontLoader { public static SKTypeface Load(string resourceName); }
public sealed class FrameComposer
{
    public SKBitmap Compose(DashboardLayout layout, IReadOnlyDictionary<Guid, IModule> modules,
                            Theme theme, DateTimeOffset now); // 1920x462
}
public static class JpegEncoder
{
    public const int MaxBytes = 450_000;
    public static byte[] Encode(SKBitmap frame, int rotation, int quality = 88); // rotation 0|180
}
```

**Comportement attendu**
- `Compose` peint le fond du thème. Pour chaque placement, il peint ensuite le panneau arrondi du thème (couleur `Panel` à l'opacité `PanelOpacity`, rayon `CornerRadius`). Puis il appelle `module.Render` dans une zone découpée (*clip*) au rectangle du module, avec `ModuleRenderContext(rect, new GridSize(p.Columns, p.Rows), theme, p.TextSize.Factor(), now)`.
- Si `Render` lève une exception, on peint à sa place un panneau d'erreur (texte « Erreur du module »), on journalise, et on continue avec les autres modules.
- `Encode` applique la rotation, puis encode. Si le résultat dépasse `MaxBytes`, il réessaie avec la qualité diminuée de 12 à chaque fois, sans descendre sous 20.

- [ ] **Step 1: Tests qui échouent**
  - `Compose_Size` : l'image fait 1920 × 462.
  - `Compose_PaintsPanelInModuleRect` : avec un faux module qui ne dessine rien, le pixel (1500,100) a la couleur du panneau mélangée au fond (ni le fond seul, ni du transparent), et le pixel (5,5) a exactement la couleur du fond.
  - `Compose_ModuleThrows_DrawsErrorPanelOnly` : sur deux faux modules, l'un lève une exception et l'autre peint en vert pur. Aucune exception ne sort de `Compose` et la zone de l'autre module contient du vert.
  - `Encode_IsJpeg` : le résultat commence par `FF D8`.
  - `Encode_Rotation180` : une image rouge à gauche et bleue à droite, encodée avec 180 puis décodée, a du bleu à gauche.
  - `Encode_TooLarge_LowersQuality` : une image de bruit aléatoire 1920 × 462, encodée, donne `Length <= 450_000`.
- [ ] **Step 2: Lancer** — Expected: FAIL.
- [ ] **Step 3: Implémenter.** Pour le 4:4:4 : `SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore)`.
- [ ] **Step 4: Lancer** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(core): composition des modules et encodage JPEG"`

---

### Task 5: Protocole USB et écran (`Trofeo.Device`)

**Files:**
- Create: `src/Trofeo.Device/LyProtocol.cs`, `IUsbTransport.cs`, `WinUsbTransport.cs`, `ScreenDevice.cs`, `ScreenState.cs`
- Test: `tests/Trofeo.Device.Tests/LyProtocolTests.cs`, `ScreenDeviceTests.cs`
- Create: `tests/Trofeo.TestDoubles/FakeUsbTransport.cs` (et `FakeUsbTransportFactory`), réutilisé à la tâche 9

**Interfaces:**
- Produces:

```csharp
public static class LyProtocol
{
    public static byte[] BuildHandshake();                       // 2048 o : [0]=0x02 [1]=0xFF [8]=0x01
    public static bool IsValidHandshakeReply(ReadOnlySpan<byte> r); // [0]=0x03 [1]=0xFF [8]=0x01
    public static byte[] Packetize(ReadOnlySpan<byte> jpeg);     // annexe A de la spec
}
public sealed class UsbTimeoutException : Exception;
public sealed class UsbBusyException : Exception;                // ouverture refusée (autre programme)
public interface IUsbTransport : IDisposable
{
    void Write(ReadOnlySpan<byte> data, int timeoutMs);          // lève UsbTimeoutException
    int Read(Span<byte> buffer, int timeoutMs);                  // lève UsbTimeoutException
    void ResetPipes();                                           // abort + reset (clear halt) des deux pipes
}
public interface IUsbTransportFactory { IUsbTransport? TryOpen(); } // null = absent ; UsbBusyException = occupé
public enum ScreenState { Disconnected, Ready, Recovering }
public sealed class ScreenDevice : IDisposable
{
    public ScreenDevice(IUsbTransportFactory factory, TimeProvider time);
    public ScreenState State { get; }
    public string? Reason { get; }                                // motif en français si Disconnected
    public event EventHandler? StateChanged;
    public bool TryConnect();                                     // ouvre + poignée de main, renvoie LastFrame si présent
    public bool TrySendFrame(byte[] jpeg);                        // une image en cours, validation attendue
    public byte[]? LastFrame { get; }
}
```

**Comportement attendu**
- `Packetize` suit exactement l'annexe A : `n = len / 496 + 1` paquets de 512 octets (en-tête de 16 + charge de 496), puis des paquets à zéro jusqu'à un multiple de 4. Lève `ArgumentException` au-delà de 450 000 octets.
- `TrySendFrame` :
  - écrit par blocs de 4096 octets avec un délai de 5000 ms, puis lit 512 octets avec un délai de 1000 ms ;
  - **en cas de délai dépassé**, passe en `Recovering` et lance la reprise ;
  - **la reprise** : `ResetPipes()`, lectures de 300 ms jusqu'au premier délai dépassé pour vider la file, poignée de main, puis **un seul** nouvel essai de l'image ;
  - si la reprise échoue : transport libéré, état `Disconnected`, motif « Écran ne répond plus ».
- `TryConnect` :
  - `null` → motif « Écran non branché » ;
  - `UsbBusyException` → motif « Écran utilisé par un autre programme (fermez TRCC) ».
- `WinUsbTransport` :
  - trouve le périphérique par `SetupDiGetClassDevs` sur l'interface `{3876E417-E089-4BCC-BBF8-47ABA55E46FB}`, puis `CreateFile` (overlapped) et `WinUsb_Initialize` ;
  - règle les délais avec `WinUsb_SetPipePolicy(PIPE_TRANSFER_TIMEOUT)` ;
  - `ERROR_ACCESS_DENIED` (5) à l'ouverture → `UsbBusyException` ; `ERROR_SEM_TIMEOUT` (121) → `UsbTimeoutException`.

- [ ] **Step 1: Tests du protocole qui échouent**
  - `Handshake_Bytes` : longueur 2048, octets 0, 1 et 8 = `02`, `FF`, `01`, tout le reste à 0.
  - `HandshakeReply_Real` : la réponse réelle de l'annexe A (`03 ff 00 00 00 00 00 00 01 …`) est acceptée, et `03 fe …` est refusée.
  - `Packetize_1000Bytes` : longueur 2048 ; paquet 0 avec un en-tête `01 FF`, une taille totale de 1000 en u32 LE, une charge de 496, l'octet 8 à `01`, un total de 3 paquets et l'index 0 ; paquet 2 avec une charge de 8 ; les octets 1536 à 2047 à zéro.
  - `Packetize_992Bytes` : 3 paquets, le dernier avec une charge de 0, total paddé à 2048.
  - `Packetize_TooLarge_Throws` : 450 001 octets → `ArgumentException`.
- [ ] **Step 2: Tests de `ScreenDevice` qui échouent**, avec un `FakeUsbTransport` scriptable qui enregistre les appels :
  - `Connect_Handshake_Ready`.
  - `Send_WritesIn4096Blocks_ThenReadsAck` : pour un JPEG de 5000 octets, on attend 12 paquets, donc 6144 octets, écrits en un bloc de 4096 et un de 2048, puis une lecture de 512.
  - `AckTimeout_RecoversAndResendsOnce` : on attend l'ordre `ResetPipes`, puis les lectures de vidage, la poignée de main, la réécriture, et `true` à la fin.
  - `RecoveryFails_Disconnected` : poignée de main refusée → `Disconnected` avec le motif « Écran ne répond plus ».
  - `OpenDenied_ReportsBusy`.
  - `Reconnects_AndResendsLastFrame` : la fabrique renvoie d'abord `null`, puis un transport → `TryConnect` réussit et `LastFrame` est réécrite.
- [ ] **Step 3: Lancer** — `dotnet test tests/Trofeo.Device.Tests`. Expected: FAIL.
- [ ] **Step 4: Implémenter `LyProtocol`, `ScreenDevice` et `WinUsbTransport`.** `WinUsbTransport` n'a pas de test automatique : il est vérifié en tâche 7.
- [ ] **Step 5: Lancer** — Expected: PASS.
- [ ] **Step 6: Commit** — `git commit -m "feat(device): protocole LY, WinUSB et garde-fou de reprise"`

---

### Task 6: Cadence d'envoi (`Trofeo.Core/Rendering/FrameScheduler.cs`)

**Files:**
- Create: `src/Trofeo.Core/Rendering/FrameScheduler.cs`
- Test: `tests/Trofeo.Core.Tests/FrameSchedulerTests.cs`

**Interfaces:**
- Produces: `public sealed class FrameScheduler { public FrameScheduler(TimeProvider time, double maxFps = 12); public void MarkDirty(); public bool ShouldRender(bool anyAnimating); public void OnRendered(); public TimeSpan MinInterval { get; } }`

`ShouldRender` renvoie vrai si deux conditions sont réunies :
- un module a demandé à être redessiné, ou un module est en train de s'animer ;
- au moins `MinInterval` s'est écoulé depuis le dernier envoi (1000/12 ≈ 83,3 ms).

- [ ] **Step 1: Tests qui échouent** (avec `FakeTimeProvider`)
  - `Idle_NeverRenders` : aucun module à redessiner, aucune animation → faux, même après 10 s.
  - `Dirty_RendersOnce` : vrai, puis faux après `OnRendered`.
  - `Animating_CappedAt12fps` : en avançant de 10 ms à la fois pendant 1 s, avec une animation en cours et `OnRendered` à chaque vrai, on compte 12 envois.
  - `MaxFps_Configurable` : avec `maxFps: 6`, on compte 6 envois en 1 s.
- [ ] **Step 2: Lancer** — Expected: FAIL.
- [ ] **Step 3: Implémenter.**
- [ ] **Step 4: Lancer** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(core): cadence d'envoi plafonnée à 12 images/s"`

---

### Task 7: Vérification sur le vrai écran (`tools/Trofeo.HardwareCheck`)

**Files:**
- Create: `tools/Trofeo.HardwareCheck/Program.cs`

**Interfaces:**
- Consumes: `ScreenDevice`, `WinUsbTransport`, `JpegEncoder`.

Programme console :
- `--orientation <0|180>` affiche une mire « HAUT GAUCHE » avec un cadre vert.
- `--fps` envoie des images animées pendant 10 s et affiche les images/s et le nombre de validations manquées.
- `--recovery` provoque volontairement le gel : une image envoyée sans attendre la validation, comme pendant l'essai du 24/10. Il appelle ensuite `TrySendFrame` et affiche si l'écran est revenu.

- [ ] **Step 1: Écrire `Program.cs`** avec les trois options.
- [ ] **Step 2: Mire d'orientation.** Logiciel Thermalright fermé, lancer : `dotnet run --project tools/Trofeo.HardwareCheck -- --orientation 180`
  Expected: la mire est à l'endroit sur l'écran d'Alex (demander à Alex de confirmer).
- [ ] **Step 3: Mesure de débit.** Run: `dotnet run --project tools/Trofeo.HardwareCheck -- --fps`
  Expected: entre 11,5 et 12,5 images/s, 0 validation manquée.
- [ ] **Step 4: Reprise après gel.** Run: `dotnet run --project tools/Trofeo.HardwareCheck -- --recovery`
  Expected: `recovered: True`, et l'écran affiche à nouveau la mire.
- [ ] **Step 5: Commit** — `git commit -m "chore(tools): vérification matérielle (orientation, débit, reprise)"`

---

### Task 8: Module Heure + date (`Trofeo.Modules.Clock`)

**Files:**
- Create: `src/Trofeo.Modules.Clock/ClockModule.cs`, `ClockFactory.cs`, `ClockText.cs`, `ClockSettings.cs`
- Test: `tests/Trofeo.Modules.Clock.Tests/ClockTextTests.cs`, `ClockModuleTests.cs`

**Interfaces:**
- Consumes: `IModule`, `IModuleFactory`, `ModuleRenderContext` (Task 1).
- Produces:
  - `ClockFactory`, dont le `Descriptor` vaut `TypeId "clock"`, `DisplayName "Heure et date"`, `MinSize 3×1`, `Presets { Compact 3×1, Ideal 4×2, Large 6×2 }`.
  - `public enum ClockFormat { H24, H12 }` et `public sealed record ClockSettings(ClockFormat Format = ClockFormat.H24)`, désérialisé de `{"format":"24h"|"12h"}`.
  - `public static class ClockText { public static string Time(DateTime local, ClockFormat f); public static string Date(DateTime local); }`

**Comportement attendu**
- Le module dessine l'heure en grand (104 px × `TextScale`, typo du thème), avec la date dessous en `TextMuted`. C'est la disposition de la maquette `themes-v3.html`, sans la météo, qui arrivera au plan 2.
- Il déclenche `Invalidated` à chaque changement de minute (minuterie calée sur la minute suivante via `TimeProvider`). `IsAnimating` vaut toujours faux.
- `Status` vaut toujours `Online`.

- [ ] **Step 1: Tests qui échouent**
  - `Time_24h` : 14:32 → `"14:32"`, 09:05 → `"09:05"`.
  - `Time_12h` : 14:32 → `"2:32 PM"`, 00:05 → `"12:05 AM"`.
  - `Date_French` : samedi 24/10/2026 → `"Samedi 24 octobre"`.
  - `Invalidates_AtMinuteBoundary` : avec `FakeTimeProvider` à 14:32:50, rien après 9 s, puis un événement en passant 14:33:00.
  - `Settings_Parse12h` : `{"format":"12h"}` → `H12`, et `{}` → `H24`.
- [ ] **Step 2: Lancer** — Expected: FAIL.
- [ ] **Step 3: Implémenter.** La date est formatée avec `CultureInfo("fr-FR")`, le format `"dddd d MMMM"`, et la première lettre en majuscule.
- [ ] **Step 4: Lancer** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(clock): module Heure et date 24 h / 12 h"`

---

### Task 9: Service d'affichage (`Trofeo.Display`)

**Files:**
- Create: `src/Trofeo.Display/Program.cs`, `DisplayLoop.cs`, `ModuleRegistry.cs`, `TrayIcon.cs`
- Test: `tests/Trofeo.Display.Tests/DisplayLoopTests.cs`, avec un `ScreenDevice` monté sur le `FakeUsbTransport` et le `FakeModule` de `tests/Trofeo.TestDoubles`.

**Interfaces:**
- Consumes: tout ce qui précède.
- Produces:

```csharp
public sealed class ModuleRegistry
{ public ModuleRegistry(IEnumerable<IModuleFactory> factories);
  public IReadOnlyDictionary<string, ModuleDescriptor> Descriptors { get; }
  public IModule? Create(ModulePlacement p, TimeProvider time); }   // type inconnu → null (ignoré, journalisé)
public sealed class DisplayLoop : IDisposable
{ public DisplayLoop(DashboardLayout layout, ModuleRegistry registry, ScreenDevice screen,
                    FrameScheduler scheduler, TimeProvider time);
  public void Tick();                // une itération : reconnecte si besoin (toutes les 2 s), rend et envoie si ShouldRender
  public int FramesSent { get; } }
```

**Comportement attendu**
- `Program` :
  - instance unique, via le mutex `Local\trofeo-HB-dashboard.display` ;
  - charge `layout.json` depuis `AppPaths.SettingsDir` ;
  - démarre les modules (`StartAsync`) ;
  - fait tourner `Tick` sur un fil dédié toutes les 10 ms ;
  - affiche l'icône de notification. Info-bulle : « Trofeo dashboard — écran : prêt / non branché / reprise… ». Menu : « Quitter ».
- `Invalidated` d'un module → `scheduler.MarkDirty()`.
- Après une reconnexion, la dernière image est renvoyée (géré par `ScreenDevice`).

- [ ] **Step 1: Tests qui échouent**
  - `Tick_Idle_SendsNothing` : un module qui n'invalide jamais → après une première image envoyée, 0 envoi de plus sur 5 s simulées.
  - `Tick_Invalidated_SendsOneFrame`.
  - `Tick_Disconnected_RetriesEvery2s` : la fabrique renvoie `null` → `TryOpen` est appelé une fois toutes les 2 s, pas à chaque passage.
- [ ] **Step 2: Lancer** — Expected: FAIL.
- [ ] **Step 3: Implémenter `ModuleRegistry`, `DisplayLoop`, `TrayIcon` et `Program`.**
- [ ] **Step 4: Lancer** — Expected: PASS.
- [ ] **Step 5: Essai réel.** Logiciel Thermalright fermé, lancer : `dotnet run --project src/Trofeo.Display -c Release`
  Expected: l'heure et la date d'aujourd'hui s'affichent à l'endroit en haut à droite de l'écran (module 4 × 2 en colonnes 13 à 16), dans le thème Classique. Elles changent à la minute suivante.
- [ ] **Step 6: Essai de débranchement.** Débrancher le câble USB de l'écran pendant 5 s, puis le rebrancher.
  Expected: l'info-bulle passe à « non branché », puis l'heure réapparaît en moins de 3 s.
- [ ] **Step 7: Sobriété.** Laisser tourner 5 minutes, puis relever la mémoire et le CPU (`Get-Process Trofeo.Display | Select WorkingSet64, CPU`).
  Expected: mémoire < 100 Mo et temps CPU cumulé < 3 s. Noter les valeurs dans le message de commit.
- [ ] **Step 8: Commit** — `git commit -m "feat(display): service de fond avec icône, boucle d'affichage et reconnexion"`

---

### Task 10: Publication sur GitHub (demande l'accord d'Alex)

**Files:**
- Modify: `README.md` (nom, une phrase de présentation, état « en construction », crédits du protocole : thermalright-trcc-linux, trofeo-lcd, TrofeoVision, Open-Trofeo-LCD ; licence GPL-3.0 ; mention « projet de fan, non affilié à Anthropic, OpenAI ni Thermalright »)

- [ ] **Step 1: Installer GitHub CLI** — `winget install --id GitHub.cli --silent --accept-package-agreements --accept-source-agreements`, puis `gh --version`.
- [ ] **Step 2: Demander à Alex de se connecter lui-même.** C'est une connexion à son compte : on ne la fait pas à sa place. Il lance `gh auth login` dans un terminal. Vérifier avec `gh auth status`.
- [ ] **Step 3: Demander à Alex la confirmation explicite** avant de publier le dépôt public `trofeo-HB-dashboard`.
- [ ] **Step 4: Avec son accord,** publier : `gh repo create trofeo-HB-dashboard --public --source . --remote origin --push --description "Dashboard modulaire pour l'écran Thermalright Trofeo Vision 9.16"`
  Expected: l'URL du dépôt est affichée, et `git log origin/main` montre les commits.

---

## Après ce plan

Le plan 2 (Modules de données) sera écrit à partir de la spec une fois ce plan livré et vérifié sur l'écran. La belle présentation GitHub (captures rendues par l'émulateur, GIF de la mascotte) arrive au plan 6, quand il y aura de quoi la montrer.
