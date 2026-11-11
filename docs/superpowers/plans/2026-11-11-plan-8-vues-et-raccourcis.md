# Plan 8 — Vues multiples et raccourcis : plan de construction

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**But :** plusieurs vues de l'écran (une seule dessinée), un nom par case, les raccourcis d'onglets sur Haut / Bas et ceux des vues sur Gauche / Droite, et des raccourcis déclarés par les modules complémentaires.

**Architecture :** la disposition passe d'une liste de cases à une liste de vues (`LayoutView`). Le service crée les cases de toutes les vues, mais ne dessine que la vue affichée, qu'il possède et garde dans un petit fichier à lui. Les cases d'un module complémentaire cachées sont ouvertes dans l'hôte mais ne dessinent pas. Les raccourcis deviennent des `ShortcutCommand` (onglets, vues, raccourcis de module complémentaire). Un module complémentaire déclare ses raccourcis dans son manifeste (SDK 1.1.0) et les reçoit par un nouveau message de l'hôte, sans jamais voir le clavier.

**Technologies :** .NET 10, C#, SkiaSharp, Avalonia (fenêtre), xUnit, Win32 `RegisterHotKey`.

**Conception :** `docs/superpowers/specs/2026-11-11-plan-8-vues-et-raccourcis-design.md` (validée par Alex le 11/11/2026). Elle prime sur ce plan.

## Contraintes globales

- Projet : `CLAUDE.md`. Debug seulement (`dotnet build`, `dotnet test`, `dotnet pack -c Debug`), jamais `-c Release`, jamais `installer/build.ps1`. Travail sur la branche `plan-8-vues`, partie de `Canary`. Rien de poussé.
- Commits : adresse `1345997+Ashewos@users.noreply.github.com` ; messages en français, préfixes `feat(…)`, `fix(…)`, `test`, `docs` ; terminés par `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Fichiers ajoutés un par un ; jamais `docs/guide-personnalisation.md`, `.claude/launch.json`, `apercus-accueil-b/`, `.superpowers/`.
- Textes affichés en français, qui tutoient. Noms de code en anglais, commentaires en français, comme le reste du dépôt.
- Ne jamais arrêter ni lancer le service ou la fenêtre d'Alex (installés dans `C:\Program Files\Ashboard Vision`).
- Valeurs fixées par la conception : **9 vues au plus**, au moins 1 ; nom de vue et nom de case **30 caractères au plus** ; première vue d'une migration nommée **« Vue 1 »** ; glissement **≈ 300 ms**, gardé seulement s'il passe **au moins 5 images** ; **10 raccourcis au plus** par module complémentaire ; **5 raccourcis transmis par seconde et par module** au plus ; SDK **1.1.0** ; canal de contrôle **protocole 4** ; publication **1.2.0** (bêta d'abord).
- Raccourcis par défaut : `Ctrl+Alt+Up` / `Ctrl+Alt+Down` → onglet précédent / suivant ; `Ctrl+Alt+Left` / `Ctrl+Alt+Right` → vue précédente / suivante (en boucle) ; `Ctrl+Alt+Shift+1` à `9` → onglet n° 1 à 9 (inchangé). Une touche par défaut proposée par un exemple utilise `Ctrl+Alt+Shift+<lettre>` (sur AZERTY, AltGr vaut Ctrl+Alt).
- Un module complémentaire **ne voit jamais le clavier** ; il ne reçoit jamais les commandes d'onglets ; un raccourci n'agit **que sur la vue affichée**.
- Tout ce qu'envoie un hôte reste une entrée hostile (règles du plan 7).

## Points de vigilance

Les cinq cas que la conception implique sans qu'une tâche les teste naturellement ; chacun a son test dans la tâche indiquée.
1. **Un `layout.json` de la 1.1.0 avec des raccourcis changés par Alex** : ses liaisons à lui restent, seules les anciennes valeurs par défaut passent sur Haut / Bas, et Gauche / Droite ne vont aux vues que si elles sont libres (tâche 2).
2. **Un module complémentaire animé dans une vue cachée** : l'hôte ne dessine rien et l'écran reste à son rythme de repos (tâches 3 et 4).
3. **Supprimer la vue affichée, ou un fichier de vue affichée qui nomme une vue disparue** : l'écran passe sur une vue voisine, ou la première, sans erreur (tâche 3).
4. **Appuis répétés pendant un glissement** : pas de file d'attente, l'écran finit sur la bonne vue (tâche 5).
5. **Importer une disposition partagée** (format 1.1.0, ou vues contenant un module complémentaire absent) : lue sans perte, le module absent garde son avertissement « type inconnu » (tâche 1).

---

## Partie A — Les vues

### Tâche 1 : le modèle des vues et des noms de cases

**Fichiers :**
- Créer : `src/Trofeo.Core/Layout/LayoutView.cs`, `src/Trofeo.Core/Layout/ViewEditor.cs`
- Modifier : `src/Trofeo.Core/Layout/DashboardLayout.cs`, `ModulePlacement.cs`, `LayoutJson.cs`, `LayoutTransfer.cs`, `LayoutValidator.cs`, `LayoutEditor.cs`, `src/Trofeo.Core/Rendering/FrameComposer.cs`, et chaque usage de `.Placements` (service, fenêtre, outils, tests) avec le sens « première vue »
- Tests : `tests/Trofeo.Core.Tests/LayoutViewsTests.cs` (nouveau), `ViewEditorTests.cs` (nouveau), tests existants adaptés

**Interfaces :**
- Produit :
  - `public sealed record LayoutView(Guid Id, string Name, IReadOnlyList<ModulePlacement> Placements)` ; `public const int MaxViews = 9; public const int MaxNameLength = 30;` sur `LayoutView`.
  - `DashboardLayout(int Rotation, IReadOnlyList<LayoutView> Views, IReadOnlyList<ShortcutBinding>? Shortcuts = null, AppearanceSettings? Appearance = null, IReadOnlyList<ModulePlacement>? Shelf = null, int? MaxFps = null)` : `Views` **remplace** `Placements`. Aides : `LayoutView View(Guid id)` (lève si absente), `IEnumerable<ModulePlacement> AllPlacements` (toutes les vues), `static DashboardLayout SingleView(int rotation, IReadOnlyList<ModulePlacement> placements, …)` pour les tests et l'assistant (vue « Vue 1 », identifiant neuf).
  - `ModulePlacement(…, JsonElement Settings, string? Name = null)` : nom facultatif de la case.
  - `LayoutEditor` : chaque opération prend la vue en deuxième paramètre, par exemple `EditResult Move(DashboardLayout layout, Guid viewId, Guid id, int column, int row)` ; `Remove` envoie la case à l'étagère ; `Add(layout, viewId, typeId, newId)` ; nouveau `EditResult SetName(DashboardLayout layout, Guid id, string? name)` (vide → `null`, coupé à 30 caractères, valable aussi pour une case de l'étagère).
  - `LayoutValidator.Check(IReadOnlyList<ModulePlacement> viewPlacements, ModulePlacement candidate, IReadOnlyDictionary<string, ModuleDescriptor> types)`.
  - `FrameComposer.Compose(IReadOnlyList<ModulePlacement> placements, IReadOnlyDictionary<Guid, IModule> modules, ThemeDefinition theme, IBackground background, DateTimeOffset now)` (et la surcharge `Theme`) : il dessine la liste reçue.
  - `public sealed class ViewEditor` (statique ou sans état, au choix) : `AddView(DashboardLayout, string name, Guid newViewId)`, `RenameView(layout, viewId, string name)`, `DuplicateView(layout, viewId, Guid newViewId, Func<Guid> newInstanceId)`, `DeleteView(layout, viewId)`, `MoveView(layout, viewId, int newIndex)` ; chacune rend `ViewEditResult(DashboardLayout Layout, ViewError Error)` avec `enum ViewError { None, TooManyViews, LastView, UnknownView, EmptyName }`.

- [ ] **Étape 1 : tests du format** dans `LayoutViewsTests.cs` :
  - `Load_LegacyPlacements_BecomesOneViewNamedVue1` : un JSON 1.1.0 (`"Placements": [deux cases]`) donne `Views.Count == 1`, `Views[0].Name == "Vue 1"`, les deux cases dans l'ordre, l'identifiant de vue non vide.
  - `RoundTrip_ThreeViews_KeepsOrderNamesAndCaseNames` : sauvegarde puis relecture identiques (noms de vues et de cases compris) ; le fichier écrit n'a plus de propriété `Placements`.
  - `Load_NoViewAtAll_GetsOneEmptyView` ; `Load_TenViews_KeepsNineAndWarns` ; `Load_NameLongerThan30_IsCut` ; `Load_SameInstanceInTwoViews_KeepsTheFirstAndWarns` ; `Load_DuplicateViewIds_RenumbersAndWarns`.
  - `Import_LegacySharedLayout_IsReadAsOneView` et `Import_ViewWithUnknownCommunityType_KeepsTheCase` (point de vigilance 5) via `LayoutTransfer.Import`.
- [ ] **Étape 2 : tests de l'éditeur de vues** dans `ViewEditorTests.cs` : `AddView_TenthView_IsRefused` (`TooManyViews`), `DeleteView_LastView_IsRefused` (`LastView`), `DeleteView_SendsItsCasesToTheShelfWithTheirSettings`, `DuplicateView_CopiesCasesWithNewIdsAndSameSettings`, `MoveView_ReordersViews`, `RenameView_EmptyName_IsRefused`, `RenameView_LongName_IsCutAt30`. Dans `LayoutEditorTests.cs` : `Move_ChecksOverlapOnlyInsideItsView` (deux vues, cases au même endroit : permis) et `SetName_IsKeptOnTheShelfToo`.
- [ ] **Étape 3 : lancer, vérifier l'échec** : `dotnet test tests/Trofeo.Core.Tests --filter "FullyQualifiedName~LayoutViewsTests|FullyQualifiedName~ViewEditorTests"` → échec de compilation (types absents).
- [ ] **Étape 4 : implémenter** le modèle, la lecture indulgente (même style que `ReadShortcuts` : un défaut devient un avertissement, jamais un refus du fichier entier) et l'écriture `Views`. Puis faire compiler toute la solution en remplaçant chaque `.Placements` par la première vue (`layout.Views[0].Placements`, ou `SingleView` dans les tests) : **aucun changement de comportement** en dehors de Core dans cette tâche.
- [ ] **Étape 5 : vérifier** : `dotnet build` sans avertissement, puis `dotnet test` (suite complète) vert.
- [ ] **Étape 6 : commit** `feat(disposition): plusieurs vues et un nom par case dans le modèle`.

### Tâche 2 : les commandes de raccourci et leur migration

**Fichiers :**
- Créer : `src/Trofeo.Core/Shortcuts/ShortcutCommand.cs`
- Modifier : `ShortcutBinding.cs`, `ShortcutDefaults.cs`, `LayoutJson.cs`, et les usages de `ShortcutBinding.Command` (routeur, fenêtre)
- Tests : `tests/Trofeo.Core.Tests/ShortcutMigrationTests.cs` (nouveau), tests de raccourcis existants

**Interfaces :**
- Produit :
  - `public enum ShortcutCommand { PreviousTab, NextTab, SelectTab, PreviousView, NextView, SelectView, Community }` : les trois premiers gardent leurs noms, donc un `"Command": "PreviousTab"` déjà écrit se relit tel quel.
  - `ShortcutBinding(string Keys, ShortcutCommand Command, int Index = 0, Guid? Target = null, string? ModuleId = null, string? ShortcutId = null)` : `Index` sert à `SelectTab` et `SelectView` (rang à partir de 0) ; `ModuleId` / `ShortcutId` seulement pour `Community` (partie B) ; `Target` vise une case.
  - `static ModuleCommandKind? ShortcutCommandExtensions.ToModuleCommand(this ShortcutCommand c)` : les trois commandes d'onglets, `null` pour le reste.
  - `ShortcutDefaults.All` : les valeurs des contraintes globales.

- [ ] **Étape 1 : tests** dans `ShortcutMigrationTests.cs` (point de vigilance 1) :
  - `Legacy_DefaultBindings_MoveTabsToUpDownAndAddViews` : un fichier sans `Views` avec les anciennes liaisons par défaut donne Up → `PreviousTab`, Down → `NextTab`, Left → `PreviousView`, Right → `NextView`, et les neuf `Ctrl+Alt+Shift+N` intacts.
  - `Legacy_CustomLeftBinding_IsKeptAndLeftNotGivenToViews` : `Ctrl+Alt+Left` lié par Alex à `SelectTab` index 2 reste tel quel ; `PreviousView` n'est pas ajouté sur Left ; Right, resté par défaut, devient `NextView`.
  - `Legacy_NoShortcutsProperty_GetsNewDefaults`.
  - `NewFormat_LeftBoundToPreviousTab_IsNotMigratedAgain` : un fichier qui a déjà `Views` n'est jamais migré (Alex a pu remettre Left sur l'onglet exprès).
  - `Binding_SelectView_RoundTrips` et `Binding_Community_RoundTripsModuleAndShortcutIds`.
- [ ] **Étape 2 : lancer, vérifier l'échec** : `dotnet test tests/Trofeo.Core.Tests --filter "FullyQualifiedName~ShortcutMigrationTests"`.
- [ ] **Étape 3 : implémenter** : la migration ne s'applique qu'à un fichier lu sans `Views` (on le sait dans `LayoutJson.Deserialize`). Les usages du routeur et de la fenêtre passent par `ToModuleCommand()` ; pour l'instant, les commandes de vue ne font rien (tâche 6).
- [ ] **Étape 4 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 5 : commit** `feat(raccourcis): commandes de vue et migration des raccourcis par défaut`.

### Tâche 3 : le service dessine seulement la vue affichée

**Fichiers :**
- Créer : `src/Trofeo.Service/DisplayedViewStore.cs`, `src/Trofeo.ModuleKit/IHideableModule.cs`
- Modifier : `src/Trofeo.Service/DisplayLoop.cs`, `ServiceControlHandler.cs`, `src/Trofeo.Display/Program.cs` (chemin du fichier)
- Tests : `tests/Trofeo.Service.Tests/DisplayLoopViewsTests.cs` (nouveau), `DisplayedViewStoreTests.cs` (nouveau)

**Interfaces :**
- Consomme : `DashboardLayout.Views`, `FrameComposer.Compose(placements, …)` (tâche 1).
- Produit :
  - `public interface IHideableModule { void SetVisible(bool visible, ModuleRenderContext place); }` dans ModuleKit : appelé par la boucle pour chaque case qui implémente l'interface, à sa création puis à chaque changement de vue ; `place` donne la place de la case (bornes, damier, taille du texte, thème) même cachée. Réservé aux modules d'origine et à `RemoteModule`.
  - `DisplayLoop` : `Guid CurrentViewId { get; }`, `bool ShowView(Guid viewId, int direction)` (faux si la vue n'existe pas ; `direction` −1, 0 ou +1, utilisé par la tâche 5), `bool ShowAdjacentView(int step)` (en boucle), `bool ShowViewAt(int index)` (faux sans vue de ce rang), `event Action<Guid>? ViewChanged`. `FindCommandTarget(Guid? instanceId)` ne cherche que dans la vue affichée (une cible d'une vue cachée rend `null`).
  - `public sealed class DisplayedViewStore(string filePath, TimeProvider time)` : `Guid? Load()`, `void Save(Guid viewId)` (au plus une écriture par seconde ; la dernière valeur gagne, écrite au plus tard une seconde après), `void Flush()` (à l'arrêt). Fichier `%AppData%\ashboard-vision\vue-affichee.json`, `{"ViewId":"…"}`.

- [ ] **Étape 1 : tests** dans `DisplayLoopViewsTests.cs`, avec les faux modules et la fausse horloge des tests existants :
  - `AllCasesOfAllViews_AreStarted` ; `OnlyTheCurrentView_IsComposed` (un module d'une vue cachée n'est jamais dessiné : compteur de `Render` à 0).
  - `HiddenAnimatingModule_DoesNotRaiseTheFrameRate` (point de vigilance 2 : sur 10 s simulées, autant d'images qu'avec la vue seule).
  - `HiddenModuleInvalidated_RedrawsOnlyWhenItsViewIsShown`.
  - `ShowAdjacentView_Wraps` ; `ShowViewAt_MissingIndex_DoesNothing`.
  - `ReloadWithoutTheCurrentView_ShowsTheNeighbourView` (point de vigilance 3 : la vue d'après, sinon celle d'avant) ; `StartWithStoredViewMissing_ShowsTheFirstView`.
  - `FindCommandTarget_IgnoresCasesOfHiddenViews`.
  - `HideableModule_IsToldItsVisibilityAndPlace` (création cachée → `SetVisible(false, place)` avec la bonne taille ; passage → `true`).
- [ ] **Étape 2 : tests** dans `DisplayedViewStoreTests.cs` : `Save_TwiceInOneSecond_WritesOnceThenTheLastValue`, `Load_MissingOrUnreadableFile_IsNull`, `Flush_WritesThePendingValue`.
- [ ] **Étape 3 : lancer, vérifier l'échec** : `dotnet test tests/Trofeo.Service.Tests --filter "FullyQualifiedName~DisplayLoopViewsTests|FullyQualifiedName~DisplayedViewStoreTests"`.
- [ ] **Étape 4 : implémenter.** `Live` garde toutes les cases, et sa liste « affichée ». `AnyAnimating` et le rendu ne regardent que celle-ci ; l'abonnement à `Invalidated` d'une case cachée marque sa vue « à redessiner » sans réveiller l'écran. Le calcul de la place d'une case (`ModuleRenderContext`) est celui de `FrameComposer` : factorisez-le pour qu'il n'existe qu'une fois. Le service lit la vue gardée au démarrage et l'enregistre à chaque changement.
- [ ] **Étape 5 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 6 : commit** `feat(service): seule la vue affichée est dessinée`.

### Tâche 4 : les cases cachées d'un module complémentaire

**Fichiers :**
- Modifier : `src/Trofeo.Extensions/Protocol/HostMessages.cs`, `src/Trofeo.Extensions/Service/RemoteModule.cs`, `ModuleHostSupervisor.cs`, `src/Trofeo.ModuleHost/InstanceRenderer.cs` (et le répartiteur des messages de l'hôte)
- Tests : `tests/Trofeo.Extensions.Tests/RemoteModuleVisibilityTests.cs` (nouveau), `tests/Trofeo.ModuleHost.Tests/InstanceRendererVisibilityTests.cs` (nouveau)

**Interfaces :**
- Consomme : `IHideableModule` (tâche 3).
- Produit : `public sealed record Visibility(Guid InstanceId, bool Visible) : HostMessage` (du service vers l'hôte) ; `HostMessages.ProtocolVersion` passe à `"1.1"`. `RemoteModule : IModule, IHideableModule`.

- [ ] **Étape 1 : tests** côté service, avec le faux hôte (`FakeHostProcess`) : `HiddenCase_IsOpenedAtItsSizeWithoutBeingDrawn` (un `Open` à la taille de la case sans aucun `Render`, puis `Visibility(false)`) ; `ShownCase_SendsVisibilityTrue_AndKeepsItsLastImageMeanwhile` ; `HiddenCase_FramesAreKeptButDoNotInvalidate`.
- [ ] **Étape 2 : tests** côté hôte : `HiddenInstance_IsNotDrawn_EvenWhenAnimating` (aucune image en 1 s) ; `HiddenInstance_DirtyRequest_IsDrawnWhenShown` (une demande de dessin faite cachée part dès `Visibility(true)`) ; `HiddenInstance_KeepsRunning` (`StartAsync` appelé, minuteries du module actives).
- [ ] **Étape 3 : lancer, vérifier l'échec** : `dotnet test tests/Trofeo.Extensions.Tests --filter "FullyQualifiedName~RemoteModuleVisibilityTests"` et `dotnet test tests/Trofeo.ModuleHost.Tests --filter "FullyQualifiedName~InstanceRendererVisibilityTests"`.
- [ ] **Étape 4 : implémenter.** Dans la boucle de `InstanceRenderer`, une instance cachée sort comme « rien à faire » (son `_dirty` reste). Un hôte plus ancien n'existe pas : il est livré avec le logiciel. Un `Visibility` d'une instance inconnue est ignoré.
- [ ] **Étape 5 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 6 : commit** `feat(modules): une case cachée tourne sans dessiner`.

### Tâche 5 : le glissement d'une vue à l'autre

**Fichiers :**
- Créer : `src/Trofeo.Core/Rendering/ViewTransition.cs`
- Modifier : `src/Trofeo.Service/DisplayLoop.cs`, `src/Trofeo.Core/Rendering/AppearanceSettings.cs`, `LayoutJson.cs` (lecture indulgente du champ), l'onglet Personnalisation de la fenêtre (case « Animer le passage entre les vues »)
- Tests : `tests/Trofeo.Core.Tests/ViewTransitionTests.cs`, `tests/Trofeo.Service.Tests/DisplayLoopTransitionTests.cs`

**Interfaces :**
- Produit :
  - `public sealed class ViewTransition(SKBitmap from, int direction, TimeSpan duration)` : `SKBitmap Compose(SKBitmap to, TimeSpan elapsed)` (la vue d'arrivée entre par le côté de `direction`, avec une courbe douce) et `bool IsDone(TimeSpan elapsed)`. `public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);`
  - `AppearanceSettings.AnimateViewSwitch` (`bool`, vrai par défaut).
  - Une ligne de journal par glissement : `Passage de vue : {n} images en {ms} ms.`

- [ ] **Étape 1 : tests** `ViewTransitionTests` : `AtStart_ShowsTheOldView`, `AtEnd_ShowsTheNewView`, `MidWay_RightKey_NewViewComesFromTheRight` (pixels témoins de deux couleurs unies).
- [ ] **Étape 2 : tests** `DisplayLoopTransitionTests` : `Switch_SendsFramesAsFastAsPossibleThenRests` (pendant 300 ms simulées, plusieurs images ; après, retour au rythme de repos) ; `SecondKeyDuringGlide_AbandonsItAndGoesOn` (point de vigilance 4 : deux appuis Droite rapprochés, vue 1 → vue 3, pas de troisième glissement en file) ; `AnimationOff_SwitchesInOneFrame` ; `Switch_LogsTheFrameCount`.
- [ ] **Étape 3 : lancer, vérifier l'échec** puis **implémenter.** Le glissement part de la dernière image envoyée ; l'image d'arrivée est composée une fois, puis seulement décalée (pas de nouveau rendu des modules à chaque image du glissement). Le plafond d'images par seconde est ignoré pendant les 300 ms.
- [ ] **Étape 4 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 5 : commit** `feat(service): passage glissé d'une vue à l'autre`.

### Tâche 6 : les vues au clavier et dans le canal de contrôle

**Fichiers :**
- Modifier : `src/Trofeo.Display/HotkeyRouter.cs`, `src/Trofeo.Display/Program.cs`, `src/Trofeo.Control/Messages.cs`, `ControlPipe.cs`, `ControlClient.cs`, `ControlServer.cs` (et ses requêtes), `src/Trofeo.Service/ServiceControlHandler.cs`, `DisplayLoop.cs` (aperçu d'une vue)
- Tests : `tests/Trofeo.Display.Tests/HotkeyRouterViewsTests.cs`, `tests/Trofeo.Control.Tests` (requêtes nouvelles), `tests/Trofeo.Service.Tests/ServiceControlHandlerViewsTests.cs`

**Interfaces :**
- Consomme : `ShortcutCommand` (tâche 2), `DisplayLoop.ShowAdjacentView`, `ShowViewAt`, `ShowView`, `ViewChanged`, `CurrentViewId` (tâche 3).
- Produit :
  - `ControlPipe.ProtocolVersion = 4`.
  - Requêtes : `ShowViewAsync(Guid viewId, CancellationToken ct) → bool` ; `SubscribePreviewAsync(int maxFps, Guid? viewId, CancellationToken ct)` (`null` : la vue affichée).
  - `ServiceStatus` gagne `Guid CurrentViewId` ; un message poussé `ViewShown(Guid ViewId)` à chaque changement (clavier compris), reçu par `ControlClient` comme les images d'aperçu (`event Action<Guid>? ViewShown`).
  - L'aperçu d'une vue cachée est composé seulement tant qu'un abonné la demande, au rythme de l'aperçu ; les cases d'un module complémentaire de cette vue sont alors « visibles » pour l'hôte, puis cachées à la fin de l'abonnement.

- [ ] **Étape 1 : tests** : `HotkeyRouterViewsTests` : `NextView_CallsShowAdjacentPlusOne`, `SelectView_CallsShowViewAt`, `TabCommand_GoesToTheTargetInTheCurrentViewOnly`. Canal : `ShowView_UnknownId_ReturnsFalse`, `ViewShown_IsPushedOnKeyboardSwitch`, `PreviewOfHiddenView_ComposesThatViewOnlyWhileSubscribed` (et les cases complémentaires de cette vue repassent cachées après).
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter.**
- [ ] **Étape 3 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 4 : commit** `feat(service): changer de vue au clavier et depuis la fenêtre`.

### Tâche 7 : les vues dans l'onglet Disposition

**Fichiers :**
- Modifier : `src/Trofeo.Settings/ViewModels/LayoutTabViewModel.cs`, `LayoutItems.cs`, `Views/LayoutTabView.axaml(.cs)`, `Services/EditorPreferences.cs`, `Services/ServiceLink.cs`, `IServiceLink.cs`, `Services/EditSession.cs`, `Resources/Strings.resx`, `ViewModels/FirstRunViewModel.cs` (une vue), `tools/Trofeo.SettingsPreview/Program.cs`
- Tests : `tests/Trofeo.Settings.Tests/LayoutViewsTabTests.cs` (nouveau)

**Interfaces :**
- Consomme : `ViewEditor`, `LayoutEditor` avec la vue, `LayoutEditor.SetName` (tâche 1) ; `ShowViewAsync`, `SubscribePreviewAsync(…, viewId)`, `ViewShown`, `ServiceStatus.CurrentViewId` (tâche 6).
- Produit : `EditorPreferences(bool FirstRunDone = false, bool RealTimePreview = false, bool FollowView = true)` ; dans `LayoutTabViewModel` : `ObservableCollection<ViewTabItem> ViewTabs`, `ViewTabItem SelectedView`, commandes `AddView`, `RenameView`, `DuplicateView`, `DeleteView`, `MoveView(int from, int to)` ; `ViewTabItem` a `Name` et `bool IsOnScreen` (le point vert).

- [ ] **Étape 1 : tests** (headless, comme `AddFlyoutTests`) : `SelectingATab_WithFollowOn_AsksTheServiceToShowIt` ; `SelectingATab_WithFollowOff_OnlyChangesThePreviewView` ; `GreenDot_FollowsViewShown` (un `ViewShown` venu du clavier déplace le point, pas la sélection) ; `AddView_TenthIsRefusedWithAMessage` ; `DeleteView_WithCases_AsksForConfirmation` ; `DeleteView_IsUndoneByCtrlZ` ; `OnScreenList_ShowsTheSelectedViewCasesWithTheirNames` ; `CaseName_IsEditedInTheCasePanel` ; `ForgetShelvedCase_RemovesTheBindingsTargetingIt` (et Ctrl+Z remet la case et ses liaisons ensemble) ; `DuplicateView_CopiesDoNotTakeTheOriginalsBindings`.
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter.** Textes : « + Nouvelle vue », « Renommer », « Dupliquer », « Supprimer », « L'écran suit la vue choisie », « Supprimer la vue « {0} » ? Ses modules vont dans « Retirés ». », « 9 vues au plus. », « Nom de la case ».
- [ ] **Étape 3 : aperçus** : `dotnet run --project tools/Trofeo.SettingsPreview -- --out apercus-reglages` produit aussi `disposition-vues.png` (trois vues, la deuxième choisie, la première sur l'écran) et `disposition-vues-menu.png` ; les regarder.
- [ ] **Étape 4 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 5 : commit** `feat(reglages): onglets de vues et noms de cases`.

### Tâche 8 : les vues dans l'onglet Raccourcis

**Fichiers :**
- Modifier : `src/Trofeo.Settings/ViewModels/ShortcutsTabViewModel.cs`, `ShortcutRowViewModel.cs`, `Views/ShortcutsTabView.axaml`, `Resources/Strings.resx`
- Tests : `tests/Trofeo.Settings.Tests/ShortcutsTabViewsTests.cs` (nouveau)

**Interfaces :**
- Consomme : `ShortcutCommand` (tâche 2), noms de cases (tâche 1).
- Produit : `ShortcutActionChoice(ShortcutCommand Command, int Index, string Text, string? ModuleId = null, string? ShortcutId = null)`.

- [ ] **Étape 1 : tests** : `Choices_IncludePreviousNextAndNineViews` (« Vue précédente », « Vue suivante », « Vue n° 1 » à « Vue n° 9 ») ; `TargetList_ShowsCaseNamesOrTypeNames` ; `AddingATakenKey_IsRefusedWithTheHolderName` (« Déjà pris par « Vue suivante ». ») ; `ViewCommands_HaveNoTargetChoice`.
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter.**
- [ ] **Étape 3 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 4 : commit** `feat(reglages): raccourcis des vues`.

---

## Partie B — Les raccourcis des modules complémentaires

### Tâche 9 : les raccourcis dans le manifeste

**Fichiers :**
- Modifier : `src/Trofeo.Extensions/Manifest/ModuleManifest.cs`, `ManifestReader.cs`, `Compatibility.cs`, `SdkVersion.cs` si besoin
- Tests : `tests/Trofeo.Extensions.Tests/ManifestShortcutsTests.cs` (nouveau)

**Interfaces :**
- Produit : `public sealed record ModuleShortcut(string Id, string Name, string? Keys)` ; `ModuleManifest` gagne `IReadOnlyList<ModuleShortcut> Shortcuts` (vide si absent) ; `Compatibility.Current` passe à `1.1`. JSON : `"shortcuts": [{ "id": "refresh", "name": "Rafraîchir", "keys": "Ctrl+Alt+Shift+R" }]`.

- [ ] **Étape 1 : tests** : `NoShortcuts_IsEmpty` ; `ValidShortcut_IsRead` ; `ElevenShortcuts_AreRefused` ; `BadId_IsRefused` (même règle que les clés de réglages) ; `DuplicateId_IsRefused` ; `UnreadableKeys_IsRefused` (via `ShortcutKeys.TryParse`) ; `NameWithControlOrBidiChars_IsCleaned` (même nettoyage que le nom du module) ; `ShortcutsWithSdk10_AreRefused` (« Ce module déclare des raccourcis : il doit demander le SDK 1.1 au moins. ») ; `Sdk11Module_IsCompatible`.
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter.**
- [ ] **Étape 3 : vérifier** : `dotnet test tests/Trofeo.Extensions.Tests` vert.
- [ ] **Étape 4 : commit** `feat(modules): raccourcis déclarés dans le manifeste`.

### Tâche 10 : le SDK 1.1.0 et le message de l'hôte

**Fichiers :**
- Modifier : `src/AshboardVision.Sdk/CommunityModule.cs`, `AshboardVision.Sdk.csproj` (`<Version>1.1.0</Version>`), `AshboardVision.Sdk.Testing` (csproj et hôte de test), `tools/AshboardVision.Sdk.Tool` (csproj), le modèle de projet (version épinglée), l'instantané d'API, `src/Trofeo.Extensions/Protocol/HostMessages.cs`, `src/Trofeo.ModuleHost/InstanceRenderer.cs`
- Tests : `tests/AshboardVision.Sdk.Tests` (instantané, aide de test), `tests/Trofeo.ModuleHost.Tests/ShortcutDeliveryTests.cs`

**Interfaces :**
- Produit :
  - `public virtual Task OnShortcutAsync(string shortcutId, CancellationToken ct) => Task.CompletedTask;` sur `CommunityModule`, appelée sur l'instance de la case (pas sur une instance à part), hors du fil de dessin.
  - `public sealed record Shortcut(Guid InstanceId, string ShortcutId) : HostMessage` (service → hôte).
  - Aide de test : `ModuleTestHost.TriggerShortcutAsync(string shortcutId)`.
  - Les 4 paquets du SDK passent en 1.1.0 ; le modèle épingle `AshboardVision.Sdk 1.1.0`.

- [ ] **Étape 1 : tests** : `Shortcut_IsDeliveredToTheInstance` ; `Shortcut_UnknownId_IsIgnoredWithoutCrash` (l'hôte ne vérifie pas la liste, le module répond ce qu'il veut) ; `Shortcut_ThrowingModule_IsLoggedAndTheHostKeepsRunning` ; `Shortcut_ForHiddenInstance_IsStillDelivered` (le service seul filtre par vue) ; instantané d'API mis à jour (`OnShortcutAsync` ajoutée) ; `TestHost_TriggerShortcut_CallsTheModule`.
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter.** `dotnet pack -c Debug` des 4 paquets, et le test du modèle (`TemplateTests`) vert avec la 1.1.0.
- [ ] **Étape 3 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 4 : commit** `feat(sdk): raccourcis des modules (SDK 1.1.0)`.

### Tâche 11 : le service transmet les raccourcis des modules

**Fichiers :**
- Modifier : `src/Trofeo.Extensions/Service/ModuleHostSupervisor.cs` (ou un fichier partiel `ModuleHostSupervisor.Shortcuts.cs`), `src/Trofeo.Extensions/CommunityModules.cs`, `ICommunityModules`, `src/Trofeo.Display/HotkeyRouter.cs`, `HotkeyWindow.cs`, `src/Trofeo.Control/*` (requête d'essai de touche), `ServiceControlHandler.cs`
- Tests : `tests/Trofeo.Extensions.Tests/SupervisorShortcutTests.cs`, `tests/Trofeo.Display.Tests/HotkeyRouterCommunityTests.cs`, `tests/Trofeo.Service.Tests/ShortcutKeyTestTests.cs`

**Interfaces :**
- Consomme : `ShortcutBinding` avec `Community` (tâche 2), `Shortcut` (tâche 10), `CurrentViewId` et les cases de la vue affichée (tâche 3).
- Produit :
  - `bool ICommunityModules.SendShortcut(string moduleId, string shortcutId, IReadOnlyCollection<Guid> instances)` : envoie à ces cases (déjà filtrées par vue et par cible) ; faux si l'hôte ne tourne pas, si le module est mis de côté, ou au-delà de **5 par seconde** pour ce module (compté par appui, pas par case).
  - Requête de contrôle `TestShortcutKeysAsync(string keys, CancellationToken ct) → ShortcutKeyState` avec `enum ShortcutKeyState { Free, Unreadable, TakenByAnotherProgram }` : le service essaie `RegisterHotKey` puis relâche aussitôt (une touche déjà enregistrée par le service lui-même rend `Free` : les conflits internes sont vérifiés par la fenêtre).
  - `ServiceStatus` gagne `IReadOnlyList<string> RefusedShortcutKeys` (les touches que Windows a refusées au dernier enregistrement).

- [ ] **Étape 1 : tests** : `SendShortcut_GoesToTheGivenInstancesOnly` ; `SendShortcut_SixthInOneSecond_IsDropped` ; `SendShortcut_HostNotRunning_IsDroppedAndReturnsFalse` ; `SendShortcut_SetAsideModule_DoesNothing` ; routeur : `CommunityBinding_SendsToTheModuleCasesOfTheCurrentViewOnly`, `CommunityBinding_WithTarget_SendsToThatCaseOnly_IfShown`, `CommunityBinding_NoCaseShown_SendsNothing` ; service : `TestShortcutKeys_KeyTakenElsewhere_ReportsIt` (registraire factice qui refuse), `RefusedKeys_AreInTheStatus`.
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter.**
- [ ] **Étape 3 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 4 : commit** `feat(modules): le service transmet les raccourcis aux modules`.

### Tâche 12 : la fenêtre propose et gère les raccourcis des modules

**Fichiers :**
- Modifier : `src/Trofeo.Settings/ViewModels/InstallModuleDialogViewModel.cs`, `Views/InstallModuleDialogView.axaml`, `ShortcutsTabViewModel.cs`, `ShortcutRowViewModel.cs`, `Views/ShortcutsTabView.axaml`, `CommunitySectionViewModel.cs` (désinstallation, mise à jour), `src/Trofeo.Control/Messages.cs` (`PackageInfo` et `CommunityModuleInfo` portent les raccourcis déclarés), `Resources/Strings.resx`
- Tests : `tests/Trofeo.Settings.Tests/InstallShortcutsTests.cs`, `ShortcutsTabCommunityTests.cs`

**Interfaces :**
- Consomme : `ModuleShortcut` (tâche 9), `TestShortcutKeysAsync`, `RefusedShortcutKeys` (tâche 11), `ShortcutActionChoice` (tâche 8).
- Produit : dans le dialogue, `ObservableCollection<ProposedShortcutItem> ProposedShortcuts` (`Name`, `Keys`, `bool IsChecked`, `string? TakenBy`) ; à la validation, les liaisons cochées sont ajoutées à la disposition (`Command = Community`, `ModuleId`, `ShortcutId`, sans `Target`) et enregistrées.

- [ ] **Étape 1 : tests** : `Install_FreeKey_ArrivesChecked` ; `Install_KeyTakenByTheSoftware_ArrivesUncheckedWithTheHolder` ; `Install_KeyTakenByAnotherProgram_ArrivesUnchecked` (« Touche déjà prise par un autre logiciel ») ; `Install_ShortcutWithoutKeys_IsNotShown` ; `Update_OnlyNewShortcutsAreProposed` ; `Update_RemovedShortcut_LosesItsBindings` ; `Uninstall_RemovesTheModuleBindings` ; `ShortcutsTab_CommunityBinding_HasTheYellowMark` ; `ShortcutsTab_Choices_GroupCommunityShortcutsByModule` ; `ShortcutsTab_RefusedKey_ShowsTheMessage` ; `ShortcutsTab_SetAsideModuleBinding_StillReservesItsKey`.
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter.** Textes : « Raccourcis proposés », « Touche déjà prise par « {0} ». », « Touche déjà prise par un autre logiciel. ».
- [ ] **Étape 3 : aperçus** : `install-raccourcis.png` et `raccourcis-modules.png` dans `apercus-reglages` ; les regarder.
- [ ] **Étape 4 : vérifier** : `dotnet test` (suite complète) vert.
- [ ] **Étape 5 : commit** `feat(reglages): raccourcis proposés par les modules complémentaires`.

### Tâche 13 : banc d'essai, exemples et documentation

**Fichiers :**
- Modifier : `tools/AshboardVision.Sdk.Tool` (passe `check`), `examples/Demo/*` (raccourci « Changer de couleur », `Ctrl+Alt+Shift+D`), `examples/GitHubRepo/*` (raccourci « Rafraîchir », sans touche), leurs manifestes (`sdk` 1.1), `docs/sdk/*.md` (nouvelle page `raccourcis.md`, manifeste, compatibilité, contrat, README de la doc), `README.md` (vues, nouvelle répartition des raccourcis)
- Tests : tests des exemples, tests du banc

- [ ] **Étape 1 : tests** : `Demo_ChangeColourShortcut_ChangesTheDrawnColour` ; `GitHubRepo_RefreshShortcut_FetchesAgain` (dans la limite de requêtes déjà en place) ; banc : `Check_DeliversEachDeclaredShortcut`, `Check_ShortcutThatThrows_IsReportedAsAnError`.
- [ ] **Étape 2 : lancer, vérifier l'échec** puis **implémenter** ; chaque extrait de code de la documentation vient d'un exemple qui compile (règle du plan 7).
- [ ] **Étape 3 : vérifier** : `dotnet test` (suite complète) vert ; `ashboard-module check` passe sur les trois exemples.
- [ ] **Étape 4 : commit** `docs(sdk): raccourcis des modules, exemples et README`.

### Tâche 14 (PC d'Alex) : essais, mesures, bêta, bilan

Chaque action vers GitHub attend l'accord d'Alex, donné pour cette action-là.

- [ ] **Étape 1 : suite complète en Debug** : verte.
- [ ] **Étape 2 : version** `1.2.0-beta.1` (`Directory.Build.props`, section du `CHANGELOG.md`), Alex construit l'installateur depuis une fenêtre PowerShell 7 et l'installe.
- [ ] **Étape 3 : avec Alex et l'écran** : sa disposition devient « Vue 1 » et ses raccourcis passent sur Haut / Bas ; créer trois vues (dont une avec Home Assistant ou Démo) ; Ctrl+Alt+Gauche / Droite en boucle ; l'écran suit l'onglet, puis option coupée ; point vert ; renommer, dupliquer, supprimer, Ctrl+Z ; noms de cases ; installer Démo 1.1 : raccourci proposé coché, puis un conflit volontaire ; le raccourci n'agit pas quand Démo est dans une vue cachée.
- [ ] **Étape 4 : mesurer** le glissement (ligne `Passage de vue` du journal : au moins 5 images en 300 ms, et fluide à l'œil d'Alex ; sinon retirer le glissement et la case « Animer le passage entre les vues ») et la sobriété (une vue ; trois vues avec un module animé caché : l'écran reste à 2 images/s).
- [ ] **Étape 5 : publier la bêta** avec l'accord d'Alex, puis écrire `docs/superpowers/plans/<date>-plan-8-bilan.md` et mettre à jour `CLAUDE.md`. Commit `docs: bilan du plan 8`.
