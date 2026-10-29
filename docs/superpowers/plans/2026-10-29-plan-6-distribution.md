# Plan 6 — Distribution (Ashboard Vision)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Faire du tableau de bord un logiciel qu'un ami installe comme VLC. Il s'appelle **Ashboard Vision** et se distribue avec :
- un installateur à pages ;
- une mise à jour en un clic depuis GitHub, en Stable ou en Bêta ;
- une désinstallation propre ;
- des versions construites par GitHub ;
- une page GitHub soignée ;
- un logo qui suit le thème de Windows.

**Architecture:**
- **Une nouvelle bibliothèque `Trofeo.Distribution`, sans Windows et testable sous Linux.** Elle contient :
  - les numéros de version ;
  - la lecture des versions GitHub et le choix de la mise à jour ;
  - le téléchargement vérifié et la copie des réglages ;
  - la préparation et le nettoyage propres à l'utilisateur.
- **Le service (`Trofeo.Display`) l'utilise.** Il fait le déménagement des réglages au démarrage et la vérification des mises à jour une fois par jour. Il expose l'état de la mise à jour à la fenêtre par le canal de contrôle (protocole 2), et dans l'icône de notification.
- **La machine et l'utilisateur sont séparés.**
  - L'installateur Inno Setup (`installer/`), en administrateur, s'occupe de la machine.
  - Le service, lancé comme l'utilisateur, s'occupe de ce qui lui appartient : valeur `Run`, mod, `%AppData%`.
- **Un outil (`tools/Trofeo.BrandAssets`) fabrique les icônes et images à partir des SVG du logo.** Les fichiers produits sont commités dans `assets/brand/`, si bien que la construction n'a pas besoin de l'outil.
- **Une recette GitHub Actions publie les versions à partir d'une étiquette `vX.Y.Z`.** Le script de construction de l'installateur (`installer/build.ps1`) sert aussi bien sur le PC d'Alex que sur GitHub.

**Tech Stack:**
- .NET 10, C#, SkiaSharp 3.119.4, Avalonia 12.1.4 ;
- xUnit 2, et xUnit v3 pour `Trofeo.Settings.Tests` ;
- FakeTimeProvider ;
- Inno Setup 6, version 6.6 ou plus pour le thème dynamique (à vérifier, Task 9) ;
- GitHub Actions (`windows-latest`) ;
- Svg.Skia, seulement dans l'outil des images ;
- ffmpeg, pour l'image animée, hors du logiciel.

**Spec:**
- `docs/superpowers/specs/2026-10-29-plan-6-distribution-design.md` : la conception du plan 6, avec le logo en section 4.7. Elle prime sur la spec d'origine là où elles diffèrent.
- Spec d'origine : `docs/superpowers/specs/2026-10-24-trofeo-dashboard-design.md`, sections 9 et 11.
- Écrans : `docs/superpowers/notes/2026-10-29-ecrans-thermalright.md`.
- Logo : `docs/superpowers/logo/`.
- Image d'accueil : `docs/superpowers/maquettes/readme-accueil-v4.html`.

## Global Constraints

- **Nom affiché : « Ashboard Vision »** partout où quelqu'un le voit. **Identifiant technique : `ashboard-vision`.** **Dépôt : `Ashewos/ashboard-vision`.** **Éditeur : « Ashewos ».**
- **Ces noms gardent l'ancien identifiant, exprès :**
  - l'entropie DPAPI de `DpapiSecretVault` (`"trofeo-HB-dashboard"`) ;
  - `FreeboxSource.AppId` (`"trofeo-HB-dashboard"`) ;
  - `LayoutTransfer.Format` (`"trofeo-HB-dashboard/disposition/1"`, pour relire les exports existants) ;
  - les tubes, les mutex et les noms de code `Trofeo.*`.

  Un test fige chacun d'eux (Task 3).
- **Textes** en français. Ils tutoient et sont rangés avec les autres textes de la fenêtre (`Strings.resx` / `Texts.cs`), ou dans des constantes comme dans `TrayIcon`. Pas de « → » dans les boutons. Un bouton dit ce qu'il fait.
- **Sobriété** : le service reste à environ 0,3 % de processeur.
  - La vérification des mises à jour se fait une fois au démarrage (60 s après), puis toutes les 24 h. Rien ne tourne entre deux vérifications.
  - La recherche d'un écran non pris en charge se fait au plus toutes les 30 s.
- **Rien n'est poussé, publié, étiqueté ni renommé sur GitHub sans l'accord d'Alex** donné pour cette action-là. On travaille sur `Canary`. Les commits utilisent l'adresse configurée dans le dépôt.
- **Ne jamais commiter `docs/guide-personnalisation.md`.**
- **Construire en Debug** sur le PC d'Alex : son service tourne depuis `bin\Release` et verrouille ses fichiers.
- **Écran pris en charge** : LY `0416:5408`, modèle `PM = 64 + octet 20` (les valeurs 0 à 3 comptent pour 1), soit **65**. Limite de **450 000 octets** par image, inchangée.
- **Versions** :
  - SemVer ;
  - les bêtas d'essai sont `1.0.0-beta.N`, depuis `Canary` ;
  - la première stable est la `1.0.0`, depuis `main` ;
  - l'installateur s'appelle `Ashboard-Vision-Setup-<version>.exe`, avec son empreinte `Ashboard-Vision-Setup-<version>.exe.sha256`.

## Review Focus

1. **L'ancienne version tourne pendant l'installation.** L'ancien service, lancé depuis le dépôt, tient son mutex et `%AppData%\trofeo-HB-dashboard`. On attend que l'installateur l'arrête et que le déménagement n'efface rien. S'il ne peut pas déplacer le dossier, l'ancien reste en place et un message le dit (tests Task 4 et Task 9).
2. **GitHub est injoignable, ou répond 403 (limite de l'API), ou renvoie un JSON inattendu.** Pas de plantage. L'état passe à « Impossible de vérifier » avec le motif, et la vérification suivante a lieu à l'heure prévue (tests Task 1 et Task 2).
3. **Le téléchargement est interrompu, l'empreinte ne correspond pas, ou elle est absente.** L'installateur n'est jamais lancé, et le fichier partiel est supprimé (test Task 2).
4. **Windows ne reçoit pas l'autorisation administrateur.** Rien ne change, le service continue, et le message dit « Mise à jour annulée » (tests Task 2 et Task 7).
5. **On repasse de Bêta à Stable avec des réglages écrits par la bêta.** La copie des réglages existe **avant** le lancement de l'installateur, et un échec de la copie empêche l'installation (test Task 2).

---

## Structure des fichiers

| Fichier | Rôle |
|---|---|
| `src/Trofeo.Core/ProductIdentity.cs` | Noms affichés, identifiants nouveaux et anciens, dépôt |
| `src/Trofeo.Distribution/` (nouveau, `net10.0`) | `AppVersion`, `ReleaseFeed`, `UpdatePolicy`, `UpdatePreferencesStore`, `UpdateDownloader`, `SettingsBackup`, `UpdateService`, `LegacySettingsMove`, `UserSetup`, `UserCleanup`, et leurs interfaces (`IRunKey`, `IInstallerLauncher`) |
| `tests/Trofeo.Distribution.Tests/` (nouveau) | Tests de tout ce qui précède, sous Linux |
| `src/Trofeo.Device/LyProtocol.cs`, `ScreenDevice.cs`, `KnownThermalrightDevices.cs` (nouveau), `UsbPresenceScanner.cs` (nouveau) | Modèle de l'écran, écran non pris en charge |
| `src/Trofeo.Control/Messages.cs`, `ControlServer.cs`, `ControlClient.cs`, `ControlPipe.cs` | Protocole 2 : état et commandes de mise à jour |
| `src/Trofeo.Display/Program.cs`, `TrayIcon.cs`, `WindowsInstallerLauncher.cs` (nouveau), `RegistryRunKey.cs` (nouveau), `TaskbarTheme.cs` (nouveau) | Branchement Windows |
| `src/Trofeo.Settings/ViewModels/SettingsTabViewModel.cs`, `DowngradeDialogViewModel.cs`, vues et textes | Mises à jour dans Paramètres, logo, nouveau nom |
| `tools/Trofeo.BrandAssets/`, `tests/Trofeo.BrandAssets.Tests/`, `assets/brand/` | Icônes, images de l'installateur, nom en tracés |
| `tools/Trofeo.ReadmeHero/`, `docs/images/` | Image d'accueil animée |
| `installer/ashboard-vision.iss`, `installer/build.ps1`, `installer/pawnio.json` | Installateur |
| `.github/workflows/release.yml`, `CHANGELOG.md` | Publication des versions |
| `README.md`, `docs/utilisation-avancee.md` | Page GitHub et documentation technique |

---

### Task 1 : Versions, versions GitHub et choix de la mise à jour

**Files:**
- Create: `src/Trofeo.Core/ProductIdentity.cs`
- Create: `src/Trofeo.Distribution/Trofeo.Distribution.csproj`, `AppVersion.cs`, `ReleaseFeed.cs`, `UpdatePolicy.cs`, `UpdatePreferences.cs`
- Create: `tests/Trofeo.Distribution.Tests/` (projet xUnit 2, comme `Trofeo.Service.Tests`)
- Modify: `trofeo-HB-dashboard.sln`

**Interfaces:**
- Produces :
  - `ProductIdentity` (static) :
    - `DisplayName = "Ashboard Vision"`, `Id = "ashboard-vision"`, `LegacyId = "trofeo-HB-dashboard"` ;
    - `Publisher = "Ashewos"`, `Repository = "Ashewos/ashboard-vision"` ;
    - `string SetupFileName(string version) => $"Ashboard-Vision-Setup-{version}.exe"`.
  - `readonly record struct AppVersion : IComparable<AppVersion>` :
    - `static bool TryParse(string? text, out AppVersion version)`, qui accepte un préfixe `v` et ignore `+métadonnées` ;
    - `bool IsPrerelease` et `ToString()` sans le `v`.
  - `sealed record GitHubRelease(AppVersion Version, bool Prerelease, string SetupUrl, long SetupSize, string? ChecksumUrl, DateTimeOffset PublishedAt)`.
  - `static class ReleaseFeed` : `static IReadOnlyList<GitHubRelease> Parse(string json)` et `const string Url = "https://api.github.com/repos/Ashewos/ashboard-vision/releases?per_page=30"`.
  - `enum UpdateChannel { Stable, Beta }` et `sealed record UpdatePreferences(UpdateChannel Channel, bool WaitForNextStable)`, avec `Default = new(Stable, false)`.
  - `sealed record UpdateOffer(GitHubRelease Release, bool IsDowngrade)` et `sealed record UpdateDecision(UpdateOffer? Offer, bool NoStableYet)`.
  - `static class UpdatePolicy` : `static UpdateDecision Choose(IReadOnlyList<GitHubRelease> releases, AppVersion installed, UpdatePreferences prefs)`.

- [ ] **Step 1 : Écrire les tests en échec**

`AppVersionTests` :
- `TryParse("v1.0.0")` et `TryParse("1.0.0+abc123")` donnent `1.0.0` ;
- `TryParse("1.0")` et `TryParse("bêta")` échouent ;
- l'ordre SemVer : `1.0.0-beta.2 < 1.0.0-beta.10 < 1.0.0 < 1.0.1 < 1.1.0-beta.1` ;
- `1.0.0-beta.1` a `IsPrerelease` à vrai.

`ReleaseFeedTests`, sur un JSON d'exemple au format de l'API GitHub, en fichier de test :
- les versions en brouillon (`draft: true`) sont ignorées ;
- une étiquette illisible est ignorée ;
- une version sans fichier `Ashboard-Vision-Setup-<v>.exe` est ignorée ;
- `ChecksumUrl` pointe vers le fichier `.sha256` de même nom, et vaut `null` s'il manque ;
- un JSON vide, `{}` ou tronqué lève `UpdateException` (déclarée ici : `sealed class UpdateException(string message) : Exception(message)`, avec un message en français).

`UpdatePolicyTests` :

| Installée | Canal | Versions publiées | Résultat attendu |
|---|---|---|---|
| `1.0.0` | Stable | `1.0.0`, `1.1.0-beta.1` | aucune offre |
| `1.0.0` | Stable | `1.0.1` | offre `1.0.1`, pas un retour en arrière |
| `1.0.0` | Bêta | `1.0.1`, `1.1.0-beta.1` | offre `1.1.0-beta.1` |
| `1.1.0-beta.1` | Bêta | `1.1.0` | offre `1.1.0` (une stable plus récente) |
| `1.1.0-beta.1` | Stable, `WaitForNextStable = false` | `1.0.1` | offre `1.0.1` avec `IsDowngrade` à vrai |
| `1.1.0-beta.1` | Stable, `WaitForNextStable = true` | `1.0.1` | aucune offre |
| `1.1.0-beta.1` | Stable, `WaitForNextStable = true` | `1.1.0` | offre `1.1.0` |
| `1.0.0-beta.1` | Stable | uniquement des bêtas | `NoStableYet` à vrai, aucune offre |

- [ ] **Step 2 : Lancer, vérifier l'échec** — `dotnet test tests/Trofeo.Distribution.Tests` : erreurs de compilation (types absents).
- [ ] **Step 3 : Implémenter.** `Trofeo.Distribution` référence `Trofeo.Core`. `ReleaseFeed.Parse` utilise `System.Text.Json` (`JsonDocument`). La comparaison suit l'ordre de priorité SemVer 2.0 : un identifiant numérique se compare comme un nombre.
- [ ] **Step 4 : Lancer, vérifier la réussite** — `dotnet test tests/Trofeo.Distribution.Tests` : tout est vert.
- [ ] **Step 5 : Commit** — `feat(distribution): versions, lecture des versions GitHub et choix de la mise à jour`

### Task 2 : Préférences, téléchargement vérifié, copie des réglages et service des mises à jour

**Files:**
- Create: `src/Trofeo.Distribution/UpdatePreferencesStore.cs`, `UpdateDownloader.cs`, `SettingsBackup.cs`, `IInstallerLauncher.cs`, `UpdateService.cs`
- Test: `tests/Trofeo.Distribution.Tests/UpdatePreferencesStoreTests.cs`, `UpdateDownloaderTests.cs`, `SettingsBackupTests.cs`, `UpdateServiceTests.cs`

**Interfaces:**
- Consumes : Task 1.
- Produces :
  - `sealed class UpdatePreferencesStore(string path)` : `UpdatePreferences Load()` (rend le défaut si le fichier est absent ou illisible) et `void Save(UpdatePreferences)`, en écriture atomique comme `LayoutStore`. Fichier : `%AppData%\ashboard-vision\mises-a-jour.json`.
  - `sealed class UpdateDownloader(HttpClient http)` : `Task<string> DownloadAsync(GitHubRelease release, string targetDir, IProgress<double>? progress, CancellationToken ct)`. Il rend le chemin du `Setup.exe` vérifié et lève `UpdateException` dans tous les cas d'échec.
  - `static class SettingsBackup` : `static string Create(string settingsDir, string backupsRoot, AppVersion from, DateTimeOffset now)`. Il copie tout sauf `logs\` vers `backupsRoot\<aaaa-MM-jj-HHmmss>-<version>\`. Racine : `%AppData%\ashboard-vision-sauvegardes`.
  - `enum InstallerLaunch { Started, Cancelled, Failed }` et `interface IInstallerLauncher { InstallerLaunch Launch(string setupPath); }`.
  - `enum UpdateState { Unknown, Checking, UpToDate, NoStableYet, Available, Downloading, Installing, Failed }`.
  - `sealed record UpdateStatus(UpdateState State, string? Version, bool IsDowngrade, int? Percent, string? Message, UpdatePreferences Preferences)`.
  - `sealed class UpdateService(UpdateChecker checker, UpdateDownloader downloader, IInstallerLauncher launcher, UpdatePreferencesStore prefs, AppVersion installed, string settingsDir, string backupsRoot, string downloadDir, TimeProvider time) : IDisposable`, avec :
    - `UpdateStatus Current` et `event EventHandler? Changed` ;
    - `void Start()` : première vérification 60 s après, puis toutes les 24 h ;
    - `Task CheckNowAsync(CancellationToken)` ;
    - `Task SetPreferencesAsync(UpdatePreferences, CancellationToken)` : enregistre, puis vérifie ;
    - `Task<(bool Ok, string Message)> InstallAsync(bool confirmDowngrade, bool reopenSettings, CancellationToken)`. Avec `reopenSettings`, il crée le repère de réouverture avant de lancer l'installateur.
  - `static class ReopenSettingsMarker` : `static void Create(string settingsDir)` et `static bool Consume(string settingsDir)`. `Consume` rend vrai et supprime le repère s'il existait. Fichier : `rouvrir-reglages`.
  - `sealed class UpdateChecker(HttpClient http)` : `Task<IReadOnlyList<GitHubRelease>> FetchAsync(CancellationToken)`. Il lit `ReleaseFeed.Url` et lève `UpdateException` sur un code HTTP autre que 200, avec un message qui cite le code (403 : « GitHub limite les vérifications pour l'instant »).

- [ ] **Step 1 : Écrire les tests en échec**

Les requêtes HTTP passent par un faux `HttpMessageHandler`.

`UpdateDownloaderTests` :
- `Download_ChecksumMatches_ReturnsVerifiedPath` ;
- `Download_ChecksumMismatch_ThrowsAndDeletesFile` : le fichier n'existe plus ;
- `Download_ChecksumMissing_Throws` : un `ChecksumUrl` nul refuse l'installation ;
- `Download_Interrupted_ThrowsAndDeletesPartial` ;
- `Download_SizeDiffersFromRelease_Throws`.

Le fichier `.sha256` contient `"<hex>  <nom>"`, ou l'empreinte seule, en majuscules ou en minuscules.

`SettingsBackupTests` :
- `Create_CopiesEverythingExceptLogs` ;
- `Create_TwoBackupsSameSecond_DoNotOverwrite` : un suffixe `-2` est ajouté.

`UpdatePreferencesStoreTests` :
- `Load_MissingOrCorrupt_ReturnsDefault` ;
- `Save_ThenLoad_RoundTrips`.

`UpdateServiceTests`, avec `FakeTimeProvider` et des faux pour le vérificateur, le téléchargeur et le lanceur :
- `Start_FirstCheckAfter60Seconds_ThenEvery24Hours` : aucun appel avant 60 s, un appel à 60 s, un autre à 24 h + 60 s ;
- `Check_NetworkError_StateFailed_ServiceKeepsSchedule` : l'état vaut `Failed`, le message n'est pas vide, et l'appel suivant a lieu à l'heure prévue ;
- `Check_NoStableYet_StateNoStableYet` ;
- `Install_LauncherCancelled_StateBackToAvailable_MessageCancelled` : le message vaut exactement « Mise à jour annulée : l'autorisation n'a pas été donnée. » ;
- `Install_Downgrade_WithoutConfirmation_Refused` ;
- `Install_Downgrade_BackupCreatedBeforeLaunch` : le faux lanceur constate que la copie existe déjà ;
- `Install_Downgrade_BackupFails_DoesNotLaunch` ;
- `Install_ChecksumFails_NeverLaunches` ;
- `SetPreferences_WaitForNextStable_SavesAndRechecks` ;
- `Install_ReopenSettings_MarkerCreatedBeforeLaunch`, et `ReopenSettingsMarker_ConsumeTwice_SecondFalse`.

- [ ] **Step 2 : Lancer, vérifier l'échec** — `dotnet test tests/Trofeo.Distribution.Tests` : les types sont absents.
- [ ] **Step 3 : Implémenter.**
  - `UpdateService` n'a qu'une opération à la fois : un verrou, comme `_reloadGate` dans `ServiceControlHandler`.
  - Le minuteur est créé par `time.CreateTimer`.
  - Les messages de `UpdateStatus` sont en français :
    - « Tu as la dernière version. »
    - « Aucune version stable pour l'instant. »
    - « La version {v} est disponible. »
    - « Téléchargement… {p} % »
    - « Windows va demander l'autorisation d'installer la mise à jour. »
    - « Impossible de vérifier les mises à jour : {motif} »
  - Le téléchargement se fait dans `downloadDir` (`%TEMP%\ashboard-vision-mise-a-jour`), vidé avant chaque téléchargement.
- [ ] **Step 4 : Lancer, vérifier la réussite** — `dotnet test tests/Trofeo.Distribution.Tests` : tout est vert.
- [ ] **Step 5 : Commit** — `feat(distribution): téléchargement vérifié, copie des réglages et service des mises à jour`

### Task 3 : Le nouveau nom dans le code et le mod des quotas

**Files:**
- Modify: `src/Trofeo.Core/AppPaths.cs`, avec `SettingsDir` sur `ProductIdentity.Id` et un nouveau `LegacySettingsDir`
- Modify: `src/Trofeo.Settings/Services/StartupRegistration.cs` : `ValueName = "ashboard-vision"`, avec un nouveau `LegacyValueName = "trofeo-HB-dashboard"`
- Modify: `src/Trofeo.Sensors/Pipe/HelperIdentity.cs` : `InstallDir` vaut `%ProgramFiles%\Ashboard Vision\sensors\`, avec un nouveau `LegacyInstallDir`
- Modify: `src/Trofeo.Sensors/Setup/TaskDefinition.cs` : `TaskName` vaut `\Ashboard Vision\Capteurs`, avec un nouveau `LegacyTaskName`. Description : « Assistant de capteurs d'Ashboard Vision… »
- Modify: `src/Trofeo.Sensors/Setup/HelperInstaller.cs` : `Install` arrête et supprime d'abord l'ancienne tâche et l'ancien dossier
- Modify: `src/Trofeo.Display/Program.cs`, pour l'agent HTTP `ashboard-vision/<version>`
- Move: `ClaudeHome` et `StatusLineInstaller` (désinstallation et restauration seulement) de `src/Trofeo.StatusLine/` vers `src/Trofeo.Modules.Agents/Claude/`. Retirer le projet `Trofeo.StatusLine` de la solution, de `Trofeo.Modules.Agents`, de ses tests et de `tools/Trofeo.HardwareCheck`. Le mode « installer la barre d'état » disparaît.
- Modify: `src/Trofeo.Modules.Agents/Claude/ClaudeSource.cs` : il lit les captures de `~/.claude/ashboard-vision/statusline`, puis celles de l'ancien dossier tant qu'elles ont moins de 8 jours
- Rename: `mods/trofeo-quotas` devient `mods/ashboard-quotas` (`plugin.json` : nom `ashboard-quotas`, description sur le nouveau dossier). `mods/.claude-plugin/marketplace.json` : nom `ashboard-vision`, description « Mods Claude Code d'Ashboard Vision… ». Dans `hooks/register.ts`, le dossier devient `ashboard-vision/statusline`, et son test aussi.
- Modify: `src/Trofeo.Modules.Agents/QuotaModInstaller.cs` : `PluginName = "ashboard-quotas@ashboard-vision"`, avec un nouveau `LegacyPluginName = "trofeo-quotas@trofeo-hb-dashboard"` et `LegacyMarketplace = "trofeo-hb-dashboard"`. Il gagne `Task<ActionResult> UninstallAsync(string plugin, string marketplace, CancellationToken)`.
- Modify: les textes visibles qui contiennent « trofeo-HB-dashboard » (fenêtre, icône, assistant, README des mods). Les trouver avec `grep -rn "trofeo-HB-dashboard" src mods --include=*.cs --include=*.resx --include=*.axaml --include=*.json --include=*.ts`. Ne pas toucher aux noms figés (Global Constraints).
- Test: `tests/Trofeo.Core.Tests/ProductIdentityTests.cs`, les tests existants à mettre à jour (Sensors, Agents, Settings), `mods/ashboard-quotas/hooks/register.test.ts`

**Interfaces:**
- Consumes : `ProductIdentity` (Task 1).
- Produces :
  - `AppPaths.SettingsDir` et `AppPaths.LegacySettingsDir` ;
  - `RunKeyStartup.LegacyValueName` ;
  - `HelperIdentity.LegacyInstallDir` ;
  - `TaskDefinition.LegacyTaskName` ;
  - `QuotaModInstaller.LegacyPluginName`, `LegacyMarketplace` et `UninstallAsync` ;
  - `ClaudeHome.StatusLineDir(claudeHome)`, sur le nouveau dossier, et `ClaudeHome.LegacyStatusLineDir(claudeHome)`.

- [ ] **Step 1 : Écrire les tests en échec**
  - `ProductIdentityTests.FrozenNames_KeepLegacyId` : vérifie, par réflexion ou par accès `internal`, que l'entropie DPAPI, `FreeboxSource.AppId` et `LayoutTransfer.Format` gardent l'ancienne valeur.
  - `AppPaths_SettingsDir_EndsWithAshboardVision`.
  - `HelperInstallerTests.Install_RemovesLegacyTaskAndFolderFirst` : le faux lanceur de commandes reçoit `schtasks /End /TN "\trofeo-HB-dashboard\Capteurs"` puis `/Delete … /F` **avant** la création de la nouvelle tâche, et l'ancien dossier est supprimé. Il n'y a ni échec ni message si l'ancienne tâche n'existe pas.
  - `ClaudeSourceTests.Captures_ReadFromNewThenLegacyFolder`.
  - `QuotaModInstallerTests.Uninstall_RunsPluginUninstallThenMarketplaceRemove` : les arguments sont exactement `plugin uninstall trofeo-quotas@trofeo-hb-dashboard` puis `plugin marketplace remove trofeo-hb-dashboard`.
  - Le test TypeScript du mod écrit dans `ashboard-vision/statusline`.
- [ ] **Step 2 : Lancer, vérifier l'échec** :
  - `dotnet test tests/Trofeo.Core.Tests` ;
  - `dotnet test tests/Trofeo.Sensors.Tests` ;
  - `dotnet test tests/Trofeo.Modules.Agents.Tests` ;
  - `claude plugin test mods/ashboard-quotas`, ou le lanceur de tests du mod décrit dans son README.
- [ ] **Step 3 : Implémenter** les renommages et `UninstallAsync`, sur le modèle de `InstallAsync`.
- [ ] **Step 4 : Lancer, vérifier la réussite** — les mêmes commandes, puis `dotnet build -c Debug` de la solution, sans avertissement.
- [ ] **Step 5 : Commit** — `refactor: le nouveau nom Ashboard Vision dans le code et le mod des quotas`

### Task 4 : Déménagement, préparation et nettoyage propres à l'utilisateur

**Files:**
- Create: `src/Trofeo.Distribution/LegacySettingsMove.cs`, `IRunKey.cs`, `UserSetup.cs`, `UserCleanup.cs`, `InstallOptions.cs`
- Modify: `src/Trofeo.Distribution/Trofeo.Distribution.csproj`, qui référence `Trofeo.Modules.Agents` pour `QuotaModInstaller` et `StatusLineInstaller`
- Test: `tests/Trofeo.Distribution.Tests/LegacySettingsMoveTests.cs`, `UserSetupTests.cs`, `UserCleanupTests.cs`

**Interfaces:**
- Consumes : Task 3 (anciens noms, `QuotaModInstaller`), Task 1 (`AppVersion`).
- Produces :
  - `static class LegacySettingsMove` : `static MoveResult Run(string legacyDir, string newDir)`, avec `enum MoveResult { NothingToMove, Moved, AlreadyMoved, Failed }`.
    - Si les deux dossiers existent, rien n'est déplacé (`AlreadyMoved`) et l'ancien est gardé.
    - Un échec laisse l'ancien dossier intact.
  - `interface IRunKey { string? Read(string name); void Write(string name, string value); void Delete(string name); }`.
  - `sealed record InstallOptions(bool StartWithWindows)` et `static InstallOptions? InstallOptions.TryRead(string path)`. Fichier : `{app}\options-installation.json`, écrit par l'installateur, avec la clé `demarrerAvecWindows`.
  - `sealed class UserSetup(IRunKey runKey, QuotaModInstaller mods, StatusLineInstaller statusLine, string settingsDir, string modsDir, string serviceExePath)` : `Task<UserSetupReport> RunAsync(AppVersion current, InstallOptions? options, CancellationToken ct)`. Il lit et écrit `settingsDir\installation.json` (`{ "versionPreparee": "...", "modInstalle": true|false }`).
  - `sealed record UserSetupReport(bool Ran, IReadOnlyList<string> Warnings)`.
  - `sealed class UserCleanup(IRunKey runKey, QuotaModInstaller mods)` : `Task RunAsync(string settingsDir, bool deleteSettings, CancellationToken ct)`.

- [ ] **Step 1 : Écrire les tests en échec**

`LegacySettingsMoveTests` :
- `Run_OnlyLegacy_MovesEverything` ;
- `Run_BothExist_KeepsBothAndReturnsAlreadyMoved` ;
- `Run_SourceLocked_ReturnsFailedAndLegacyIntact` : un fichier ouvert sans partage dans l'ancien dossier, sous Windows seulement ; sous Linux, un dossier en lecture seule.

`UserSetupTests`, avec un faux `IRunKey` et un faux lanceur de `claude` :
- `Run_SameVersionAlreadyPrepared_DoesNothing` ;
- `Run_LegacyRunValue_ReplacedByNewPointingToServiceExe` : l'ancienne valeur est supprimée, et la nouvelle vaut `"\"<serviceExePath>\""` ;
- `Run_NoLegacy_FirstInstall_StartWithWindowsOption_WritesRunValue` ;
- `Run_StartWithWindowsFalse_NoRunValue` ;
- `Run_LegacyModInstalled_UninstallsItAndInstallsNew` : la sortie de `claude plugin list` contient `trofeo-quotas@trofeo-hb-dashboard` ;
- `Run_ModRecordedInstalled_ReinstallsAfterUpdate` ;
- `Run_ClaudeMissing_SkipsModSilently` : un avertissement est rendu, sans exception ;
- `Run_OldStatusLineConfigured_UninstalledAndPreviousRestored` ;
- `Run_RecordsPreparedVersion`.

`UserCleanupTests` :
- `Run_RemovesRunValueAndMod` ;
- `Run_DeleteSettingsTrue_RemovesFolder_FalseKeepsIt`.

- [ ] **Step 2 : Lancer, vérifier l'échec** — `dotnet test tests/Trofeo.Distribution.Tests` : les types sont absents.
- [ ] **Step 3 : Implémenter.**
  - Ordre de `UserSetup.RunAsync` : valeur `Run`, puis barre d'état, puis mod, puis enregistrement de la version.
  - Chaque étape qui échoue ajoute un avertissement en français et laisse continuer les suivantes.
  - Le mod n'est installé que si l'ancien l'était, ou si `modInstalle` vaut vrai. Sinon, c'est l'assistant de première ouverture qui le propose, comme aujourd'hui.
- [ ] **Step 4 : Lancer, vérifier la réussite** — `dotnet test tests/Trofeo.Distribution.Tests` : tout est vert.
- [ ] **Step 5 : Commit** — `feat(distribution): déménagement des réglages, préparation et nettoyage propres à l'utilisateur`

### Task 5 : Reconnaître l'écran, dire quand il n'est pas pris en charge

**Files:**
- Modify: `src/Trofeo.Device/LyProtocol.cs`, `ScreenDevice.cs`
- Create: `src/Trofeo.Device/KnownThermalrightDevices.cs` (pur), `src/Trofeo.Device/UsbPresenceScanner.cs` (SetupAPI, Windows)
- Test: `tests/Trofeo.Device.Tests/LyProtocolTests.cs`, `ScreenDeviceTests.cs`, `KnownThermalrightDevicesTests.cs`

**Interfaces:**
- Produces :
  - `LyProtocol.SupportedModel = 65` et `static int ReadModel(ReadOnlySpan<byte> reply)`.
  - `static class KnownThermalrightDevices` : `static string? Describe(ushort vid, ushort pid)` et `static bool TryParseHardwareId(string id, out ushort vid, out ushort pid)`. La table reprend les identifiants USB de la note sur les écrans (`87CD:70DB`, `87AD:70DB`, `0402:3922`, `0416:5406`, `0416:5302`, `0418:5303`, `0418:5304`, `0416:5409`, `0416:8001`), avec la famille et des exemples de modèles.
  - `ScreenDevice` gagne un paramètre facultatif `Func<IReadOnlyList<(ushort Vid, ushort Pid)>>? scanPresentUsb`, qui vaut `UsbPresenceScanner.Scan` sous Windows.

- [ ] **Step 1 : Écrire les tests en échec**
  - `ReadModel` : octet 20 = 0, 1, 2 ou 3 → 65 ; 4 → 68 ; 5 → 69. Avec la réponse réelle d'Alex (spec, annexe A), on obtient 65.
  - `ScreenDeviceTests.Handshake_UnsupportedModel_DisconnectedWithReason` : un faux transport répond avec l'octet 20 = 4. L'état vaut `Disconnected`, et le motif est exactement « Ce modèle d'écran Thermalright (code 68) n'est pas encore pris en charge : seul le Trofeo Vision 9,16 pouces (1920 × 462) l'est pour l'instant. ». Aucune image n'est envoyée.
  - `ScreenDeviceTests.Unsupported_RetriesAtMostEvery30Seconds` (FakeTimeProvider).
  - `ScreenDeviceTests.NoScreen_KnownThermalrightPresent_ReasonNamesIt` : le faux scan rend `0416:5302`, et le motif commence par « Écran Thermalright détecté (HID type 2) ».
  - `ScreenDeviceTests.NoScreen_ScanAtMostEvery30Seconds`.
  - `KnownThermalrightDevicesTests` : `TryParseHardwareId("USB\\VID_0416&PID_5302&REV_0100")` donne `0416:5302`, et `Describe(0x1234, 0x5678)` rend `null`.
- [ ] **Step 2 : Lancer, vérifier l'échec** — `dotnet test tests/Trofeo.Device.Tests`.
- [ ] **Step 3 : Implémenter.**
  - `UsbPresenceScanner.Scan` utilise `SetupDiGetClassDevs` avec `DIGCF_ALLCLASSES | DIGCF_PRESENT` et l'énumérateur `"USB"`, puis lit `SPDRP_HARDWAREID`. Les déclarations P/Invoke vont avec celles de `WinUsbNative`.
  - Le scan n'a lieu que quand aucun écran pris en charge ne s'ouvre.
- [ ] **Step 4 : Lancer, vérifier la réussite** — `dotnet test tests/Trofeo.Device.Tests` : tout est vert.
- [ ] **Step 5 : Commit** — `feat(device): reconnaître le modèle de l'écran et nommer un écran non pris en charge`

### Task 6 : Le service branche tout, protocole 2, icône de notification

**Files:**
- Modify: `src/Trofeo.Control/Messages.cs`, `ControlServer.cs`, `ControlClient.cs`, `ControlPipe.cs` (`ProtocolVersion = 2`)
- Modify: `src/Trofeo.Service/ServiceControlHandler.cs` : il reçoit un `UpdateService?`
- Modify: `src/Trofeo.Display/Program.cs`, `TrayIcon.cs`
- Create: `src/Trofeo.Display/RegistryRunKey.cs` (`IRunKey` sur HKCU Run), `WindowsInstallerLauncher.cs`, `TaskbarTheme.cs`
- Test: `tests/Trofeo.Control.Tests/` (aller-retour des nouveaux messages), `tests/Trofeo.Service.Tests/ServiceControlHandlerTests.cs`, `tests/Trofeo.Display.Tests/TrayIconTests.cs`, `WindowsInstallerLauncherTests.cs`

**Interfaces:**
- Consumes : `UpdateService`, `UpdateStatus` et `UpdatePreferences` (Task 2) ; `LegacySettingsMove`, `UserSetup`, `UserCleanup` et `InstallOptions` (Task 4).
- Produces (dans `Trofeo.Control`, sans référence à `Trofeo.Distribution`) :
  - `enum LinkUpdateState { Unknown, Checking, UpToDate, NoStableYet, Available, Downloading, Installing, Failed }` et `enum LinkUpdateChannel { Stable, Beta }` ;
  - `sealed record UpdateInfo(LinkUpdateState State, string? Version, bool IsDowngrade, int? Percent, string? Message, LinkUpdateChannel Channel, bool WaitForNextStable)` ;
  - dans `IControlHandler` :
    - `UpdateInfo GetUpdate()` ;
    - `Task CheckUpdatesAsync(CancellationToken)` ;
    - `Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable, CancellationToken)` ;
    - `Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade, CancellationToken)` ;
  - les méthodes du même nom dans `ControlClient`.
- Produces (dans `Trofeo.Display`) : la ligne de commande `Trofeo.Display.exe --desinstaller-utilisateur`.

- [ ] **Step 1 : Écrire les tests en échec**
  - L'aller-retour des quatre nouvelles requêtes dans `Trofeo.Control.Tests`, comme pour `RunActionAsync`.
  - `ServiceControlHandlerTests.GetUpdate_WithoutUpdateService_ReturnsUnknown`.
  - `ServiceControlHandlerTests.InstallUpdate_ForwardsConfirmDowngrade`.
  - `TrayIconTests.Tooltip_UpdateAvailable_MentionsVersion` : l'info-bulle se termine par « Mise à jour {v} disponible ». Garder la limite de 127 caractères, déjà testée.
  - `TrayIconTests.Menu_UpdateAvailable_ShowsInstallItem` : l'élément « Installer la mise à jour {v} » est placé avant « Ouvrir les réglages ».
  - `TaskbarThemeTests.IconFor_LightTaskbar_UsesLightIcon` : fonction pure `static string IconResourceFor(bool lightTaskbar)`, qui rend `tray-clair.ico` ou `tray-sombre.ico`.
  - `WindowsInstallerLauncherTests.Arguments_AreSilent` : les arguments valent exactement `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`. Le code Win32 1223 donne `Cancelled`.
- [ ] **Step 2 : Lancer, vérifier l'échec** :
  - `dotnet test tests/Trofeo.Control.Tests` ;
  - `dotnet test tests/Trofeo.Service.Tests` ;
  - `dotnet test tests/Trofeo.Display.Tests`, sous Windows.
- [ ] **Step 3 : Implémenter**

`Program.Main`, dans l'ordre :
1. le mutex ;
2. `--desinstaller-utilisateur` : il appelle `UserCleanup` avec la question `MessageBox` « Garder tes réglages (disposition, connexions) ? » (Oui / Non), puis quitte. En désinstallation silencieuse, sans fenêtre, les réglages sont gardés. L'installateur passe `--silencieux` ;
3. `LegacySettingsMove.Run(AppPaths.LegacySettingsDir, AppPaths.SettingsDir)`, **avant** `InitializeLog`. Le résultat est journalisé juste après ;
4. `InitializeLog` et le reste du démarrage actuel ;
5. `UserSetup.RunAsync`, en tâche de fond qui ne bloque pas l'écran. Ses avertissements sont journalisés et rejoignent les avis du service ;
6. `UpdateService.Start()` ;
7. si `ReopenSettingsMarker.Consume(AppPaths.SettingsDir)` rend vrai, ouvrir la fenêtre de réglage comme le fait « Ouvrir les réglages ».

`InstallUpdateAsync`, reçu de la fenêtre, passe `reopenSettings: true`. Lancé depuis le menu de l'icône, il passe `false`.

Le reste du branchement :
- le service lit la version installée dans son `AssemblyInformationalVersion`, sans le `+hash`, comme `ServiceControlHandler.ReadVersion` ;
- `TrayIcon` reçoit l'état de mise à jour et le thème de la barre (`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`, `SystemUsesLightTheme` ; une valeur absente vaut sombre). Il change d'icône sur `SystemEvents.UserPreferenceChanged` ;
- les icônes `tray-clair.ico` et `tray-sombre.ico` viennent de `assets/brand/` (Task 8), en ressources incorporées. Tant que la Task 8 n'est pas faite, on garde `SystemIcons.Application` ;
- `WindowsInstallerLauncher` lance l'installateur avec `UseShellExecute = true`, `Verb = "runas"` et les arguments ci-dessus.
- [ ] **Step 4 : Lancer, vérifier la réussite** — les mêmes commandes, puis la solution en Debug, sans avertissement.
- [ ] **Step 5 : Commit** — `feat(service): mises à jour, déménagement des réglages et icône qui suit le thème`

### Task 7 : La fenêtre : mises à jour dans Paramètres

**Files:**
- Modify: `src/Trofeo.Settings/ViewModels/SettingsTabViewModel.cs`, la vue de l'onglet Paramètres, `Strings.resx` et `Texts.cs`
- Create: `src/Trofeo.Settings/ViewModels/DowngradeDialogViewModel.cs` et sa vue, sur le modèle des confirmations existantes de la fenêtre
- Test: `tests/Trofeo.Settings.Tests/SettingsTabUpdateTests.cs` (xUnit v3, Avalonia.Headless)

**Interfaces:**
- Consumes : `ControlClient.GetUpdate`, `CheckUpdatesAsync`, `SetUpdatePreferencesAsync` et `InstallUpdateAsync`, ainsi que `UpdateInfo` (Task 6).

Une section « Mises à jour » est ajoutée sous la ligne « Version … » existante. Elle contient :
- un choix « Version », avec les options « Stable » et « Bêta » ;
- une ligne d'état, qui reprend le `Message` de `UpdateInfo` ;
- le bouton « Rechercher maintenant » ;
- le bouton « Installer la version {v} », visible seulement quand l'état vaut `Available`.

L'état est relu au même rythme que l'état du service.

- [ ] **Step 1 : Écrire les tests en échec**
  - `ChannelBeta_CallsSetPreferencesBeta`.
  - `ChannelStable_FromBeta_DowngradeOffered_OpensDialog`. Le texte de la boîte vaut exactement : « Tu repasses à la version stable {v}, plus ancienne que ta bêta. Les réglages enregistrés par la bêta pourraient mal se relire : une copie en est faite avant. » La boîte contient la case « Attendre la prochaine version stable » et les boutons « Installer la version {v} » et « Annuler ».
  - `DowngradeDialog_WaitChecked_SetsPreferencesWaitTrue_DoesNotInstall`.
  - `DowngradeDialog_Confirmed_InstallsWithConfirmDowngrade`.
  - `InstallButton_HiddenUnlessAvailable`.
  - `InstallCancelledByWindows_ShowsCancelledMessage` : le message est « Mise à jour annulée : l'autorisation n'a pas été donnée. ».
  - `NoStableYet_ShowsMessage` : le message est « Aucune version stable pour l'instant. ».
- [ ] **Step 2 : Lancer, vérifier l'échec** — `dotnet test tests/Trofeo.Settings.Tests`.
- [ ] **Step 3 : Implémenter** en MVVM, comme le reste de l'onglet (CommunityToolkit.Mvvm). Les textes vont dans `Strings.resx`.
- [ ] **Step 4 : Lancer, vérifier la réussite** :
  - `dotnet test tests/Trofeo.Settings.Tests` est vert ;
  - mettre à jour les aperçus de la fenêtre (`dotnet run --project tools/Trofeo.SettingsPreview -- --out apercus-reglages`) et regarder l'onglet Paramètres.
- [ ] **Step 5 : Commit** — `feat(reglages): choisir Stable ou Bêta et installer une mise à jour`

### Task 8 : Images de la marque (icônes, installateur, nom en tracés)

**Files:**
- Create: `tools/Trofeo.BrandAssets/` (console, SkiaSharp 3.119.4 et Svg.Skia, dans une version compatible avec SkiaSharp 3.119), `IcoWriter.cs`, `WordmarkBuilder.cs`, `Program.cs`
- Create: `tests/Trofeo.BrandAssets.Tests/IcoWriterTests.cs`, `WordmarkBuilderTests.cs`
- Create (produits par l'outil, puis commités) dans `assets/brand/` :
  - `ashboard-vision.ico` : tailles 16, 20, 24, 32, 40, 48, 64 et 256. Jusqu'à 24, à partir de `ashboard-symbole-petit-sombre.svg` ; au-dessus, de `ashboard-symbole-icone.svg` ;
  - `tray-sombre.ico` et `tray-clair.ico` : tailles 16, 20, 24 et 32, à partir des deux coupes `petit` ;
  - `ashboard-vision-horizontal-sombre.svg` et `ashboard-vision-horizontal-clair.svg` : le symbole et le nom en tracés ;
  - `logo-256.png` et `logo-512.png` ;
  - pour l'assistant d'installation, en BMP 24 bits :
    - `assistant-clair.bmp` et `assistant-sombre.bmp`, en 164×314 et 328×628 ;
    - `petit-logo-clair.bmp` et `petit-logo-sombre.bmp`, en 55×55 et 110×110.
- Modify: `src/Trofeo.Display/Trofeo.Display.csproj`, `src/Trofeo.Settings/Trofeo.Settings.csproj` et `src/Trofeo.SensorHelper/Trofeo.SensorHelper.csproj` (`<ApplicationIcon>`), ainsi que l'icône de la fenêtre Avalonia
- Modify: l'en-tête de la fenêtre de réglage : le symbole devant « Ashboard Vision », en version claire ou sombre selon le thème de la fenêtre

**Interfaces:**
- Produces : `static byte[] IcoWriter.Write(IReadOnlyList<(int Size, byte[] Png)> images)` et `static string WordmarkBuilder.PathData(string text, string fontPath, float size)`.
- La ligne de commande : `dotnet run --project tools/Trofeo.BrandAssets -- --logo docs/superpowers/logo --font src/Trofeo.Core/Assets/Fonts/Manrope-Medium.ttf --out assets/brand`.

- [ ] **Step 1 : Écrire les tests en échec**
  - `IcoWriterTests.Write_HeaderAndDirectory` : la signature vaut `00 00 01 00`, le nombre d'images est juste, chaque entrée a sa taille (256 s'écrit 0) et son décalage pointe vers les données PNG.
  - `WordmarkBuilderTests.PathData_NotEmpty_NoTextElement` : le tracé de « Ashboard » n'est pas vide et commence par `M`.
- [ ] **Step 2 : Lancer, vérifier l'échec** — `dotnet test tests/Trofeo.BrandAssets.Tests`.
- [ ] **Step 3 : Implémenter l'outil.**
  - Le nom vient de `SKFont.GetTextPath`, puis `SKPath.ToSvgPathData()`.
  - « VISION » est à 42 % de la taille, avec un espacement de 0,28 em et une opacité de 62 %, comme sur la maquette.
  - Lancer l'outil, regarder chaque image produite (les ouvrir), puis commiter `assets/brand/`.
- [ ] **Step 4 : Lancer, vérifier la réussite** :
  - `dotnet test tests/Trofeo.BrandAssets.Tests` est vert ;
  - `dotnet build -c Debug` est vert ;
  - l'exe du service a la nouvelle icône dans l'explorateur, à vérifier sur Windows.
- [ ] **Step 5 : Commit** — `feat(marque): icônes, images de l'installateur et nom en tracés`

### Task 9 : L'installateur

**Files:**
- Create: `installer/ashboard-vision.iss`, `installer/build.ps1`, `installer/pawnio.json`, `installer/README.md`
- Modify: `.gitignore` (`installer/out/`, `installer/publish/`)

**Interfaces:**
- Consumes :
  - `Trofeo.SensorHelper.exe install [--pawnio <setup>]`, qui retire aussi l'ancienne tâche (Task 3) ;
  - `Trofeo.Display.exe --desinstaller-utilisateur [--silencieux]` (Task 6) ;
  - `options-installation.json` (Task 4) ;
  - `assets/brand/` (Task 8).
- Produces : `pwsh installer/build.ps1 -Version <semver> [-SansPawnIo]`, qui écrit `installer/out/Ashboard-Vision-Setup-<v>.exe` et son `.sha256`.

- [ ] **Step 1 : Vérifier deux points avant d'écrire, et les noter dans `installer/README.md` avec leurs sources.**
  1. **La licence de PawnIO permet-elle de redistribuer son installeur ?**
     - Si oui, `pawnio.json` donne l'adresse officielle, la version et l'empreinte SHA-256. `build.ps1` le télécharge et vérifie l'empreinte.
     - Si non, l'installateur le télécharge pendant l'installation, avec la même vérification. **Prévenir Alex** : ce n'est plus « tout embarqué ».
  2. **Inno Setup sait-il suivre le thème de Windows ?** Chercher le nom exact du réglage de `WizardStyle` (thème dynamique) et des images pour le mode sombre, avec la version minimale. Si ce n'est pas possible, **le dire à Alex** avant de continuer.
- [ ] **Step 2 : Écrire `build.ps1`.**
  - Il publie `Trofeo.Display`, `Trofeo.Settings` et `Trofeo.SensorHelper` en `-c Release -r win-x64 --self-contained` dans le même dossier `installer/publish/`, avec `-p:Version=<v>`.
  - Il y copie `mods/`, les licences tierces et `LICENSE`.
  - Il télécharge et vérifie PawnIO, puis lance `iscc` avec `/DAppVersion=<v>`.
  - Enfin, il écrit le `.sha256` au format `"<hex>  <nom>"`.
- [ ] **Step 3 : Écrire `ashboard-vision.iss`**

En-tête :
- `AppId` : un GUID fixe, généré une fois et commenté « ne jamais changer » ;
- `AppName = Ashboard Vision`, `AppPublisher = Ashewos`, `AppVersion = {#AppVersion}` ;
- `DefaultDirName = {autopf}\Ashboard Vision` ;
- `PrivilegesRequired = admin`, `ArchitecturesAllowed` et `ArchitecturesInstallIn64BitMode` à `x64compatible` ;
- langue française (`compiler:Languages\French.isl`) ;
- `LicenseFile = LICENSE` ;
- `SetupIconFile` et `UninstallDisplayIcon` sur `ashboard-vision.ico` ;
- `WizardStyle` et les images claires et sombres, selon le Step 1 ;
- `OutputBaseFilename = Ashboard-Vision-Setup-{#AppVersion}`.

Les options (`[Tasks]`) :
- `bureau` : « Créer un raccourci sur le bureau », décochée ;
- `demarrage` : « Démarrer Ashboard Vision avec Windows », cochée ;
- `pawnio` : « Installer le pilote des températures (PawnIO) », cochée.

Avant l'installation (`[Code]`, dans `PrepareToInstall`) :
- arrêter `Trofeo.Display.exe`, `Trofeo.Settings.exe` et l'ancien `Trofeo.SensorHelper.exe` (`taskkill /F /IM …`), y compris ceux lancés depuis le dépôt ;
- écrire `{app}\options-installation.json` d'après la tâche `demarrage`.

Après l'installation (`[Run]`) :
1. `Trofeo.SensorHelper.exe install` (avec `--pawnio` si la tâche est cochée), masqué, en attendant la fin ;
2. le lancement du service **en tant qu'utilisateur d'origine** (`runasoriginaluser`) : une entrée `postinstall` « Lancer Ashboard Vision », plus une entrée sans case pour le mode silencieux, qui relance après une mise à jour.

À la désinstallation (`[UninstallRun]`) :
1. `Trofeo.Display.exe --desinstaller-utilisateur`, avec `--silencieux` si la désinstallation est silencieuse ;
2. `Trofeo.SensorHelper.exe uninstall`.

Avant tout, la désinstallation arrête le service et la fenêtre.
- [ ] **Step 4 : Vérifier sur Windows** (PC d'Alex, ou en essai sur GitHub à la Task 10)
  - `pwsh installer/build.ps1 -Version 1.0.0-beta.1` produit le `Setup.exe` et son `.sha256`.
  - Dans le **Bac à sable Windows** :
    - les pages sont en français, avec le thème suivi ;
    - le logiciel apparaît dans « Applications installées » avec l'icône et l'éditeur « Ashewos » ;
    - sans écran, le service affiche « déconnecté » sans planter ;
    - la désinstallation ne laisse ni tâche, ni valeur `Run`, ni `Program Files\Ashboard Vision`. Le dossier `%AppData%\ashboard-vision` reste ou disparaît selon la réponse.
- [ ] **Step 5 : Commit** — `feat(installateur): assistant Inno Setup, construction et désinstallation propre`

### Task 10 : Publier les versions depuis GitHub

**Files:**
- Create: `.github/workflows/release.yml`, `CHANGELOG.md`
- Modify: `Directory.Build.props` : `<Version>1.0.0-beta.1</Version>`

**Interfaces:**
- Consumes : `installer/build.ps1` (Task 9).
- Produces :
  - une version GitHub par étiquette `v<semver>`, avec `Ashboard-Vision-Setup-<v>.exe` et son `.sha256`, marquée « préversion » quand `-beta.` est dans le numéro ;
  - un lancement manuel « essai » qui construit sans rien publier.

- [ ] **Step 1 : Écrire `release.yml`**

Déclencheurs :
- `push: tags: ['v*']` ;
- `workflow_dispatch` avec une entrée `essai: boolean`.

Le travail tourne sur `windows-latest`, avec la permission `contents: write`. Ses étapes :
1. `actions/checkout`, avec `fetch-depth: 0` ;
2. `actions/setup-dotnet` en `10.0.x` ;
3. vérifier que le numéro de l'étiquette est celui de `Directory.Build.props` ;
4. vérifier la branche : une stable doit être contenue dans `origin/main`, une bêta dans `origin/Canary` (`git merge-base --is-ancestor`) ;
5. `dotnet test -c Release` sur toute la solution ;
6. installer Inno Setup, dans la version minimale de la Task 9 ;
7. `pwsh installer/build.ps1 -Version <v>` ;
8. extraire la section `## <v>` de `CHANGELOG.md` ;
9. `gh release create v<v> <setup> <sha256> --title "Ashboard Vision <v>" --notes-file …`, avec `--prerelease` pour une bêta.

En mode « essai », les étapes 3, 4, 8 et 9 sont sautées, et le `Setup.exe` est déposé comme artefact du lancement.

Si un test échoue sur GitHub uniquement pour une raison de machine (aucun écran, aucun capteur), le marquer `[Trait("Machine", "Physique")]`, l'exclure dans la recette (`--filter "Machine!=Physique"`) et écrire la raison à côté. Ne jamais l'exclure pour masquer un vrai défaut.
- [ ] **Step 2 : Écrire `CHANGELOG.md`**, en français, avec une section `## 1.0.0-beta.1` rédigée à partir des commits du plan 6 : le nouveau nom, l'installateur, les mises à jour, le logo et l'écran reconnu.
- [ ] **Step 3 : Vérifier** — `act` n'est pas requis. Avec l'accord d'Alex pour pousser `Canary`, lancer la recette en mode « essai » (`gh workflow run release.yml -f essai=true --ref Canary`). Elle est verte et l'artefact contient le `Setup.exe`.
- [ ] **Step 4 : Commit** — `ci: publier les versions d'Ashboard Vision depuis GitHub`

### Task 11 : Page GitHub, image d'accueil, documentation

**Files:**
- Create: `tools/Trofeo.ReadmeHero/` : il reprend le rendu de `tools/Trofeo.ThemePreview` en référençant son projet ou en en extrayant la partie commune
- Create: `tests/Trofeo.BrandAssets.Tests/HeroTimelineTests.cs`
- Create: `docs/images/accueil.webp` (repli `accueil.gif`), `docs/images/modules/*.png`, `docs/utilisation-avancee.md`
- Modify: `README.md`, `docs/superpowers/specs/2026-10-24-trofeo-dashboard-design.md` (sections 9.1, 9.3 et 11), `docs/superpowers/plans/2026-10-24-plan-1-fondations.md` (ligne du plan 6), `CLAUDE.md`

**Interfaces:**
- Produces :
  - `dotnet run --project tools/Trofeo.ReadmeHero -- --name "Ashboard Vision" --tagline "Le tableau de bord de ton écran Thermalright Trofeo Vision." --logo assets/brand/ashboard-vision-horizontal-sombre.svg --out apercus-accueil`, qui écrit les images numérotées et `encode.txt` (les commandes ffmpeg) ;
  - `static (int Theme, double Progress, double Sheen) HeroTimeline.At(double seconds)`.

- [ ] **Step 1 : Écrire le test en échec** `HeroTimelineTests`, sur un cycle de 12 s à 4 s par thème :
  - `At(0)`, `At(4)` et `At(8)` donnent les thèmes 0, 1 et 2 ;
  - `At(12)` revient au thème 0 ;
  - `Progress` va de 0 à 1 sur chaque thème.
- [ ] **Step 2 : Lancer, vérifier l'échec** — `dotnet test tests/Trofeo.BrandAssets.Tests`.
- [ ] **Step 3 : Implémenter l'outil d'après la maquette `docs/superpowers/maquettes/readme-accueil-v4.html` :**
  - fond noir ;
  - écran incliné à 10° (matrice de perspective SkiaSharp) ;
  - lueur Ambilight : copie floue (`SKImageFilter.CreateBlur`, environ 38 px à 1280 de large), saturée et agrandie (×1,18 en largeur, ×1,9 en hauteur) ;
  - glissement et flou à chaque changement, reflet qui balaie la vitre, nom du thème, barre de progression, légère oscillation (±2,5°) ;
  - le logo et le nom en haut ;
  - 1280 px de large, 12 images par seconde.

  Encoder avec ffmpeg en WebP animé : boucle infinie, qualité réglée pour **5 Mo au plus**. Si GitHub n'affiche pas le WebP animé, se replier sur un GIF par palette (`palettegen` / `paletteuse`). Montrer le résultat à Alex avant de commiter.
- [ ] **Step 4 : Écrire le README**, dans l'ordre de la conception (5.6) :
  1. l'image d'accueil, les badges shields.io (version, préversion, Windows 10 | 11, licence GPL-3.0, téléchargements) et le bouton « Télécharger pour Windows » vers la dernière version ;
  2. ce que fait le logiciel ;
  3. les modules, avec une capture chacun (outils d'aperçu existants) ;
  4. l'installation, avec SmartScreen. Les images `docs/images/smartscreen-1.png` et `-2.png` seront prises à la Task 12 : le README les référence déjà ;
  5. les versions Stable et Bêta ;
  6. les écrans compatibles ;
  7. la foire aux questions, avec la limite « autre compte administrateur » ;
  8. les crédits, repris tels quels ;
  9. la licence et la non-affiliation.

  Le reste du README actuel part dans `docs/utilisation-avancee.md`.
- [ ] **Step 5 : Mettre à jour la spec d'origine** (9.1, 9.3, 11 : Inno Setup, plus de Velopack, Stable / Bêta, le nouveau nom), la ligne du plan 6 dans la feuille de route et `CLAUDE.md` (nouveau nom, installateur, commandes de construction).
- [ ] **Step 6 : Lancer** `dotnet test tests/Trofeo.BrandAssets.Tests` (vert) et vérifier que chaque lien relatif du README pointe vers un fichier existant.
- [ ] **Step 7 : Commit** — `docs: page GitHub d'Ashboard Vision, image d'accueil et documentation`

### Task 12 (PC d'Alex) : Renommer, publier la première bêta, vérifier, bilan

Chaque action vers GitHub attend l'accord d'Alex, donné pour cette action-là : pousser, renommer le dépôt, poser une étiquette, publier.

- [ ] **Step 1 : Toute la solution en Debug sous Windows** : `dotnet test` est vert, `Display.Tests` compris.
- [ ] **Step 2 : Renommer le dépôt** en `ashboard-vision`, avec l'accord d'Alex (`gh repo rename ashboard-vision`). Vérifier la redirection de l'ancienne adresse et mettre à jour `origin`.
- [ ] **Step 3 : Publier `v1.0.0-beta.1`** depuis `Canary`, avec l'accord d'Alex : pousser, puis poser l'étiquette. La recette est verte, et la préversion porte le `Setup.exe` et son `.sha256`.
- [ ] **Step 4 : Avec Alex et l'écran.** Il faut d'abord fermer le service lancé depuis le dépôt ; l'installateur doit de toute façon l'arrêter.
  - installer la bêta depuis GitHub, et prendre les captures SmartScreen pour le README ;
  - vérifier le déménagement :
    - la disposition, la Freebox **toujours autorisée** et la clé UniFi lisible ;
    - l'ancienne valeur `Run`, l'ancienne tâche et l'ancien mod remplacés ;
  - publier une `v1.0.0-beta.2` (accord d'Alex), puis la mise à jour en un clic : autorisation administrateur, relance, mod réinstallé ;
  - passer en Stable avant toute stable : « Aucune version stable pour l'instant. » ;
  - vérifier le thème de l'icône (clair et sombre) et le thème de l'installateur ;
  - désinstaller en gardant les réglages, réinstaller, puis désinstaller en les effaçant ;
  - démarrer Windows : l'écran s'allume.
- [ ] **Step 5 : Mesurer la sobriété** comme aux bilans 4 et 5 : service seul, fenêtre fermée, avant et après une vérification de mise à jour.
- [ ] **Step 6 : Écrire `docs/superpowers/plans/<date>-plan-6-bilan.md`** :
  - état et commits ;
  - nombre de tests ;
  - ce qui a été vérifié ;
  - mesures ;
  - décisions ;
  - reste à faire, dont la `1.0.0` stable dans `main` quand Alex le décide ;
  - idées pour la suite : autres écrans, plusieurs écrans.

  Mettre aussi à jour `CLAUDE.md`. Commit `docs: bilan du plan 6`.
