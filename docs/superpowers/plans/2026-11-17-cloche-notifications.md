# Cloche des notifications — plan de construction

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**But :** une icône de cloche dans l'en-tête de la fenêtre de réglage rassemble les mises à jour (logiciel, modules complémentaires) et les pannes (arrêt brusque ou mise de côté d'un module complémentaire, panne de la carte graphique). Chaque notification a une croix ; elle revient quand l'événement est nouveau.

**Architecture :**
- **Le service** ajoute ce qui manque pour dater les pannes :
  - le nombre d'arrêts brusques de chaque module complémentaire, et l'heure du dernier ;
  - l'heure de la mise de côté, enregistrée avec sa raison ;
  - le type et l'heure du problème de la carte graphique.
- **La fenêtre** compose la liste elle-même, dans un `NotificationsViewModel`, à partir de trois sources :
  - les mises à jour du logiciel (`IServiceLink.Updates`) ;
  - l'état du service (`Status.Acceleration`) ;
  - la vue d'ensemble des modules complémentaires, que la cloche relit à la connexion puis toutes les 30 s.
- **Les croix** sont retenues dans `settings-app.json`.

**Technologies :** .NET 10, C#, Avalonia, CommunityToolkit.Mvvm, xUnit.

**Conception :** `docs/superpowers/specs/2026-11-17-cloche-notifications-design.md`, validée par Alex le 17/11/2026. Elle prime sur ce plan.

## Contraintes globales

- **Projet.**
  - Les règles de `CLAUDE.md` s'appliquent.
  - Debug seulement (`dotnet build`, `dotnet test`). Jamais `-c Release`, jamais `installer/build.ps1`.
  - Le travail se fait sur la branche `plan-10-gpu`, à la suite de l'accélération matérielle. Rien n'est poussé.
- **Commits.**
  - Adresse : `1345997+Ashewos@users.noreply.github.com`.
  - Messages en français, avec les préfixes `feat(…)`, `fix(…)`, `test` et `docs`.
  - Ils se terminent par `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
  - Les fichiers sont ajoutés un par un. Jamais `docs/guide-personnalisation.md`, `.claude/launch.json`, `apercus-accueil-b/`, `apercus-*` ni `.superpowers/`.
- **Textes et code.**
  - Les textes affichés sont en français et tutoient ; ils vont dans `src/Trofeo.Settings/Resources/Strings.resx` (et `Texts.cs`).
  - Les noms de code sont en anglais, les commentaires en français.
- **Le service et la fenêtre d'Alex** ne sont jamais arrêtés ni lancés. `Trofeo.SettingsPreview` rend hors écran : c'est permis.
- **Protocole du canal de contrôle : reste 5.**
  - Les nouveaux champs sont des paramètres optionnels, placés en fin de liste. Ils ont une valeur par défaut, et un service qui ne les envoie pas donne cette valeur.
  - Les nouveaux types du canal vont dans `src/Trofeo.Control/Messages.cs`.
- **Valeurs fixées.**
  - Relecture de la vue d'ensemble des modules par la cloche : à la connexion, puis toutes les **30 s** tant que la liaison est connectée.
  - Clés des notifications ; une heure s'écrit en UTC, au format `"O"` :
    - `app:<version>` ;
    - `update:<id>:<version>` ;
    - `stop:<id>:<heure du dernier arrêt>` ;
    - `aside:<id>:<heure de la mise de côté>`, ou `aside:<id>:` sans heure ;
    - `gpu:<heure de la panne>`.
  - Textes, mot pour mot :
    - « Ashboard Vision {0} est disponible. »
    - « {0} {1} est disponible. »
    - « {0} s'est arrêté brusquement et a été relancé. »
    - « {0} s'est arrêté brusquement et a été relancé ({1} fois). », à partir de 2 arrêts
    - « {0} a été mis de côté après des arrêts répétés. »
    - la phrase de la carte graphique : `AccelerationInfo.Problem`, telle quelle
    - les liens : « Voir la mise à jour », « Voir le module », « Voir les réglages »
    - le panneau vide : « Rien de nouveau. »
    - le nom de l'icône (bulle d'aide et accessibilité) : « Notifications »
    - le nom de la croix : « Supprimer la notification »
  - Ordre de la liste :
    - les problèmes d'abord, du plus récent au plus ancien ;
    - puis la mise à jour du logiciel ;
    - puis les mises à jour des modules, par nom de module.
  - Le rond de la cloche porte le nombre de notifications visibles : rouge (`warning`/`danger` du thème) s'il y a au moins un problème, couleur d'accent sinon, absent à zéro.

## Points de vigilance (non couverts ailleurs, chacun a son test dans la tâche indiquée)

1. **Liaison coupée ou lecture des modules en échec.** Les croix enregistrées ne sont jamais élaguées : on n'élague que quand la liaison est connectée, que `Updates` est connu et que la dernière lecture des modules a réussi. Sans cela, une croix serait oubliée pendant une coupure et la notification reviendrait. Tâche 3 : `Prune_SkippedWhileDisconnectedOrOverviewFailed`.
2. **Ancienne liste des modules validés**, sans `SetAsideAt` : elle se lit et se vérifie comme avant, la mise de côté sans heure donne la clé `aside:<id>:`. Tâche 1 : `Store_LegacyFileWithoutSetAsideAt_LoadsAndVerifies`.
3. **Ancien `settings-app.json`**, sans `DismissedNotifications` : il se lit, et la liste est vide. Tâche 3 : `Preferences_LegacyFile_HasNoDismissed`.
4. **Nom de module venu d'un tiers** (retours à la ligne, caractères de contrôle, texte très long) : il passe par `UntrustedText.Line` avant d'entrer dans une phrase. Tâche 3 : `ModuleName_IsCleaned`.
5. **Module désinstallé** pendant qu'il a une notification d'arrêt ou de mise à jour : elle disparaît à la lecture suivante, et sa clé est élaguée. Tâche 3 : `UninstalledModule_NotificationDisappears`.

---

### Task 1: les arrêts et la mise de côté datés, côté service

**Fichiers :**
- Modifier :
  - `src/Trofeo.Extensions/Service/ModuleHostSupervisor.cs` (rappel optionnel `stopped`)
  - `src/Trofeo.Extensions/CommunityModules.cs` (compte par module, heure de mise de côté)
  - `src/Trofeo.Extensions/Approval/ApprovedModules.cs` et `ApprovedModulesStore.cs` (`SetAsideAt`)
  - `src/Trofeo.Control/Messages.cs` (`CommunityModuleInfo`)
  - `src/Trofeo.Service/ServiceControlHandler.cs` (`ToModuleInfo`)
- Créer : `src/Trofeo.Extensions/ModuleStops.cs`
- Tests : `tests/Trofeo.Extensions.Tests/` (superviseur, `CommunityModules`, magasin), `tests/Trofeo.Service.Tests/` (correspondance), `tests/Trofeo.Control.Tests/` (aller-retour)

**Interfaces :**
- Produit :
  - `public sealed record ModuleStops(int Count, DateTimeOffset LastAt);` (espace `Trofeo.Extensions`).
  - `ModuleHostSupervisor` : nouveau paramètre optionnel nommé `Action<DateTimeOffset>? stopped = null` en fin de constructeur. Il est appelé hors du verrou, une fois par arrêt que personne n'a voulu (le même qui passe par `RegisterStopLocked`), arrêt pour mémoire dépassée compris, avec l'heure de l'arrêt.
  - `CommunityModules.StopsFor(string id) : ModuleStops?`
    - Il compte les arrêts depuis le démarrage du service.
    - Le compte survit aux relances et aux remplacements de superviseur.
    - Il est remis à zéro quand le module est désinstallé.
  - `ApprovedModule` gagne `DateTimeOffset? SetAsideAt = null`, dernier paramètre.
    - `OnSetAside` l'écrit avec l'heure de `TimeProvider`.
    - Une relance qui efface `SetAsideReason` l'efface aussi.
  - `CommunityModuleInfo` gagne trois paramètres optionnels en fin de liste : `int StopCount = 0, DateTimeOffset? LastStopAt = null, DateTimeOffset? SetAsideAt = null`.
  - `ToModuleInfo` les remplit :
    - `StopCount` et `LastStopAt` viennent de `StopsFor(id)`.
    - `SetAsideAt` vaut `entry.Approval?.SetAsideAt`. Pour un module mis de côté sans validation (dossier de développement), il prend `StopsFor(id)?.LastAt`.

- [ ] **Étape 1 : écrire les tests qui échouent.**
  - `Supervisor_UnwantedStop_CallsStoppedWithTheTime` : un hôte qui s'arrête tout seul déclenche `stopped` une fois, avec l'heure du `TimeProvider` de test ; un arrêt demandé (désinstallation, arrêt du service) ne le déclenche pas.
  - `StopsFor_CountsAcrossRelaunches` : deux arrêts brusques, avec une relance entre les deux, donnent `Count == 2` et `LastAt` = l'heure du second.
  - `StopsFor_UnknownOrUninstalled_IsNull`.
  - `SetAside_RecordsTheTime_AndRelaunchClearsIt` : mise de côté à T, puis `SetAsideAt == T` dans le magasin. Une relance remet `SetAsideAt` et `SetAsideReason` à `null`.
  - `Store_LegacyFileWithoutSetAsideAt_LoadsAndVerifies` (point de vigilance 2).
  - `ToModuleInfo_CarriesStopsAndSetAsideAt` : les trois champs, dont le repli sur `LastAt` pour un dossier de développement mis de côté.
  - Control : `CommunityModuleInfo_RoundTripsStops` ; un message sans ces champs donne `0`, `null`, `null`.
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Extensions.Tests --filter "Stops|SetAside|Legacy"`, puis les tests Service et Control ciblés. Attendu : échec de compilation ou échecs.
- [ ] **Étape 3 : écrire le code** décrit dans « Interfaces ». La signature sous laquelle le magasin est scellé ne doit pas changer pour un fichier sans le nouveau champ.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Extensions.Tests`, `dotnet test tests/Trofeo.Service.Tests`, `dotnet test tests/Trofeo.Control.Tests`. Attendu : tout passe.
- [ ] **Étape 5 : commit** `feat(modules): arrêts brusques comptés et mise de côté datée, envoyés à la fenêtre`.

### Task 2: le type et l'heure du problème de la carte graphique

**Fichiers :**
- Modifier :
  - `src/Trofeo.Service/AccelerationStatus.cs`
  - `src/Trofeo.Service/AccelerationController.cs`
  - `src/Trofeo.Control/Messages.cs` (`AccelerationInfo`)
  - `src/Trofeo.Service/ServiceControlHandler.cs` (correspondance)
- Tests : `tests/Trofeo.Service.Tests/AccelerationControllerTests.cs`, le test de correspondance du service, `tests/Trofeo.Control.Tests/`

**Interfaces :**
- Produit :
  - Service (espace `Trofeo.Display`, comme `AccelerationStatus`) :
    - `public enum AccelerationProblemKind { Failure, Slow, Gone }`.
    - `AccelerationStatus` gagne `AccelerationProblemKind? ProblemKind = null, DateTimeOffset? ProblemAt = null`, en fin de liste.
  - Canal :
    - `public enum LinkAccelerationProblem { Failure, Slow, Gone }`.
    - `AccelerationInfo` gagne `LinkAccelerationProblem? ProblemKind = null, DateTimeOffset? ProblemAt = null`, en fin de liste.
  - Le type suit la phrase publiée (`_failure ?? _slow ?? _notice`) :
    - `_failure` donne `Failure` : la carte n'a pas pu démarrer, ou a eu un problème ;
    - `_slow` donne `Slow` ;
    - `_notice` donne `Gone` : la carte n'est plus là.
  - `ProblemAt` est l'heure (`TimeProvider.GetUtcNow()`) où la phrase en cours a été posée :
    - un nouvel échec, même au même texte (nouvel essai qui échoue), donne une nouvelle heure ;
    - une phrase qui reste la même d'une publication à l'autre garde son heure.

- [ ] **Étape 1 : écrire les tests qui échouent.**
  - `Failure_HasKindAndTime` : une panne pendant le dessin donne `ProblemKind == Failure`, avec `ProblemAt` égal à l'heure de la panne.
  - `FailedRetry_GetsANewTime` : le nouvel essai échoue 5 min plus tard, et `ProblemAt` avance d'autant.
  - `Republish_KeepsTheTime` : un `Tick` ou un `ReportFrame` sans changement ne bouge pas `ProblemAt`.
  - `Slow_And_Gone_Kinds` : chacun a son type.
  - `NoProblem_NullKindAndTime`.
  - Correspondance service → canal, et aller-retour Control, avec un message sans les champs (valeur `null`).
- [ ] **Étape 2 :** les tests ciblés (`--filter "Acceleration"` dans Service, et le test Control). Attendu : échec.
- [ ] **Étape 3 : écrire le code** décrit dans « Interfaces ».
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Service.Tests`, `dotnet test tests/Trofeo.Control.Tests`. Attendu : tout passe.
- [ ] **Étape 5 : commit** `feat(service): type et heure du problème de la carte graphique`.

### Task 3: la liste des notifications, côté fenêtre (sans vue)

**Fichiers :**
- Créer :
  - `src/Trofeo.Settings/ViewModels/NotificationsViewModel.cs`
  - `src/Trofeo.Settings/ViewModels/NotificationItem.cs`
- Modifier :
  - `src/Trofeo.Settings/Services/EditorPreferences.cs`
  - `src/Trofeo.Settings/ViewModels/MainViewModel.cs` (création, propriété `Notifications`)
  - `Strings.resx` et `Texts.cs` (les textes ci-dessus)
- Tests : `tests/Trofeo.Settings.Tests/NotificationsTests.cs`

**Interfaces :**
- Consomme :
  - `IServiceLink` : `State`, `Updates`, `Status`, `Changed` et `GetCommunityAsync()` ;
  - les champs des tâches 1 et 2 ;
  - `PreferencesStore` (`Current`, `Update`) ;
  - `TimeProvider` pour la relecture toutes les 30 s.
- Produit :
  - `public enum NotificationKind { News, Problem }`.
  - `public enum NotificationTarget { Settings, CommunityModules }`.
  - `public sealed record NotificationItem(string Key, NotificationKind Kind, string Text, string LinkText, NotificationTarget Target, DateTimeOffset? At)`.
  - `EditorPreferences` gagne `IReadOnlyList<string>? DismissedNotifications = null`, en fin de liste.
  - `NotificationsViewModel(IServiceLink link, PreferencesStore preferences, TimeProvider time)` :
    - `IReadOnlyList<NotificationItem> Items` : les notifications visibles, sans les clés supprimées, dans l'ordre fixé ;
    - `int Count` ;
    - `bool HasProblem` ;
    - `bool IsEmpty` ;
    - `void Dismiss(string key)` ajoute la clé aux préférences et retire la ligne ;
    - `event Action<NotificationTarget>? Navigate`, que la tâche 4 branche, et `void Open(NotificationItem item)` qui le lève ;
    - `Task RefreshCommunityAsync()`, appelée à la connexion puis toutes les 30 s tant que la liaison est connectée.
  - Ce qui produit une notification :
    - `Updates.State == Available` donne `app:<Version>` ;
    - `UpdateVersion` non nul donne `update:<id>:<UpdateVersion>` ;
    - un module à l'état `SetAside` donne `aside:…` ;
    - sinon, `StopCount > 0` donne `stop:<id>:<LastStopAt>` ;
    - `Acceleration.ProblemKind == Failure` donne `gpu:<ProblemAt>`. `Slow` et `Gone` ne donnent rien.
  - Sans connexion, `Items` est vide.
  - L'élagage ne garde, dans `DismissedNotifications`, que les clés encore produites. Il n'a lieu qu'aux conditions du point de vigilance 1.

- [ ] **Étape 1 : écrire les tests qui échouent** (faux `IServiceLink`, comme les tests voisins, et `FakeTimeProvider`).
  - **Contenu des notifications :**
    - `AppUpdate_Notified` : le texte est « Ashboard Vision 1.2.0 est disponible. », le lien « Voir la mise à jour », la cible `Settings`.
    - `ModuleUpdate_Notified` : le texte est « Météo 1.3 est disponible. », la cible `CommunityModules`.
    - `ModuleStopped_OnceThenCount` : 1 arrêt donne « Météo s'est arrêté brusquement et a été relancé. ». 2 arrêts donnent « … (2 fois). », avec une nouvelle clé.
    - `SetAside_ReplacesStop` : un module mis de côté, qui a aussi des arrêts, n'a qu'une notification : celle de la mise de côté.
    - `GpuFailure_Notified_SlowAndGoneNot`.
  - **La croix :**
    - `Dismiss_HidesAndPersists` : la croix retire la ligne, et la clé est dans `PreferencesStore.Current.DismissedNotifications`.
    - `Dismissed_ComesBackWhenTheKeyChanges` : une nouvelle version, ou un nouvel arrêt, fait revenir la notification.
    - `Resolved_Disappears` : quand la mise à jour n'est plus proposée ou que le module n'est plus de côté, la notification disparaît. Sa clé est élaguée.
  - **Ordre et cloche :**
    - `Order_ProblemsFirstNewestFirst_ThenApp_ThenModulesByName`.
    - `Badge_CountAndHasProblem`.
  - **Relecture :**
    - `Community_ReadAtConnectThenEvery30s` : une lecture à la connexion, aucune à 29 s, une autre à 30 s.
    - `Disconnected_NoItems`.
  - **Points de vigilance :**
    - `Prune_SkippedWhileDisconnectedOrOverviewFailed` (point 1) ;
    - `Preferences_LegacyFile_HasNoDismissed` (point 3) ;
    - `ModuleName_IsCleaned` (point 4) ;
    - `UninstalledModule_NotificationDisappears` (point 5).
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Settings.Tests --filter Notifications`. Attendu : échec de compilation.
- [ ] **Étape 3 : écrire le code** décrit dans « Interfaces ». Les événements de `IServiceLink` arrivent sur le fil de l'interface. Le minuteur des 30 s repasse sur ce fil avant de toucher à la liste, comme les autres minuteurs de la fenêtre.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Settings.Tests`. Attendu : tout passe.
- [ ] **Étape 5 : commit** `feat(reglages): liste des notifications (mises à jour et pannes) avec la croix retenue`.

### Task 4: la cloche dans l'en-tête, aperçus et notes de version

**Fichiers :**
- Créer : `src/Trofeo.Settings/Views/NotificationsView.axaml` (+ `.axaml.cs`)
- Modifier :
  - `src/Trofeo.Settings/Views/MainWindow.axaml` : une colonne de plus après la pastille. La grille passe de `Auto,*,Auto` à `Auto,*,Auto,Auto`, avec un écart de 8 px.
  - `src/Trofeo.Settings/ViewModels/MainViewModel.cs` (navigation)
  - `src/Trofeo.Settings/ViewModels/SettingsTabViewModel.cs` (amener la section « Modules complémentaires » à l'écran)
  - `tools/Trofeo.SettingsPreview/Program.cs`
  - `CHANGELOG.md` : une ligne dans la section `## 1.2.0-beta.3`
- Tests : `tests/Trofeo.Settings.Tests/NotificationsTests.cs` (navigation)

**Interfaces :**
- Consomme : `NotificationsViewModel` (tâche 3), via `MainViewModel.Notifications`.
- Produit :
  - **Ce que fait le clic sur un lien** (`Navigate`) :
    - il ferme le panneau ;
    - il met `SelectedTab = SettingsTab` (Paramètres) ;
    - pour `CommunityModules`, il demande en plus à `SettingsTabViewModel` d'amener la section des modules complémentaires à l'écran, par une requête que la vue honore avec `BringIntoView` ;
    - si un changement d'onglet est refusé (modifications en attente), rien d'autre ne se passe.
  - **La cloche** est un bouton icône : la cloche est dessinée en `Path`, sans texte ni emoji.
    - Bulle d'aide et `AutomationProperties.Name` : « Notifications ».
    - Le rond porte `Count` quand `Count > 0`.
    - Il est rouge si `HasProblem`, de la couleur d'accent sinon.
  - **Le panneau** est un `Flyout` sous la cloche, dans le style des panneaux voisins. Chaque ligne a :
    - une icône de type ;
    - la phrase ;
    - le lien ;
    - une croix à droite, nommée « Supprimer la notification ».

    Vide, le panneau dit « Rien de nouveau. ».

- [ ] **Étape 1 : écrire les tests qui échouent.**
  - `Open_Settings_SelectsSettingsTab`.
  - `Open_CommunityModules_SelectsSettingsAndAsksForTheSection`.
  - `Open_WhenTabChangeRefused_StaysPut`.
- [ ] **Étape 2 :** `dotnet test tests/Trofeo.Settings.Tests --filter Notifications`. Attendu : échec.
- [ ] **Étape 3 : écrire la vue et la navigation.**
  - Ajouter à `Trofeo.SettingsPreview` trois images de l'en-tête : `notifications-vide.png`, la cloche sans rond ; `notifications-probleme.png`, la cloche avec 3 notifications dont un problème ; `notifications-panneau.png`, le panneau ouvert sur ces 3 lignes.
  - Les produire avec `dotnet run --project tools/Trofeo.SettingsPreview -- --out <dossier du scratchpad>`, puis les regarder : la cloche est alignée sur la pastille, et le panneau ne déborde pas de la fenêtre.
- [ ] **Étape 4 :** `dotnet test tests/Trofeo.Settings.Tests`, puis le `dotnet test` complet de la solution. Attendu : tout passe.
- [ ] **Étape 5 :** dans `CHANGELOG.md`, section `## 1.2.0-beta.3`, ajouter une ligne : une cloche dans l'en-tête de la fenêtre rassemble les mises à jour (logiciel et modules complémentaires) et les pannes (module arrêté ou mis de côté, carte graphique) ; chaque notification se supprime d'une croix et revient si l'événement est nouveau. Puis `dotnet test tests/Trofeo.Distribution.Tests`.
- [ ] **Étape 6 : commit** `feat(reglages): cloche des notifications dans l'en-tête`.
