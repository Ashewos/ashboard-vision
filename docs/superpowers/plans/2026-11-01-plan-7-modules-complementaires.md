# Plan 7 — Modules complémentaires et SDK : plan de construction

> **Pour les agents :** sous-compétence REQUISE : superpowers:subagent-driven-development (choisi par Alex : implémenteurs et relecteurs sur Sonnet, effort élevé). Les étapes utilisent des cases (`- [ ]`).

**But :** permettre à n'importe qui d'écrire un module complémentaire en C#, qui tourne enfermé dans son propre processus, avec des permissions validées par l'utilisateur, installé depuis un fichier `.avmod`, testé par un banc d'essai.

**Architecture :** le service voit chaque module complémentaire comme un `IModule` ordinaire (`RemoteModule`), si bien que `DisplayLoop` et `FrameComposer` ne changent pas. Derrière, un superviseur lance par type de module un processus `Trofeo.ModuleHost.exe` enfermé dans un AppContainer et un job object, lui parle par deux tubes anonymes (messages JSON) et reçoit ses images par une mémoire partagée à deux tampons. Le SDK public (`AshboardVision.Sdk`) donne au créateur une classe de base `CommunityModule` ; le banc d'essai (`ashboard-module check|pack`) lance le vrai hôte.

**Technologies :** .NET 10, C#, SkiaSharp 3.119.4, xUnit 2, Avalonia (fenêtre existante), API Win32 (AppContainer, job objects, `CreateProcess` étendu, `DuplicateHandle`, `MapViewOfFile`) par P/Invoke, Inno Setup 7.1.1.

**Conception :** `docs/superpowers/specs/2026-11-01-plan-7-modules-complementaires-design.md` (elle prime sur ce plan en cas de désaccord). Spec d'origine : `docs/superpowers/specs/2026-10-24-trofeo-dashboard-design.md`.

## Contraintes globales

- Windows 10 et 11, 64 bits ; tout marche **sans droits d'administrateur** (le service tourne avec les droits de l'utilisateur).
- `Directory.Build.props` : `TreatWarningsAsErrors`, `Nullable`, .NET 10 ; projets Windows en `net10.0-windows10.0.19041.0` comme les autres.
- Textes visibles en français, qui tutoient ; documentation du SDK en français.
- Commits : adresse `1345997+Ashewos@users.noreply.github.com`, fin de message `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` ; travail sur une branche partie de `Canary` ; **rien de poussé ni publié sans l'accord d'Alex**.
- Construire et tester **en Debug** sur le PC d'Alex (jamais `-c Release` : il fait tourner la version installée, mais la règle reste).
- **Coût nul sans module complémentaire** : aucun processus, aucun minuteur en plus ; le service reste à environ 0,3 % de processeur.
- **Aucun code d'un module non validé ne tourne**, même enfermé.
- Les six modules d'origine restent dans le service, inchangés.
- Damier : 1920×462, 16 colonnes × 4 lignes (`GridGeometry`).

## Décisions qui valent pour tout le plan

- **Identifiant d'un module** : `^[a-z0-9]+(-[a-z0-9]+)*\.[a-z0-9]+(-[a-z0-9]+)*$`, 40 caractères au plus. Le point obligatoire le distingue des types d'origine (`clock`, `monitoring`…), qui n'en ont pas. Il sert de `TypeId` dans la disposition et de portée du coffre à secrets (`DpapiSecretVault.Scope(id)`).
- **Permissions**, dans le manifeste : `reseau-local`, `internet`, `ce-pc`, `capteurs`, `musique` (enum `ModulePermission { LocalNetwork, Internet, ThisComputer, Sensors, NowPlaying }`). Pour `ce-pc`, aucun port n'est écrit dans le manifeste : le service relaie vers `127.0.0.1` seulement les ports **saisis par l'utilisateur dans les champs `port` des réglages de la case** qui demande la connexion, 16 connexions simultanées au plus par module. Un module qui déclare `ce-pc` doit avoir au moins un champ `port` dans son schéma.
- **Pas de signature par clé** (écartée par Alex le 01/11/2026). L'intégrité hors ligne repose sur l'empreinte du contenu enregistrée à l'installation et sur le verrou d'exécution.
- **Verrou d'exécution** : avant de lancer un hôte, le service ouvre chaque fichier du module avec `FileShare.Read` (ni écriture ni suppression), calcule l'empreinte sur ces fichiers ouverts, la compare à celle de l'installation, et les garde ouverts jusqu'à la fin de l'hôte.
- **Nouveaux champs de réglage** (dans `Trofeo.ModuleKit`, donc pour tous les modules) : `NumberField(Key, Label, int Min, int Max, int Default)` (discriminant `number`) et `PortField(Key, Label, int Default)` (discriminant `port`, 1 à 65535).
- **SDK** : jamais sur nuget.org ; paquets `.nupkg` joints aux versions GitHub et embarqués par l'installateur (tâche 22).
- **Version du SDK parlée par le logiciel** : `1.0` au départ ; versions majeures acceptées : `{1}`.
- **AppContainer** : nom `ashboard-vision.mod.<id>` ; capacités `privateNetworkClientServer` (`reseau-local`) et `internetClient` (`internet`).
- **Job object** : 512 Mio de mémoire par processus (`JOB_OBJECT_LIMIT_PROCESS_MEMORY`), 25 % du processeur en plafond dur (`CpuRate = 2500`, `HARD_CAP`), `ActiveProcessLimit = 1` (le module ne peut lancer aucun programme), `KILL_ON_JOB_CLOSE`, `DIE_ON_UNHANDLED_EXCEPTION`.
- **Pannes** : relance après 1, 2, 4, 8, 16, 32 puis 60 s ; au 3ᵉ arrêt en 10 minutes, le module est **mis de côté** (enregistré dans la liste scellée).
- **Délai de réponse** : une case qui n'a reçu aucune image 10 s après son ouverture (ou après une demande de dessin) affiche « Le module ne répond pas ».
- **Repère de consommation élevée** : plus de 2 % du processeur de la machine ou plus de 150 Mio de mémoire privée, en moyenne sur 60 s.
- **Fichiers** : modules installés dans `<dossier des modules>\<id>\<version>\` ; dossier par défaut `Documents\Ashboard Vision\Modules` ; liste scellée `%AppData%\ashboard-vision\modules.json` et sa clé `%AppData%\ashboard-vision\modules.key`.
- **Nom des fichiers publiés par un créateur** : `<id>-<version>.avmod` et `<id>-<version>.avmod.sha256`, joints à une version GitHub dont l'étiquette est `v<version>`.

## Points de vigilance pour la relecture

Les cinq cas que la conception implique sans qu'une tâche ne les vise d'elle-même ; chacun a son test dans la tâche indiquée.

1. **Un `.avmod` piégé** (chemins `..\`, chemins absolus, archive de plusieurs Go, milliers d'entrées) : refusé avant toute écriture sur le disque (tâche 2).
2. **L'hôte qui meurt au milieu d'une image** ou un tube coupé pendant que le service dessine : aucune exception dans la boucle d'affichage, la case garde sa dernière image puis passe en erreur (tâche 8).
3. **Une disposition qui cite un module désinstallé ou pas encore validé** : la case affiche « À valider dans la fenêtre de réglage » (présent mais non validé) ou disparaît avec l'avertissement habituel (absent), sans faire échouer le rechargement (tâche 8).
4. **Une permission retirée pendant que le module tourne** : les capacités d'un AppContainer sont fixées au lancement, donc l'hôte est relancé avec les nouvelles (tâche 9).
5. **Un module qui écrit ses images plus vite que le service ne les lit** : jamais de tampon écrasé pendant sa copie (accusé de réception `frameDone`, tâche 7).

---

# Partie A — le moteur et la sécurité

### Tâche 1 : manifeste, versions et compatibilité

**Fichiers :**
- Créer : `src/AshboardVision.Sdk/AshboardVision.Sdk.csproj` (net10.0 ; pour l'instant le seul fichier `ModulePermission.cs`, le reste du SDK vient à la tâche 7)
- Créer : `src/Trofeo.Extensions/Trofeo.Extensions.csproj` (net10.0-windows10.0.19041.0 ; références : `AshboardVision.Sdk`, `Trofeo.ModuleKit`, `Trofeo.Core`, `Trofeo.Distribution`), `src/Trofeo.Extensions/Manifest/ModuleManifest.cs`, `ManifestReader.cs`, `SdkVersion.cs`, `PermissionTexts.cs`, `Compatibility.cs`
- Test : `tests/Trofeo.Extensions.Tests/` (nouveau projet ; les trois projets ajoutés à `trofeo-HB-dashboard.sln`)

**Interfaces :**
- Produit :
  - `public readonly record struct SdkVersion(int Major, int Minor) : IComparable<SdkVersion>` avec `static bool TryParse(string? text, out SdkVersion version)` (forme `M.m` seulement) et `ToString()` → `"1.0"`.
  - Dans `AshboardVision.Sdk` (espace `AshboardVision.Sdk`) : `public enum ModulePermission { LocalNetwork, Internet, ThisComputer, Sensors, NowPlaying }` et `public static class ModulePermissions { string ToManifest(ModulePermission p); bool TryParse(string text, out ModulePermission p); }`.
  - Dans `Trofeo.Extensions` : `public static class PermissionTexts { string Label(ModulePermission p); string Explanation(ModulePermission p); }` — libellés : « Réseau local », « Internet », « Cet ordinateur (ports choisis dans les réglages du module) », « Capteurs », « Musique en cours » ; explications reprises du tableau 5.2 de la conception.
  - Dans `Trofeo.ModuleKit/Settings/SettingsSchema.cs` : `NumberField` et `PortField` (section « Décisions »), avec leurs `JsonDerivedType`.
  - `public sealed record ModuleManifest(string Id, string Name, string Description, string Author, string Version, SdkVersion Sdk, string Entry, IReadOnlyList<ModulePermission> Permissions, string? Repository, GridSize MinSize, IReadOnlyDictionary<SizePreset, GridSize> Presets, SettingsSchema Settings, string? Icon)` et `ModuleDescriptor ToDescriptor()`.
  - `public static class ManifestReader { ManifestReadResult Read(string json); }` ; `public sealed record ManifestReadResult(ModuleManifest? Manifest, IReadOnlyList<string> Errors)`.
  - `public enum CompatibilityKind { Compatible, NeedsNewerApp, TooOld }` ; `public static class Compatibility { SdkVersion Current { get; } /* 1.0 */ IReadOnlySet<int> SupportedMajors { get; } /* {1} */ CompatibilityKind Check(SdkVersion required); string Message(CompatibilityKind kind); }`.

Format JSON du manifeste (noms exacts) : `id`, `name`, `description`, `author`, `version` (SemVer, `AppVersion.TryParse` de `Trofeo.Distribution`), `sdk`, `entry` (nom d'une DLL à la racine, sans dossier), `permissions` (tableau), `repository` (facultatif, `propriétaire/dépôt`), `minSize` `{columns, rows}`, `presets` `{compact, ideal, large}` (chacun facultatif), `settings` (tableau de champs au format JSON de `SettingsSchema`, discriminant `kind`), `icon` (facultatif, PNG à la racine). Limites : `name` ≤ 40 caractères, `author` ≤ 40, `description` ≤ 300 ; tailles dans le damier (1 à 16 colonnes, 1 à 4 lignes).

- [ ] **Étape 1 : tests qui échouent** dans `ManifestReaderTests` et `CompatibilityTests` :
  - `Read_ValidManifest_ReturnsAllFields` (un manifeste complet → chaque champ relu, `Permissions == [LocalNetwork, Internet]`, `Settings.Fields.Count == 2`).
  - `Read_IdWithoutDot_IsRefused` (`"clock"` → erreur « L'identifiant doit avoir la forme auteur.module »), `Read_IdWithPathCharacters_IsRefused` (`"a..b"`, `"a/b.c"`, `"A.b"`), `Read_IdTooLong_IsRefused` (41 caractères).
  - `Read_UnknownPermission_IsRefused` (`"fichiers"` → erreur qui la nomme), `Read_ThisComputerWithoutPortField_IsRefused` (« Un module qui demande "Cet ordinateur" doit proposer un champ port dans ses réglages »), `Read_PortFieldDefaultOutOfRange_IsRefused` (`0`, `65536`), `Read_NumberFieldDefaultOutsideMinMax_IsRefused`, `Schema_NumberAndPortFields_RoundTripAsJson` (dans `tests/Trofeo.Core.Tests`, comme les autres champs), `Read_EntryWithFolder_IsRefused` (`"bin/x.dll"`), `Read_SizeOutsideGrid_IsRefused` (`minSize` 17×1), `Read_InvalidJson_ReturnsOneError` (pas d'exception).
  - `Check_OlderMinorRequired_IsCompatible` (`1.0` alors que `Current` vaut `1.0`, et avec un `Current` simulé `1.5` via une surcharge `internal static CompatibilityKind Check(SdkVersion required, SdkVersion current, IReadOnlySet<int> majors)`) ; `Check_NewerMinorRequired_NeedsNewerApp` (`1.6` contre `1.5`) ; `Check_UnsupportedMajor_IsTooOld` (`0.9` contre majeures `{1}`) ; `Check_NewerMajor_NeedsNewerApp` (`2.0`).
  - `Message(NeedsNewerApp)` vaut exactement « Ce module demande une version plus récente d'Ashboard Vision : mets à jour le logiciel. »
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Extensions.Tests` → échec à la compilation.
- [ ] **Étape 3 :** écrire les types ci-dessus (lecture avec `System.Text.Json` et `ControlJson`-like options : énumérations en texte, `AllowOutOfOrderMetadataProperties` pour `kind`).
- [ ] **Étape 4 :** les tests passent.
- [ ] **Étape 5 :** commit `feat(extensions): manifeste des modules complémentaires, versions du SDK et compatibilité`.

### Tâche 2 : le paquet `.avmod`

**Fichiers :** créer `src/Trofeo.Extensions/Packaging/AvmodPackage.cs`, `ContentHash.cs`, `AvmodWriter.cs` ; test `tests/Trofeo.Extensions.Tests/AvmodPackageTests.cs`

**Interfaces :**
- Consomme : `ManifestReader` (tâche 1).
- Produit :
  - `public static class AvmodPackage { AvmodInspection Inspect(string path); void ExtractTo(string path, string targetDirectory); }` ; `public sealed record AvmodInspection(ModuleManifest? Manifest, IReadOnlyList<string> Errors, long UncompressedBytes)`.
  - `public static class ContentHash { string Compute(string directory); string Compute(IReadOnlyList<(string RelativePath, Stream Content)> files); }` : SHA-256 hexadécimal minuscule, sur les lignes triées (ordinal) `chemin-relatif-en-minuscules-avec-/` + `\0` + SHA-256 du fichier.
  - `public static class AvmodWriter { void Pack(string moduleDirectory, string outputPath); }` (utilisé par le banc d'essai).

Limites : 50 Mio décompressés au total, 2 000 entrées, `manifest.json` à la racine, l'`entry` du manifeste présente à la racine, aucun nom d'entrée absolu, avec lecteur, `..` ou `:`. `ExtractTo` écrit d'abord dans `<cible>.tmp-<guid>` puis renomme d'un bloc ; une cible existante est refusée.

- [ ] **Étape 1 : tests qui échouent** :
  - `Inspect_ValidPackage_ReturnsManifest` ; `Inspect_NoManifestAtRoot_IsRefused` ; `Inspect_EntryDllMissing_IsRefused`.
  - `Inspect_ZipSlipEntry_IsRefused` (entrées `..\evil.dll`, `C:\evil.dll`, `/evil.dll`) ; `Inspect_TooManyEntries_IsRefused` (2 001) ; `Inspect_TooLarge_IsRefused` (en-têtes annonçant 51 Mio).
  - `ExtractTo_RefusedPackage_WritesNothing` (le dossier parent reste vide) ; `ExtractTo_ExistingTarget_Throws`.
  - `Compute_SameContentDifferentOrder_SameHash` ; `Compute_OneByteChanged_DifferentHash` ; `Compute_RenamedFile_DifferentHash`.
  - `Pack_ThenInspect_RoundTrips` (même `ContentHash` avant paquet et après extraction).
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** `System.IO.Compression.ZipArchive` (ne jamais faire confiance à `ExtractToDirectory`).
- [ ] **Étape 4 :** les tests passent.
- [ ] **Étape 5 :** commit `feat(extensions): paquet .avmod vérifié avant extraction et empreinte du contenu`.

### Tâche 3 : la liste scellée des modules validés

**Fichiers :** créer `src/Trofeo.Extensions/Approval/ApprovedModulesStore.cs`, `ApprovedModules.cs` ; test `ApprovedModulesStoreTests.cs`

**Interfaces :**
- Produit :
  - `public sealed record ApprovedModule(string Id, string Version, string Directory, string ContentHash, IReadOnlyList<ModulePermission> Granted, string? SetAsideReason, string? Repository)`.
  - `public sealed record DevFolder(string Path, IReadOnlyList<ModulePermission> Granted)`.
  - `public sealed record ApprovedModules(string? ModulesRoot, bool DeveloperMode, IReadOnlyList<DevFolder> DevFolders, IReadOnlyList<ApprovedModule> Modules)` avec `static ApprovedModules Empty`.
  - `public sealed class ApprovedModulesStore(string filePath, string keyPath) { ApprovedLoad Load(); void Save(ApprovedModules modules); }` ; `public sealed record ApprovedLoad(ApprovedModules Modules, bool Tampered)`.

Sceau : HMAC-SHA256 (clé aléatoire de 32 octets, créée au premier `Save`, rangée dans `modules.key` chiffrée par DPAPI `CurrentUser` avec l'entropie `"ashboard-vision.modules"`) calculé sur le JSON sans le champ `mac`. Fichier absent → `Empty`, `Tampered = false`. Sceau faux, clé illisible ou JSON illisible → `Empty`, `Tampered = true`, avertissement dans le journal. Écriture atomique (fichier temporaire puis remplacement).

- [ ] **Étape 1 : tests qui échouent** : `SaveThenLoad_RoundTrips` ; `Load_Missing_IsEmptyNotTampered` ; `Load_EditedByHand_IsTampered` (un `granted` ajouté au texte) ; `Load_KeyReplaced_IsTampered` ; `Load_Garbage_IsTampered`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire le magasin (`ProtectedData` comme `DpapiSecretVault`).
- [ ] **Étape 4 :** les tests passent sous Windows (sous Linux, ils sont dans les échecs connus DPAPI, comme Core).
- [ ] **Étape 5 :** commit `feat(extensions): liste scellée des modules validés`.

### Tâche 4 : le catalogue des modules du dossier

**Fichiers :** créer `src/Trofeo.Extensions/Catalog/CommunityCatalog.cs`, `CommunityEntry.cs`, `ModulesFolder.cs` ; test `CommunityCatalogTests.cs`

**Interfaces :**
- Consomme : tâches 1 à 3.
- Produit :
  - `public static class ModulesFolder { string Default { get; } /* Documents\Ashboard Vision\Modules */ string Resolve(ApprovedModules modules); }`.
  - `public enum CommunityState { Ready, NeedsApproval, Changed, NeedsNewerApp, TooOld, Invalid, Unavailable, SetAside }`.
  - `public sealed record CommunityEntry(string Id, string Directory, ModuleManifest? Manifest, CommunityState State, string? Message, ApprovedModule? Approval, bool FromDevFolder)`.
  - `public sealed class CommunityCatalog(Func<ApprovedLoad> load) { IReadOnlyList<CommunityEntry> Scan(); }`.

Règles : on parcourt `<racine>\<id>\<version>\manifest.json` ; pour un même `id`, seule la version approuvée compte (sinon la plus récente). `Ready` exige : approuvé, même version, `ContentHash.Compute` identique, compatible, pas mis de côté. Hash différent → `Changed`. Un fichier illisible (OneDrive : rapatriement impossible, `IOException`) → `Unavailable` avec le message « Fichiers du module indisponibles (OneDrive ?) : choisis un dossier non synchronisé dans les Paramètres. ». Mode développeur allumé : chaque `DevFolder` donne une entrée `FromDevFolder` (`Ready` si son manifeste est valide et compatible, hash non vérifié) ; un dossier de développement dont l'`id` est déjà installé est `Invalid` avec « Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant. ». Mode éteint : aucune entrée de développement.

- [ ] **Étape 1 : tests qui échouent** (dossiers temporaires, magasin factice) : `Scan_ApprovedAndUnchanged_IsReady` ; `Scan_NotApproved_NeedsApproval` ; `Scan_FileChanged_IsChanged` ; `Scan_NewerSdk_NeedsNewerApp` ; `Scan_InvalidManifest_IsInvalid` ; `Scan_SetAside_IsSetAside` ; `Scan_TamperedStore_EverythingNeedsApproval` ; `Scan_DevFolderWithModeOff_Ignored` ; `Scan_DevFolderCollidingWithInstalled_IsInvalid`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire le catalogue.
- [ ] **Étape 4 :** les tests passent.
- [ ] **Étape 5 :** commit `feat(extensions): catalogue des modules complémentaires et de leur état`.

### Tâche 5 : l'enfermement (AppContainer et job object)

**Fichiers :** créer `src/Trofeo.Extensions/Sandbox/AppContainerProfile.cs`, `SandboxedProcess.cs`, `ModuleJob.cs`, `Native.cs` ; créer `tests/Trofeo.SandboxProbe/` (application console de test) ; test `tests/Trofeo.Extensions.Tests/SandboxTests.cs`

**Interfaces :**
- Produit :
  - `public sealed class AppContainerProfile { static AppContainerProfile Ensure(string moduleId); static void Delete(string moduleId); SecurityIdentifier Sid { get; } string Name { get; } string DataDirectory { get; } void GrantRead(string directory); }` (`CreateAppContainerProfile` / `DeriveAppContainerSidFromAppContainerName` si elle existe déjà, `GetAppContainerFolderPath`, droits lecture + exécution hérités accordés au SID seul).
  - `public sealed class ModuleJob : IDisposable { ModuleJob(); void Assign(SafeProcessHandle process); JobUsage Read(); }` ; `public readonly record struct JobUsage(TimeSpan CpuTime, long PeakProcessMemory, bool MemoryLimitHit)` — limites de la section « Décisions ».
  - `public sealed class SandboxedProcess : IDisposable { static SandboxedProcess Start(SandboxStart start); int Id { get; } SafeProcessHandle Handle { get; } Task<int> Exited { get; } IntPtr DuplicateInto(SafeHandle source); }` ; `public sealed record SandboxStart(string Executable, string Arguments, AppContainerProfile Profile, IReadOnlyList<ModulePermission> Granted, IReadOnlyList<SafeHandle> InheritedHandles, ModuleJob Job)`.

`Start` : `CreateProcess` avec `STARTUPINFOEX`, attribut `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` (capacités des permissions réseau accordées seulement) et `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (seulement `InheritedHandles`), `CREATE_SUSPENDED`, assignation au job **avant** `ResumeThread`, `CREATE_NO_WINDOW`.

`Trofeo.SandboxProbe <action> <argument>` imprime `OK` ou `DENIED <code>` : `read <fichier>`, `tcp <hôte> <port>`, `spawn <exe>`, `alloc <Mo>`.

- [ ] **Étape 1 : tests qui échouent** (Windows seulement : `[Trait("Windows", "Sandbox")]`, ignorés hors Windows) :
  - `Probe_CannotReadUserFile` (un fichier créé dans `%TEMP%` de l'utilisateur → `DENIED`).
  - `Probe_CanReadGrantedDirectory` (dossier passé à `GrantRead` → `OK`).
  - `Probe_WithoutNetworkPermission_CannotConnect` / `Probe_WithInternet_CanConnect` (`tcp 1.1.1.1 443` : `DENIED` sans capacité, `OK` avec `internetClient` ; marqués `[Trait("Network", "Internet")]`).
  - `Probe_Localhost_IsDenied` (un serveur TCP de test sur `127.0.0.1` n'est pas joignable, même avec les deux capacités : c'est la limite documentée).
  - `Probe_CannotSpawn` (`ActiveProcessLimit = 1` → `DENIED`).
  - `Probe_OverMemoryLimit_IsKilled` (`alloc 600` → le processus se termine, `JobUsage.MemoryLimitHit`).
  - `Delete_RemovesProfileAndDataDirectory`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** P/Invoke dans `Native.cs` (`LibraryImport`), SID et ACL avec `System.Security.AccessControl`.
- [ ] **Étape 4 :** les tests passent sous Windows, Debug, sans élévation. Le dossier de sortie des tests doit être lisible par le conteneur : les tests accordent `GrantRead` au dossier de `Trofeo.SandboxProbe` et du runtime.
- [ ] **Étape 5 :** commit `feat(extensions): enfermement des modules dans un AppContainer et un job object`.

### Tâche 6 : le protocole entre le service et l'hôte

**Fichiers :** créer `src/Trofeo.Extensions/Protocol/HostMessages.cs`, `MessageChannel.cs`, `FrameBuffers.cs` ; test `ProtocolTests.cs`

**Interfaces :**
- Produit :
  - `public sealed class MessageChannel : IAsyncDisposable { MessageChannel(Stream input, Stream output); Task SendAsync(HostMessage message, CancellationToken ct); IAsyncEnumerable<HostMessage> ReadAllAsync(CancellationToken ct); }` : trames = longueur sur 4 octets petit-boutiste + JSON UTF-8, 1 Mio au plus (au-delà : `InvalidDataException` et fin de lecture).
  - `public abstract record HostMessage` avec discriminant `type` ; messages exacts :
    - service → hôte : `Hello(string Protocol, string ModuleDirectory, string Entry, string TypeId, IReadOnlyList<ModulePermission> Granted, string DataDirectory, JsonElement Appearance, int MaxFps)`, `Open(Guid InstanceId, JsonElement Settings, int Width, int Height, float TextScale, int Columns, int Rows, long Buffer0, long Buffer1)`, `Close(Guid InstanceId)`, `AppearanceChanged(JsonElement Appearance)`, `Limits(int MaxFps)`, `FrameDone(Guid InstanceId, int Buffer)`, `Lookup(int Id, string FieldKey, string Query)`, `RunAction(int Id, Guid InstanceId, string ActionKey, JsonElement Settings)`, `SecretValue(int Id, string? Value)`, `DataValue(int Id, string Status, JsonElement? Value)`, `Shutdown()`.
    - hôte → service : `Ready(string RequiredSdk)`, `Frame(Guid InstanceId, int Buffer, bool Animating)`, `Status(Guid InstanceId, ModuleHealth Health, string? Message)`, `Log(string Level, string Message)`, `SecretGet(int Id, string Key)`, `SecretSet(int Id, string Key, string? Value)`, `DataGet(int Id, string Kind)` (`"sensors"` ou `"nowPlaying"`), `LocalConnect(int Id, Guid InstanceId, int Port)`, `LookupReply(int Id, IReadOnlyList<LookupResult> Results)`, `ActionReply(int Id, ActionResult Result)`, `Failure(string Message)`.
    - réponses du service au relais : `LocalConnected(int Id, long ReadHandle, long WriteHandle)` (deux bouts de tubes anonymes dupliqués dans l'hôte), `LocalRefused(int Id, string Reason)`.
  - `public sealed class FrameBuffers : IDisposable` (côté service) : `FrameBuffers(int width, int height)` crée une section anonyme de `2 × width × height × 4` octets (BGRA, alpha prémultiplié, pas = `width × 4`) ; `SafeHandle Section { get; }` ; `void CopyTo(int buffer, SKBitmap destination)`.
  - `public static class FrameView` (côté hôte) : `IntPtr Map(long sectionHandle, int width, int height)` et `Unmap(IntPtr)`.
- [ ] **Étape 1 : tests qui échouent** : `RoundTrip_EachMessageType` (chaque message, sérialisé puis relu, est égal) ; `Read_OversizedFrame_Throws` ; `Read_TruncatedStream_EndsCleanly` ; `FrameBuffers_WriteThroughMappedView_ReadsBack` (on écrit des pixels par `FrameView.Map` sur un handle dupliqué dans le même processus, `CopyTo` les retrouve).
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire le protocole (`System.Text.Json`, polymorphisme par `type`).
- [ ] **Étape 4 :** les tests passent.
- [ ] **Étape 5 :** commit `feat(extensions): protocole et images partagées entre le service et l'hôte de module`.

### Tâche 7 : le contrat du SDK et l'hôte de module

**Fichiers :**
- Créer : `src/AshboardVision.Sdk/CommunityModule.cs`, `ModuleServices.cs`, `DataResult.cs`, `PinnedHttpClient.cs` ; modifier `AshboardVision.Sdk.csproj`
- Créer : `src/Trofeo.ModuleHost/Trofeo.ModuleHost.csproj` (exe, `OutputType` WinExe sans fenêtre), `Program.cs`, `ModuleLoader.cs`, `InstanceRenderer.cs`
- Test : `tests/Trofeo.ModuleHost.Tests/` (avec un module de test `tests/Trofeo.ModuleHost.Tests.SampleModule/`)

**Interfaces :**
- Consomme : protocole (tâche 6), `AppearanceResolver.Resolve` de `Trofeo.Core` pour reconstruire le thème à partir de `Hello.Appearance`.
- Produit (API publique, espace `AshboardVision.Sdk`) :
  - `public abstract class CommunityModule : IAsyncDisposable` : `protected ModuleServices Services { get; }` ; `public virtual Task StartAsync(CancellationToken ct)` ; `public abstract void Render(SKCanvas canvas, ModuleRenderContext ctx)` ; `protected void Invalidate()` ; `protected void SetStatus(ModuleHealth health, string? message = null)` ; `public virtual bool IsAnimating => false` ; `public virtual Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query, CancellationToken ct)` ; `public virtual Task<ActionResult> RunActionAsync(string actionKey, CancellationToken ct)` ; `public virtual ValueTask DisposeAsync()`.
  - `public sealed class ModuleServices` : `JsonElement Settings`, `IModuleLog Log`, `ISecretStore Secrets`, `TimeProvider Time`, `string DataDirectory`, `bool Has(ModulePermission permission)`, `Task<DataResult<SensorReading>> GetSensorsAsync(CancellationToken ct)`, `Task<DataResult<NowPlayingInfo>> GetNowPlayingAsync(CancellationToken ct)`, `Task<DataResult<Stream>> ConnectLocalAsync(int port, CancellationToken ct)` (relais vers un service de ce PC ; le port doit être la valeur d'un champ `port` des réglages de cette case, sinon `Denied`), `HttpClient CreateHttpClient(string? certificateKey = null)` (son `SocketsHttpHandler.ConnectCallback` fait passer `localhost`, `127.0.0.1` et `::1` par `ConnectLocalAsync`, le reste par le réseau normal).
  - `public enum DataStatus { Ok, Denied, Unavailable }` ; `public sealed record DataResult<T>(DataStatus Status, T? Value)` ; `public sealed record SensorReading(string? CpuName, string? GpuName, IReadOnlyDictionary<string, double> Values)` ; `public sealed record NowPlayingInfo(string Title, string Artist, string AppName, TimeSpan Position, TimeSpan Duration, bool IsPlaying, byte[]? CoverPng)`.
  - Le projet `AshboardVision.Sdk` (créé à la tâche 1) gagne les références `Trofeo.ModuleKit` et SkiaSharp.
- Produit (hôte) : `Trofeo.ModuleHost.exe --in <handle> --out <handle>`.

Règles de l'hôte :
- Il charge la DLL `Entry` dans un `AssemblyLoadContext` dont la résolution renvoie **les assemblages de l'hôte** pour `AshboardVision.Sdk`, `Trofeo.ModuleKit` et `SkiaSharp*` (copies du paquet ignorées), le reste depuis le dossier du module.
- La DLL doit contenir **exactement une** classe publique non abstraite dérivée de `CommunityModule`, avec un constructeur sans paramètre ; sinon `Failure("…")` et fin.
- Une instance de la classe par `Open` ; `StartAsync` puis dessin.
- Dessin : à chaque `Invalidate()` (et tant que `IsAnimating`), au plus `MaxFps` fois par seconde, dans le tampon libre (jamais celui qui attend son `FrameDone`), puis `Frame`.
- `SetStatus` → `Status` ; `Log` limité à 20 messages par seconde.
- `GetSensorsAsync` sans la permission → `DataResult(Denied, null)` sans message au service.
- `CreateHttpClient(key)` : validation du certificat par `CertificatePinning.Check` (déplacé de `Trofeo.Modules.Network` vers `Trofeo.ModuleKit`, l'appel du module Réseau suit), empreinte rangée dans le coffre du module sous `cert:<key>`.
- Une exception non rattrapée d'une méthode du module → `Status(Offline, "Erreur du module")` pour l'instance, l'hôte continue ; les exceptions des fils du module font tomber l'hôte (c'est le rôle du superviseur).
- Fin de l'hôte quand le tube d'entrée se ferme.

- [ ] **Étape 1 : tests qui échouent** (l'hôte lancé **sans** enfermement, par tubes anonymes ordinaires, avec le module de test) : `Host_LoadsModuleAndSendsFirstFrame` (pixels attendus dans le tampon 0) ; `Host_NeverWritesBufferAwaitingFrameDone` (le module invalide 10 fois sans `FrameDone` : au plus deux `Frame`) ; `Host_RespectsMaxFps` (module animé, `MaxFps = 4`, 2 s → entre 7 et 9 images) ; `Host_SensorsWithoutPermission_ReturnsDenied` ; `Host_ConnectLocalWithoutPermission_ReturnsDenied` ; `HttpClient_Localhost_GoesThroughRelay` (le faux service répond `LocalConnected` avec un tube relié à un petit serveur HTTP de test) ; `Host_TwoModuleClasses_Fails` ; `Host_ModuleThrowsInRender_ReportsOfflineAndKeepsRunning` ; `Host_InputClosed_Exits`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire le SDK et l'hôte.
- [ ] **Étape 4 :** les tests passent ; `dotnet test tests/Trofeo.Modules.Network.Tests` reste vert après le déplacement de `CertificatePinning`.
- [ ] **Étape 5 :** commit `feat(sdk): contrat CommunityModule et hôte de module`.

### Tâche 8 : le module distant dans le service

**Fichiers :** créer `src/Trofeo.Extensions/Service/RemoteModuleFactory.cs`, `RemoteModule.cs`, `ModuleHostSupervisor.cs`, `PlaceholderModuleFactory.cs`, `IServiceDataBroker.cs`, `LocalRelay.cs`, `ModuleFileLock.cs` ; modifier `src/Trofeo.Service/ModuleRegistry.cs` ; test `tests/Trofeo.Extensions.Tests/RemoteModuleTests.cs`, `SupervisorTests.cs`, `tests/Trofeo.Service.Tests/ModuleRegistryTests.cs`

**Interfaces :**
- Consomme : tâches 4 à 7.
- Produit :
  - `ModuleRegistry` devient modifiable : `void SetCommunityFactories(IReadOnlyList<IModuleFactory> factories)` (remplace d'un bloc les fabriques complémentaires, les fabriques d'origine restent en tête) ; `Descriptors`, `Types` et `Factory` lisent un instantané (sûr entre fils) ; `event EventHandler? TypesChanged`.
  - `public interface IServiceDataBroker { DataResult<SensorReading> Sensors(); DataResult<NowPlayingInfo> NowPlaying(); }`.
  - `public sealed class ModuleHostSupervisor : IAsyncDisposable` (un par type) : `ModuleHostSupervisor(CommunityEntry entry, IReadOnlyList<ModulePermission> granted, string hostExecutable, ISecretStore secrets, IServiceDataBroker data, TimeProvider time, Action<string, string> setAside)` ; `RemoteModule Open(ModuleContext context)` ; `int ActiveInstances` ; `JobUsage? Usage` ; `Task RestartAsync()` ; `void SetMaxFps(int fps)` ; `void SetAppearance(JsonElement appearance)`.
  - `public sealed class RemoteModuleFactory : IConfigurableModuleFactory` (descripteur et schéma tirés du manifeste ; `LookupAsync` / `RunActionAsync` relayés à l'hôte, « Module arrêté. » s'il ne tourne pas).
  - `public sealed class PlaceholderModuleFactory : IModuleFactory` : pour un module présent mais pas `Ready` ; son module dessine le message de l'entrée (« À valider dans la fenêtre de réglage », « Mis de côté : … », etc.) en couleur `Warning`, état `NotConfigured`.

`RemoteModule.Render` pose la dernière image copiée (aucune attente) ; avant la première image, rien ; au-delà de 10 s sans image après `Open`, « Le module ne répond pas ». À la réception de `Frame`, copie par `FrameBuffers.CopyTo`, puis `FrameDone`, puis `Invalidated`. Un hôte qui meurt : instances en `Offline` « Module arrêté, relance… », relance selon la section « Décisions » ; mise de côté au 3ᵉ arrêt en 10 min (`setAside(id, raison)`), la raison citant « 512 Mo dépassés » quand `MemoryLimitHit`. Le superviseur ne démarre un hôte qu'à la première `Open`, et l'arrête quand `ActiveInstances` retombe à 0. **Avant chaque lancement**, il prend le verrou d'exécution (`ModuleFileLock.Acquire(string directory) → ModuleFileLock`, qui expose `ContentHash` calculé sur les fichiers ouverts et se libère à `Dispose`, à la fin de l'hôte) ; sauf dossier de développement, une empreinte différente de celle validée empêche le lancement et les cases affichent « Fichiers du module modifiés : valide-le de nouveau ».

`LocalRelay` : à `LocalConnect(id, instanceId, port)`, refuse (`LocalRefused`, « Port non autorisé ») un port qui n'est la valeur d'aucun champ `PortField` des réglages de cette instance, une permission `ThisComputer` non accordée, ou au-delà de 16 connexions ouvertes ; sinon se connecte en TCP à `127.0.0.1:port` (délai 5 s), crée deux tubes anonymes, en duplique les bouts dans l'hôte (`SandboxedProcess.DuplicateInto`), répond `LocalConnected` et recopie les octets dans les deux sens jusqu'à la fermeture d'un côté.

- [ ] **Étape 1 : tests qui échouent** (hôte réel non enfermé, module de test de la tâche 7, fabrique de processus injectable) :
  - `Render_BeforeFirstFrame_DrawsNothing` ; `Render_AfterFrame_DrawsItsPixels` ; `Render_NoFrameFor10Seconds_SaysNotResponding` (`FakeTimeProvider`).
  - `HostKilledMidFrame_RenderDoesNotThrowAndKeepsLastImage` (point de vigilance 2).
  - `ThreeCrashesInTenMinutes_SetsAside` ; `Crash_RestartsWithBackoff`.
  - `NoOpenInstance_NoProcessStarted` (coût nul) ; `LastInstanceClosed_StopsHost`.
  - `Start_FilesChangedSinceScan_DoesNotLaunch` ; `FilesLockedWhileHostRuns_WriteAndDeleteFail` ; `HostExited_FilesUnlocked`.
  - `Relay_EchoServer_RoundTrip` (serveur d'écho sur `127.0.0.1`, son port saisi dans le champ `port` de l'instance) ; `Relay_PortNotInInstanceSettings_IsRefused` ; `Relay_PortChangedInSettings_NewPortAllowedOldRefused` ; `Relay_PermissionNotGranted_IsRefused` ; `Relay_SeventeenthConnection_IsRefused`.
  - `Registry_SetCommunityFactories_KeepsBuiltInsFirstAndRaisesTypesChanged`.
  - `Layout_WithUnapprovedCommunityType_ShowsPlaceholder` et `Layout_WithUninstalledType_IsIgnoredWithWarning` (point de vigilance 3, dans `tests/Trofeo.Service.Tests` avec `DisplayLoop`).
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire le service distant. En production, `ModuleHostSupervisor` passe par `SandboxedProcess` (tâche 5) avec les tubes et la section dupliqués ; en test, une fabrique de processus sans enfermement.
- [ ] **Étape 4 :** les tests passent.
- [ ] **Étape 5 :** commit `feat(extensions): modules complémentaires vus par le service comme des modules ordinaires`.

### Tâche 9 : installation, validation et gestion côté service

**Fichiers :** créer `src/Trofeo.Extensions/CommunityModules.cs` (le point d'entrée du service), `ModuleInstaller.cs` ; modifier `src/Trofeo.Display/Program.cs` (composition) ; test `CommunityModulesTests.cs`

**Interfaces :**
- Consomme : tâches 2 à 8, `DpapiSecretVault`, `SensorHub`, une `IMediaSource` partagée créée à la demande.
- Produit : `public sealed class CommunityModules : IAsyncDisposable` avec :
  - `IReadOnlyList<CommunityEntry> Entries { get; }` ; `event EventHandler? Changed` ;
  - `PackageInspection Inspect(string avmodPath)` ; `public sealed record PackageInspection(ModuleManifest? Manifest, IReadOnlyList<string> Errors, string? Conflict, ApprovedModule? Installed)` (`Conflict` : « Un autre module porte déjà cet identifiant » quand l'`id` existe avec un autre `repository`) ;
  - `Task<ActionResult> InstallAsync(string avmodPath, IReadOnlyList<ModulePermission> granted, CancellationToken ct)` (extraction dans `<racine>\<id>\<version>\`, `GrantRead`, empreinte du contenu enregistrée, ancienne version supprimée après succès) ;
  - `ActionResult Approve(string id, IReadOnlyList<ModulePermission> granted)` ; `Task<ActionResult> SetPermissionsAsync(string id, IReadOnlyList<ModulePermission> granted)` (relance l'hôte : point de vigilance 4) ; `Task<ActionResult> UninstallAsync(string id)` (dossier, AppContainer, coffre `Scope(id)` vidé, entrée retirée) ; `Task<ActionResult> RelaunchAsync(string id)` (retire la mise de côté) ;
  - `ActionResult SetModulesRoot(string path)` ; `ActionResult SetDeveloperMode(bool on)` ; `ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted)` ; `ActionResult RemoveDevFolder(string path)` ; `Task<ActionResult> ReloadDevModuleAsync(string id)`.

Une permission accordée qui n'est pas déclarée dans le manifeste est refusée (`ActionResult(false, …)`). Une permission nouvelle déclarée par une nouvelle version n'est pas accordée tant que l'utilisateur ne l'a pas cochée. Installer une nouvelle version retire la mise de côté. Après chaque changement : `Scan()`, `ModuleRegistry.SetCommunityFactories(...)`, `Changed`.

- [ ] **Étape 1 : tests qui échouent** : `Install_ValidPackage_IsReadyAndRegistered` ; `Install_GrantingUndeclaredPermission_IsRefused` ; `Install_SameIdOtherRepository_IsConflict` ; `Install_NewVersionSameRepository_ReplacesOld` ; `Install_NewVersionWithNewPermission_NotGrantedUntilChecked` ; `Install_NewVersion_ClearsSetAside` ; `InstalledFileModifiedLater_IsNotLaunched` ; `SetPermissions_RestartsHostWithNewCapabilities` ; `Uninstall_RemovesFolderSecretsAndProfile` ; `DroppedFolderNotApproved_NeverStartsAHost` ; `Relaunch_ClearsSetAside`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire la classe ; `Program.cs` la crée après le `ModuleRegistry`, lui passe `vault.Scope`, `sensorHub`, le chemin de `Trofeo.ModuleHost.exe` (`AppLocator`), et transmet `MaxFps` et l'apparence à chaque rechargement.
- [ ] **Étape 4 :** les tests passent ; suite complète verte.
- [ ] **Étape 5 :** commit `feat(extensions): installer, valider, régler et désinstaller un module complémentaire`.

### Tâche 10 : le canal de contrôle

**Fichiers :** modifier `src/Trofeo.Control/Trofeo.Control.csproj` (référence `AshboardVision.Sdk` pour `ModulePermission`), `Messages.cs`, `ControlPipe.cs` (`ProtocolVersion = 3`), `ControlServer.cs`, `ControlClient.cs`, `src/Trofeo.Service/ServiceControlHandler.cs`, `src/Trofeo.Settings/Services/IServiceLink.cs`, `ServiceLink.cs` ; tests `tests/Trofeo.Control.Tests/ControlChannelTests.cs`, `tests/Trofeo.Service.Tests/ServiceControlHandlerTests.cs`, `tests/Trofeo.Settings.Tests/ServiceLinkTests.cs`

**Interfaces :**
- Consomme : `CommunityModules` (tâche 9). Comme `LinkUpdateState` pour les mises à jour, la fenêtre ne connaît que des copies simples : `Trofeo.Control` ne référence pas `Trofeo.Extensions`, et `ServiceControlHandler` fait la traduction.
- Produit :
  - `ModuleTypeInfo` gagne `CommunityTypeInfo? Community` ; `public sealed record CommunityTypeInfo(string Author, string Description, string Version, IReadOnlyList<ModulePermission> Declared, IReadOnlyList<ModulePermission> Granted)`.
  - `public enum LinkCommunityState { Ready, NeedsApproval, Changed, NeedsNewerApp, TooOld, Invalid, Unavailable, SetAside }` (miroir de `CommunityState`).
  - `public sealed record CommunityModuleInfo(string Id, string Name, string Author, string Description, string? Version, LinkCommunityState State, string? Message, IReadOnlyList<ModulePermission> Declared, IReadOnlyList<ModulePermission> Granted, bool FromDevFolder, string? UpdateVersion, string? UpdateNotes, IReadOnlyList<ModulePermission> UpdateNewPermissions)`.
  - `public sealed record CommunityOverview(IReadOnlyList<CommunityModuleInfo> Modules, string ModulesRoot, bool DeveloperMode, IReadOnlyList<string> DevFolders, bool StoreTampered)`.
  - `public sealed record PackageInfo(string? Id, string? Name, string? Author, string? Version, string? Description, IReadOnlyList<ModulePermission> Declared, IReadOnlyList<string> Errors, string? Conflict, string? InstalledVersion)` (copie de `PackageInspection`).
  - `public sealed record ModuleConsumption(string Id, string DisplayName, bool IsCommunity, double CpuPercent, long MemoryBytes, bool High)` (produit par la tâche 11).
  - `IControlHandler` gagne : `IReadOnlyList<ModuleTypeInfo> GetTypes()`, `CommunityOverview GetCommunity()`, `PackageInfo InspectPackage(string path)`, `Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted, CancellationToken ct)`, `ActionResult ApproveModule(string id, IReadOnlyList<ModulePermission> granted)`, `Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct)`, `Task<ActionResult> UninstallModuleAsync(string id, CancellationToken ct)`, `Task<ActionResult> RelaunchModuleAsync(string id, CancellationToken ct)`, `ActionResult SetModulesRoot(string path)`, `ActionResult SetDeveloperMode(bool on)`, `ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted)`, `ActionResult RemoveDevFolder(string path)`, `Task<ActionResult> ReloadDevModuleAsync(string id, CancellationToken ct)`, `Task CheckModuleUpdatesAsync(CancellationToken ct)`, `Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct)`, `IReadOnlyList<ModuleConsumption> GetConsumption()`.
  - `IServiceLink` reprend chaque méthode (asynchrone, `ControlException` en cas d'échec), plus `IReadOnlyList<ModuleTypeInfo>? Types` relu après chaque changement.
- [ ] **Étape 1 : tests qui échouent** : un aller-retour par nouvelle requête dans `ControlChannelTests` ; `Hello_OldWindowProtocol2_IsMismatch` ; dans `ServiceControlHandlerTests`, chaque méthode délègue à `CommunityModules` ; dans `ServiceLinkTests`, `Types_RefreshedAfterInstall`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire les requêtes (noms de type : `types`, `community`, `inspectPackage`, `installPackage`, `approveModule`, `setModulePermissions`, `uninstallModule`, `relaunchModule`, `setModulesRoot`, `setDeveloperMode`, `addDevFolder`, `removeDevFolder`, `reloadDevModule`, `checkModuleUpdates`, `installModuleUpdate`, `consumption`).
- [ ] **Étape 4 :** les tests passent.
- [ ] **Étape 5 :** commit `feat(control): les modules complémentaires dans le canal de contrôle (protocole 3)`.

### Tâche 11 : consommation et mises à jour des modules

**Fichiers :** créer `src/Trofeo.Extensions/Consumption/ConsumptionMeter.cs`, `src/Trofeo.Extensions/Updates/ModuleUpdateChecker.cs` ; modifier `src/Trofeo.Distribution/ReleaseFeed.cs`, `UpdateChecker.cs` et extraire `VerifiedDownload.cs` de `UpdateDownloader.cs` ; tests `ConsumptionMeterTests.cs`, `ModuleUpdateCheckerTests.cs`, `tests/Trofeo.Distribution.Tests/` (existants verts)

**Interfaces :**
- Produit :
  - `ReleaseFeed.UrlFor(string repository)` (`https://api.github.com/repos/{repository}/releases?per_page=10`) ; `UpdateChecker.FetchAsync(string repository, CancellationToken ct)` ; `Url` et `FetchAsync(ct)` gardent leur sens pour le logiciel.
  - `public static class VerifiedDownload { Task DownloadAsync(HttpClient http, Uri asset, Uri sha256, string destination, IProgress<int>? progress, CancellationToken ct); }` — `UpdateDownloader` s'en sert ; ses tests ne changent pas.
  - `public sealed class ModuleUpdateChecker(UpdateChecker checker, TimeProvider time)` : `Task<IReadOnlyDictionary<string, ModuleUpdate>> CheckAsync(IReadOnlyList<CommunityEntry> entries, CancellationToken ct)` ; `public sealed record ModuleUpdate(string Version, string Notes, Uri Package, Uri Sha256)`. Version proposée : étiquette `v<version>` plus récente (préversion seulement si l'installée en est une), fichiers nommés selon la section « Décisions ». Vérification au démarrage du service (après 60 s, comme le logiciel) puis toutes les 24 h, seulement s'il y a au moins un module avec `repository`.
  - `public sealed class ConsumptionMeter(TimeProvider time)` : `void Sample(string id, JobUsage usage, long privateBytes)` ; `IReadOnlyList<ModuleConsumption> Read(ProcessInfo service)` (`High` selon la section « Décisions » ; une ligne « Modules d'origine (dans le service) » tirée de `ProcessInfo`). Rythme (conception 5.9) : **un relevé par minute** tant qu'un hôte tourne, ce qui suffit au repère puisque le temps processeur cumulé du job donne la moyenne exacte sur la minute ; un relevé toutes les 5 s **seulement pendant que la fenêtre lit la consommation** (requête `consumption`) ; aucun relevé sans hôte.
  - `CommunityModules` gagne `IReadOnlyList<ModuleConsumption> Consumption()`.
- [ ] **Étape 1 : tests qui échouent** : `Check_NewerTagWithAssets_ProposesUpdate` ; `Check_PrereleaseIgnoredForStableInstall` ; `Check_MissingSha256_NoUpdate` ; `Check_NoRepository_NoRequest` ; `Meter_AboveTwoPercentForAMinute_IsHigh` ; `Meter_ShortSpike_IsNotHigh` ; `Meter_NoHostRunning_TakesNoSample` ; `Meter_WindowNotReading_OneSamplePerMinute`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire ; l'installation d'une mise à jour passe par `VerifiedDownload` puis `CommunityModules.InstallAsync` (permissions nouvelles non accordées sans case cochée).
- [ ] **Étape 4 :** les tests passent, ceux de `Trofeo.Distribution.Tests` aussi.
- [ ] **Étape 5 :** commit `feat(extensions): consommation des modules et mises à jour proposées depuis GitHub`.

### Tâche 12 : la fenêtre de réglage — installer, valider, signaler

**Fichiers :** créer `src/Trofeo.Settings/Views/InstallModuleDialogView.axaml(.cs)`, `src/Trofeo.Settings/ViewModels/InstallModuleDialogViewModel.cs`, `CommunitySectionViewModel.cs`, `src/Trofeo.Settings/Controls/CommunityBadge.cs` ; modifier `ModulesTabView/ViewModel`, `LayoutTabView/ViewModel`, `SettingsTabView/ViewModel`, `ModuleListItem.cs`, `GridOverlay.cs`, `Program.cs` (argument `.avmod`), `Resources/Strings.resx`, `Texts.cs` ; tests `tests/Trofeo.Settings.Tests/InstallModuleDialogTests.cs`, `CommunitySectionTests.cs`, `CommunityBadgeTests.cs`

**Ce qu'on voit :**
- **Dialogue d'installation** (bouton « Installer un module… » de la section « Modules complémentaires » des Paramètres, ou `Trofeo.Settings.exe <chemin.avmod>`) : point d'exclamation jaune et texte d'avertissement « Ce module a été créé par la communauté, pas par Ashboard Vision. Il peut contenir du code malveillant : installe seulement un module dont tu connais et fais confiance à l'auteur. » ; nom, auteur, version, description ; une case par permission **déclarée**, décochée par défaut, avec son libellé et son explication ; boutons « Installer » et « Annuler ». Erreurs et conflits affichés tels quels, sans bouton « Installer ».
- **Valider un module trouvé dans le dossier** (`NeedsApproval`, `Changed`) : le même dialogue, titre « Valider un module complémentaire ».
- **Point d'exclamation jaune** (`CommunityBadge`, couleur `Warning` du thème de la fenêtre) : dans la liste des modules à ajouter (onglet Modules), dans l'en-tête des réglages d'un module complémentaire avec la mention « Module complémentaire : créé par la communauté, pas par Ashboard Vision » et ses permissions accordées, et **dans un angle** de sa case dans l'éditeur de disposition (rien d'autre sur la case).
- **Les champs `NumberField` et `PortField`** dans le formulaire des réglages d'un module (onglet Modules), pour les modules d'origine comme pour les complémentaires : une zone numérique bornée (1 à 65535 pour un port), la valeur proposée par défaut, et un message en rouge hors des bornes.
- **Section « Modules complémentaires »** (onglet Paramètres) : une ligne par module (nom, auteur, version, état en clair, mise à jour proposée), et pour chacun : « Permissions… » (mêmes cases), « Mettre à jour » (montre les notes et les permissions nouvelles), « Relancer » (mis de côté), « Désinstaller » (confirmation « Ses cases seront retirées de l'écran. » ; la fenêtre retire alors ses emplacements de la disposition puis recharge). Bandeau « La liste des modules validés a été modifiée hors du logiciel : valide de nouveau tes modules. » quand `StoreTampered`.

- [ ] **Étape 1 : tests qui échouent** : `Dialog_ShowsOnlyDeclaredPermissions_Unchecked` ; `Dialog_InstallSendsCheckedPermissionsOnly` ; `Dialog_ErrorsHideInstallButton` ; `SettingsForm_PortField_RejectsOutOfRange` ; `SettingsForm_NumberField_UsesDefault` ; `Badge_ShownForCommunityTypesOnly` (liste, en-tête, case de l'éditeur) ; `Section_Uninstall_RemovesPlacementsThenReloads` ; `Section_TamperedStore_ShowsBanner` ; `Program_AvmodArgument_OpensInstallDialogAfterConnect`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire les vues ; textes dans `Strings.resx` ; aperçu vérifié avec `dotnet run --project tools/Trofeo.SettingsPreview -- --out apercus-reglages` (ajouter un état « module complémentaire » à l'outil d'aperçu).
- [ ] **Étape 4 :** les tests passent ; regarder les aperçus.
- [ ] **Étape 5 :** commit `feat(reglages): installer, valider et signaler les modules complémentaires`.

### Tâche 13 : la fenêtre de réglage — dossier, mode développeur, consommation

**Fichiers :** modifier `CommunitySectionViewModel.cs`, `SettingsTabView.axaml`, `MainWindow.axaml` (bandeau), `Strings.resx` ; tests `CommunitySectionTests.cs`

**Ce qu'on voit :**
- « Dossier des modules » : chemin affiché, bouton « Changer… » (sélecteur de dossier) ; un dossier non vide d'un autre logiciel est accepté, les modules déjà installés restent où ils sont.
- « Mode développeur » : interrupteur ; allumé, bandeau « Mode développeur actif » en haut de la fenêtre, liste des dossiers de travail, « Ajouter un dossier… » (même dialogue d'avertissement et de permissions, titre « Tester un module en développement »), « Recharger » et « Retirer » par dossier.
- « Consommation des modules » : un tableau (module, processeur, mémoire), relu toutes les 5 s **seulement tant que la section est visible**, repère orange « Consommation élevée » quand `High`.
- [ ] **Étape 1 : tests qui échouent** : `DeveloperModeOn_ShowsBanner` ; `AddDevFolder_AsksPermissions` ; `Reload_CallsService` ; `Consumption_PolledOnlyWhileVisible` ; `Consumption_HighShowsOrangeMarker`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire.
- [ ] **Étape 4 :** les tests passent, aperçus regardés.
- [ ] **Étape 5 :** commit `feat(reglages): dossier des modules, mode développeur et consommation`.

### Tâche 14 : installateur, désinstallation, publication de l'hôte

**Fichiers :** modifier `installer/build.ps1` (publier `Trofeo.ModuleHost`), `installer/ashboard-vision.iss` (association `.avmod`), `installer/README.md`, `src/Trofeo.Distribution/UserCleanup.cs` ; tests `tests/Trofeo.Distribution.Tests/UserCleanupTests.cs`

- `build.ps1` publie `Trofeo.ModuleHost` dans le même dossier que les trois autres et vérifie que `Trofeo.ModuleHost.exe` est présent.
- `[Registry]` (HKA) : `.avmod` → `AshboardVision.Module` (« Module Ashboard Vision », icône de l'application), commande `"{app}\Trofeo.Settings.exe" "%1"`, `uninsdeletekey`.
- `UserCleanup` (lancé par la désinstallation, service arrêté) : supprime les AppContainer `ashboard-vision.mod.*` connus de la liste scellée et les portées de leurs secrets ; « effacer mes réglages » supprime aussi `modules.json`, `modules.key` et le dossier des modules **s'il est le dossier par défaut** (un dossier choisi par l'utilisateur n'est jamais supprimé).
- [ ] **Étape 1 : tests qui échouent** : `Cleanup_DeletesModuleProfilesAndSecrets` ; `Cleanup_EraseRemovesDefaultModulesFolder` ; `Cleanup_EraseKeepsCustomModulesFolder` ; `Cleanup_KeepLeavesModulesFolder`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire ; `pwsh installer/build.ps1 -Version 1.1.0-beta.1` construit un installateur sans avertissement d'ISCC.
- [ ] **Étape 4 :** les tests passent ; le dossier `installer/publish` contient `Trofeo.ModuleHost.exe`.
- [ ] **Étape 5 :** commit `feat(installateur): hôte des modules, association .avmod et nettoyage des modules`.

---

# Partie B — le SDK, le banc d'essai, la documentation, les exemples

### Tâche 15 : le paquet `AshboardVision.Sdk`

**Fichiers :** modifier `src/AshboardVision.Sdk/AshboardVision.Sdk.csproj` ; créer `src/AshboardVision.Sdk/README.md` (affiché par NuGet) ; test `tests/AshboardVision.Sdk.Tests/PackageTests.cs`

- Le paquet `AshboardVision.Sdk` version `1.0.0` contient `AshboardVision.Sdk.dll` et `Trofeo.ModuleKit.dll` (le schéma des réglages, le thème et `ModuleDrawing` en font partie : ce sont des types publics du contrat), dépendance NuGet SkiaSharp `3.119.4` ; documentation XML incluse.
- `EnablePackageValidation` est mis ; `PackageValidationBaselineVersion` sera posé à `1.0.0` après la première publication (commentaire dans le `.csproj`).
- `Trofeo.ModuleKit` : tout ajout public futur respecte la règle « on ajoute, on ne retire pas » ; un test liste l'API publique attendue (`PublicApi_MatchesSnapshot`, fichier `tests/AshboardVision.Sdk.Tests/api-1.0.txt`).
- [ ] **Étape 1 : tests qui échouent** : `Pack_ContainsSdkAndModuleKit` (`dotnet pack` dans un dossier temporaire, puis lecture du `.nupkg`) ; `PublicApi_MatchesSnapshot`.
- [ ] **Étape 2 :** échec.
- [ ] **Étape 3 :** écrire le `.csproj` (`PackageId`, `Authors` Ashewos, licence GPL-3.0-or-later, `RepositoryUrl`) et l'instantané d'API.
- [ ] **Étape 4 :** les tests passent.
- [ ] **Étape 5 :** commit `feat(sdk): paquet AshboardVision.Sdk et instantané de son API`.

### Tâche 16 : les aides de test `AshboardVision.Sdk.Testing`

**Fichiers :** créer `src/AshboardVision.Sdk.Testing/` (net10.0-windows), `ModuleTestHost.cs` ; test `tests/AshboardVision.Sdk.Tests/ModuleTestHostTests.cs`

**Interfaces :**
- Produit : `public sealed class ModuleTestHost<TModule> : IAsyncDisposable where TModule : CommunityModule, new()` : `ModuleTestHost(string manifestPath, JsonElement? settings = null, IReadOnlyList<ModulePermission>? granted = null)` (par défaut, toutes les permissions déclarées) ; `Task StartAsync()` ; `SKBitmap Render(GridSize size, string theme)` (`"classique"`, `"liquid-glass"`, `"pixel"`) ; `ModuleHealth Health` ; `string? StatusMessage` ; `FakeSecrets Secrets` ; `void SetSensors(SensorReading? reading)` ; `void SetNowPlaying(NowPlayingInfo? info)` ; `FakeTimeProvider Time`.
- C'est l'exécution **dans le processus du test**, sans enfermement, pour des tests rapides ; le banc d'essai (tâche 17) fait l'essai enfermé.
- [ ] **Étape 1 : tests qui échouent** : `Render_DrawsAtRequestedSize` ; `DeniedPermission_ReturnsDenied` ; `Settings_AreGivenToModule`.
- [ ] **Étape 2 :** échec. **Étape 3 :** écrire. **Étape 4 :** vert.
- [ ] **Étape 5 :** commit `feat(sdk): aides de test pour les créateurs de modules`.

### Tâche 17 : le banc d'essai `ashboard-module`

**Fichiers :** créer `tools/AshboardVision.Sdk.Tool/` (outil .NET, commande `ashboard-module`, paquet `AshboardVision.Sdk.Tool`), `CheckCommand.cs`, `PackCommand.cs`, `CheckReport.cs` ; test `tests/AshboardVision.Sdk.Tests/ToolTests.cs`

- L'outil embarque `Trofeo.ModuleHost` et ses dépendances (copie publiée dans le paquet de l'outil, même version que le SDK) et réutilise `Trofeo.Extensions` (manifeste, paquet, enfermement, superviseur).
- `ashboard-module check [dossier] [--out <dossier>]` :
  1. manifeste (erreurs de la tâche 1) et compatibilité ;
  2. démarrage enfermé avec les permissions déclarées, première image en moins de 10 s ;
  3. une image PNG par taille (`minSize` et chaque préréglage) et par thème (`classique`, `liquid-glass`, `pixel`), dans `--out` (par défaut `check-out`) ;
  4. une passe par permission déclarée **refusée** : l'hôte ne doit pas s'arrêter ;
  5. mesure sur 30 s : temps de dessin moyen, processeur moyen, mémoire maximale, avec les repères de la section « Décisions » ;
  6. arrêt propre en moins de 5 s.

  Sortie en français, une ligne par vérification (`✔` / `✖` / `!`), conclusion « Compatible avec Ashboard Vision (SDK 1.0). » ou « Pas compatible : … » ; code de sortie 0 ou 1.
- `ashboard-module pack [dossier] [--out <dossier>]` : vérifie le manifeste, fabrique `<id>-<version>.avmod` (`AvmodWriter.Pack`) et `.sha256` (format `sha256sum`).
- Tout marche **hors ligne** : aucune commande n'appelle le réseau (sauf ce que fait le module lui-même pendant `check`).
- [ ] **Étape 1 : tests qui échouent** (module de test de la tâche 7) : `Check_GoodModule_IsCompatibleAndWritesPngs` (9 images au moins) ; `Check_ModuleCrashingWithoutPermission_IsNotCompatible` ; `Check_InvalidManifest_ExitCode1` ; `Pack_ProducesInspectablePackageAndSha256` (le `.avmod` passe `AvmodPackage.Inspect`).
- [ ] **Étape 2 :** échec. **Étape 3 :** écrire (`System.CommandLine`). **Étape 4 :** vert (Windows seulement).
- [ ] **Étape 5 :** commit `feat(sdk): banc d'essai ashboard-module (check et pack)`.

### Tâche 18 : le modèle de projet `dotnet new ashboard-module`

**Fichiers :** créer `templates/AshboardVision.Sdk.Templates/` (`PackageType` Template), `content/ashboard-module/.template.config/template.json` (`shortName` `ashboard-module`, `sourceName` `MonModule`, paramètres `id` et `author`), projet de module, `manifest.json` copié à la sortie de la construction, projet de tests avec `ModuleTestHost`, `README.md` ; test `tests/AshboardVision.Sdk.Tests/TemplateTests.cs`

- Le module généré affiche « Bonjour » et l'heure, suit le thème, déclare zéro permission, a un champ de réglage texte « Message ».
- [ ] **Étape 1 : test qui échoue** : `NewModule_BuildsTestsAndChecks` (installe le modèle depuis le dossier, `dotnet new ashboard-module -n Essai --id moi.essai --author Moi`, puis `dotnet build`, `dotnet test`, `ashboard-module check` → code 0 ; marqué lent).
- [ ] **Étape 2 :** échec. **Étape 3 :** écrire. **Étape 4 :** vert.
- [ ] **Étape 5 :** commit `feat(sdk): modèle de projet dotnet new ashboard-module`.

### Tâche 19 : la documentation du SDK

**Fichiers :** créer `docs/sdk/README.md` (sommaire), `demarrer.md`, `contrat.md`, `permissions.md`, `dessin.md`, `reglages.md`, `tests.md`, `publier.md`, `compatibilite.md`, `securite.md` ; modifier `README.md` (section « Modules complémentaires » : ce que c'est, avertissement, lien vers `docs/sdk`) et `docs/utilisation-avancee.md`

Contenu imposé :
- `permissions.md` : le tableau 5.2 de la conception ; « Cet ordinateur » avec un champ `port` dans les réglages (jamais de port en dur), `ConnectLocalAsync` et le client HTTP du SDK qui passent par le relais du service.
- `reglages.md` : tous les champs, dont `NumberField` et `PortField`, et le fait que chaque case a ses propres réglages (deux serveurs = deux cases).
- `securite.md` : ce que l'enfermement empêche, ce qu'il n'empêche pas (données confiées envoyées sur Internet ; PC déjà infecté), l'empreinte enregistrée à l'installation et le verrou d'exécution, pourquoi les mises à jour sont proposées et jamais silencieuses.
- `publier.md` : `ashboard-module pack`, la version GitHub `v<version>` avec les deux fichiers nommés selon la section « Décisions », le champ `repository`.
- `demarrer.md` : installer le SDK en cochant « Outils pour créer des modules (SDK) » dans l'installateur d'Ashboard Vision (ou depuis les `.nupkg` d'une version GitHub, avec les commandes `dotnet nuget add source`, `dotnet new install` et `dotnet tool install -g`).
- `compatibilite.md` : les règles de la conception 4.5, exemples à l'appui.
- Chaque exemple de code de la documentation compile : les extraits viennent des modules d'exemple (tâches 20 et 21).
- [ ] **Étape 1 :** écrire les pages.
- [ ] **Étape 2 :** vérifier que chaque lien relatif pointe vers un fichier existant (même vérification qu'au plan 6, tâche 11).
- [ ] **Étape 3 :** commit `docs(sdk): documentation des modules complémentaires`.

### Tâche 20 : modules d'exemple « Dépôt GitHub » et « Démo »

**Fichiers :** créer `examples/GitHubRepo/` (id `ashewos.github`, permission `internet`, réglages : dépôt `propriétaire/nom`, jeton facultatif en secret) et `examples/Demo/` (id `ashewos.demo`, aucune permission, animation à 12 i/s) avec leurs tests

- « Dépôt GitHub » affiche les étoiles, la dernière version (étiquette et date) et le total des téléchargements des fichiers de ses versions ; `Offline` avec message en cas d'erreur réseau, `NotConfigured` sans dépôt ; rafraîchit toutes les 10 minutes (limite de l'API GitHub sans jeton : 60 requêtes par heure).
- « Démo » dessine une animation simple qui suit le thème et sert à essayer `IsAnimating`, le plafond d'images et la consommation.
- [ ] **Étape 1 : tests qui échouent** (`ModuleTestHost`, réponses HTTP factices) : `GitHub_ShowsStarsAndLatestRelease` ; `GitHub_NoRepository_NotConfigured` ; `GitHub_InternetDenied_SaysSo` ; `Demo_IsAnimating`.
- [ ] **Étape 2 :** échec. **Étape 3 :** écrire. **Étape 4 :** vert ; `ashboard-module check` → compatible pour les deux.
- [ ] **Étape 5 :** commit `feat(exemples): modules Dépôt GitHub et Démo`.

### Tâche 21 : module d'exemple « Serveur Minecraft »

**Fichiers :** créer `examples/Minecraft/` (id `ashewos.minecraft`, permissions `ce-pc`, `reseau-local` et `internet`), `ServerListPing.cs`, `MinecraftModule.cs`, tests avec un faux serveur TCP

**Interfaces :**
- `public static class ServerListPing { Task<ServerStatus> QueryAsync(string host, int port, TimeSpan timeout, CancellationToken ct); }` ; `public sealed record ServerStatus(string Motd, string Version, int Online, int Max, IReadOnlyList<string> SamplePlayers, TimeSpan Latency, byte[]? FaviconPng)`.
- Protocole « Server List Ping » de Minecraft Java : paquet de poignée de main (identifiant `0x00`, version de protocole `-1` en VarInt, adresse, port, état suivant `1`), demande d'état (`0x00`), réponse JSON (`version.name`, `players.online`, `players.max`, `players.sample[].name`, `description` texte ou objet de chat dont on garde le texte, `favicon` en `data:image/png;base64,…`), puis ping (`0x01` avec un long) pour la latence. Tous les entiers de longueur sont des VarInt.
- Réglages : `adresse` (`TextField`, « Adresse du serveur », `localhost` proposé), `port` (`PortField`, « Port », 25565 proposé), rafraîchissement 30 s. Deux serveurs = deux cases, chacune avec son adresse et son port. Une adresse `localhost`, `127.0.0.1` ou `::1` passe par `Services.ConnectLocalAsync(port)` (permission « Cet ordinateur ») ; les autres par une connexion TCP normale. `QueryAsync` prend donc aussi une surcharge `QueryAsync(Stream connection, string host, int port, …)`.
- Affichage : icône du serveur, nom (texte du message du jour, codes de couleur `§x` retirés), « 3 / 20 joueurs », jusqu'à trois pseudos, latence ; `Offline` « Serveur injoignable » ; un message clair quand la permission réseau manque.
- [ ] **Étape 1 : tests qui échouent** : `Query_ParsesStatusFromFakeServer` ; `Query_DescriptionAsChatObject_ReturnsText` ; `Query_StripsColorCodes` ; `Query_Timeout_Throws` ; `Module_Unreachable_IsOffline` ; `Module_ShowsPlayersAndIcon` ; `Module_LocalhostWithoutThisComputer_SaysPermissionMissing`.
- [ ] **Étape 2 :** échec. **Étape 3 :** écrire. **Étape 4 :** vert ; `ashboard-module check` → compatible.
- [ ] **Étape 5 :** commit `feat(exemples): module Serveur Minecraft`.

### Tâche 22 : le SDK dans les versions GitHub et dans l'installateur

**Fichiers :** modifier `.github/workflows/release.yml`, `installer/build.ps1`, `installer/ashboard-vision.iss`, `installer/README.md`

- `build.ps1` fabrique les quatre paquets du SDK (`AshboardVision.Sdk`, `AshboardVision.Sdk.Testing`, `AshboardVision.Sdk.Tool`, `AshboardVision.Sdk.Templates`), version = celle du SDK (`1.0.x`), dans `installer/publish/sdk/`, et les copie à côté de l'installateur.
- `release.yml` joint ces `.nupkg` à la version GitHub, avec l'installateur (rien sur nuget.org).
- `ashboard-vision.iss` :
  - `[Tasks]` : `Name: sdk; Description: "Outils pour créer des modules (SDK)"; Flags: unchecked`. Inno Setup reprend par défaut les choix de l'installation précédente (`UsePreviousTasks`), y compris en mise à jour silencieuse : la case est donc cochée lors d'une mise à jour si le SDK était installé, décochée sinon.
  - `[Files]` : les `.nupkg` dans `{app}\sdk`, `Tasks: sdk`.
  - À la fin de l'installation, si la tâche `sdk` est choisie : `dotnet --list-sdks` lancé avec les droits de l'utilisateur (`ExecAsOriginalUser`) ; si un SDK .NET 10 est présent, ajouter la source `AshboardVision` vers `{app}\sdk` (`dotnet nuget add source`), `dotnet new install AshboardVision.Sdk.Templates` et `dotnet tool update -g AshboardVision.Sdk.Tool --add-source "{app}\sdk"`, chaque code de sortie journalisé ; sinon, page ou message « Installe le SDK .NET 10 pour créer des modules : https://dotnet.microsoft.com/download ». Tâche décochée lors d'une mise à jour alors qu'elle était cochée : mêmes retraits qu'à la désinstallation.
  - Désinstallation : `dotnet tool uninstall -g AshboardVision.Sdk.Tool`, `dotnet new uninstall AshboardVision.Sdk.Templates`, `dotnet nuget remove source AshboardVision`, en ignorant les échecs (journalisés).
- [ ] **Étape 1 :** écrire ; `pwsh installer/build.ps1 -Version 1.1.0-beta.1` construit sans avertissement d'ISCC et `installer/publish/sdk` contient les quatre paquets.
- [ ] **Étape 2 :** essai à l'exécution dans le Bac à sable Windows, avec Alex (fichiers d'essai de `installer/bac-a-sable/`) : case décochée par défaut ; cochée sans SDK .NET → message ; cochée avec SDK .NET → `dotnet new ashboard-module` et `ashboard-module` disponibles ; mise à jour silencieuse → case reprise ; désinstallation → outil, modèle et source retirés.
- [ ] **Étape 3 :** commit `feat(installateur): le SDK joint aux versions et proposé par l'installateur`.

### Tâche 23 (PC d'Alex) : publication, vérification, bilan

Chaque action vers GitHub attend l'accord d'Alex, donné pour cette action-là.

- [ ] **Étape 1 : suite complète en Debug** sous Windows : verte.
- [ ] **Étape 2 : publier une bêta** `1.1.0-beta.1` (CHANGELOG, `Directory.Build.props`) avec l'accord d'Alex ; la version GitHub porte l'installateur et les paquets du SDK.
- [ ] **Étape 3 : avec Alex et l'écran** :
  - installer la bêta ; aucun module complémentaire → consommation identique au plan 6 (mesure comme au bilan 6) ;
  - installer « Dépôt GitHub » depuis son `.avmod` : avertissement, case Internet, point d'exclamation dans la liste, les réglages et l'éditeur ; la case affiche le dépôt `Ashewos/ashboard-vision` ;
  - modifier un fichier d'un module installé, sans Internet : il n'est plus lancé et demande à être validé de nouveau ;
  - refuser Internet puis le réaccorder : message, puis retour ;
  - « Serveur Minecraft » sur un serveur lancé sur le PC d'Alex, avec « Cet ordinateur » ; changer le port du serveur et le reporter dans les réglages de la case ;
  - « Démo » à 12 i/s : vue « Consommation des modules », repère orange si le seuil est dépassé ;
  - tuer `Trofeo.ModuleHost.exe` trois fois : relance, puis mise de côté ; « Relancer » ;
  - copier à la main un dossier de module dans le dossier des modules : il n'est pas lancé et demande à être validé ;
  - mode développeur avec le modèle `dotnet new ashboard-module` : bandeau, « Recharger » ;
  - désinstaller un module : ses cases disparaissent, son dossier et son AppContainer aussi ;
  - double-clic sur un `.avmod` : le dialogue s'ouvre.
- [ ] **Étape 4 : mesurer la sobriété** : service sans module, puis avec « Dépôt GitHub », puis avec « Démo » (processeur et mémoire de chaque hôte).
- [ ] **Étape 5 : écrire `docs/superpowers/plans/<date>-plan-7-bilan.md`** (état et commits, nombre de tests, vérifications, mesures, décisions, reste à faire) et mettre à jour `CLAUDE.md`. Commit `docs: bilan du plan 7`.
