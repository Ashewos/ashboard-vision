# Cloche des notifications — conception

Demandée par Alex le 17/11/2026, pour la même préversion que l'accélération matérielle (**1.2.0-beta.3**). Construite sur la branche `plan-10-gpu`, avant sa fusion dans `Canary`.

## Pourquoi

Ce qui demande l'attention d'Alex est aujourd'hui éparpillé dans la fenêtre de réglage :
- la mise à jour du logiciel est dans Paramètres ;
- celle d'un module complémentaire est dans la section « Modules complémentaires » de Paramètres ;
- un module mis de côté est au même endroit ;
- une panne de la carte graphique est dans Paramètres.

Une icône dans l'en-tête les rassemble, pour qu'on ne les rate pas.

## Ce qu'Alex a décidé

- **L'icône.** Une icône de cloche, sans texte, à droite de la pastille « Écran branché ».
- **Ce qu'elle montre : les mises à jour et les pannes, rien d'autre.**
  - Elle ne montre pas un module hors ligne (service injoignable, appareil éteint…).
  - Elle ne montre pas un raccourci refusé par Windows.
- **La croix.** Chaque notification a une croix qui la supprime.
- **Une notification revient quand l'événement est nouveau** :
  - une autre version qui sort ;
  - une deuxième panne.
- **Approche 1 : la fenêtre compose la liste elle-même** à partir de ce que le service lui envoie déjà. Le service n'ajoute que ce qui lui manque pour dater les pannes. Les croix sont retenues par la fenêtre, sur ce PC.
  - L'approche 2 a été écartée : le service aurait tenu un vrai journal des notifications, dans un fichier, avec les croix et de nouveaux messages dans le canal. C'était plus de code pour un besoin que l'approche 1 couvre.

## Les notifications

Chaque notification a :
- une **clé**, qui dit ce qui la rend unique : supprimée, elle reste supprimée tant que sa clé ne change pas ;
- un **type** : nouveauté ou problème ;
- une **phrase** en français, qui tutoie ;
- un **lien** qui mène au bon endroit.

| Événement | Clé | Type | Phrase | Lien |
|---|---|---|---|---|
| Mise à jour du logiciel proposée (`UpdateInfo.State == Available`, rétrogradation comprise) | `app:<version>` | nouveauté | « Ashboard Vision <version> est disponible. » | « Voir la mise à jour » → onglet Paramètres |
| Mise à jour d'un module complémentaire (`CommunityModuleInfo.UpdateVersion`) | `update:<id>:<version>` | nouveauté | « <module> <version> est disponible. » | « Voir le module » → onglet Paramètres, section « Modules complémentaires » |
| Arrêt brusque d'un module complémentaire, relancé ensuite | `stop:<id>:<heure du dernier arrêt>` | problème | « <module> s'est arrêté brusquement et a été relancé. » ; à partir du 2ᵉ arrêt depuis le démarrage du service : « … (<n> fois) » | « Voir le module » → onglet Paramètres, section « Modules complémentaires » |
| Module mis de côté après des pannes répétées (`CommunityState.SetAside`) | `aside:<id>:<heure de la mise de côté>` | problème | « <module> a été mis de côté après des arrêts répétés. » | « Voir le module » → onglet Paramètres, section « Modules complémentaires » |
| Panne de la carte graphique (`AccelerationInfo.Problem` d'une panne, pas d'une carte trop lente ni absente) | `gpu:<heure de la panne>` | problème | la phrase du service, telle quelle | « Voir les réglages » → onglet Paramètres |

Règles :
- **Une seule notification d'arrêt par module.** Quand le module est mis de côté, la notification « mis de côté » remplace celle de l'arrêt.
- **Une notification disparaît d'elle-même quand sa cause n'existe plus** :
  - la mise à jour est installée ou n'est plus proposée ;
  - le module est relancé (il n'est plus de côté) ou désinstallé ;
  - la carte graphique n'a plus de problème.
- **Une notification d'arrêt (sans mise de côté) reste** jusqu'à la croix ou au redémarrage du service. Le service ne garde les heures d'arrêt qu'en mémoire.
- **Ordre de la liste** : les problèmes d'abord, puis les nouveautés, les plus récents en haut.

## Ce que le service ajoute

- **Les arrêts brusques d'un module complémentaire.** `CommunityModuleInfo` gagne `LastStopAt` (`DateTimeOffset?`) et `StopCount` (`int`).
  - Ils comptent les arrêts que personne n'a voulus depuis le démarrage du service, comme ceux que `ModuleHostSupervisor` compte déjà pour la mise de côté. Un arrêt pour mémoire dépassée en fait partie.
  - Les deux paramètres sont optionnels, en fin de liste : le protocole reste **5**, car la beta.3 n'est pas encore publiée.
- **L'heure de la mise de côté.** Elle est enregistrée avec sa raison, dans la liste des modules validés (`SetAsideAt`, optionnel), et envoyée dans `CommunityModuleInfo.SetAsideAt`.
  - Sans elle, une croix sur « mis de côté » serait oubliée au prochain démarrage.
  - Une mise de côté plus ancienne, sans heure, a pour clé `aside:<id>:`.
- **L'heure de la panne de la carte graphique.** `AccelerationInfo` gagne `ProblemAt` (`DateTimeOffset?`), l'heure de la panne en cours.
  - Le type de problème est aussi transmis, pour que la fenêtre distingue une panne d'une carte trop lente ou absente : un `ProblemKind` (`Failure`, `Slow`, `Gone`, `null`).

## La fenêtre

- **L'icône.**
  - Une cloche dessinée en tracé (pas d'emoji, pas de texte). Bulle d'aide et nom pour l'accessibilité : « Notifications ».
  - Avec des notifications, un petit rond porte leur nombre : rouge s'il y a au moins un problème, couleur d'accent sinon.
- **Le panneau.** Un clic ouvre un panneau sous l'icône, dans le style de la fenêtre.
  - Chaque ligne a une icône de type, la phrase, le lien en dessous et une croix à droite.
  - Sans notification, le panneau dit « Rien de nouveau. ».
  - Un clic sur un lien ferme le panneau et ouvre l'onglet.
- **Les croix sont retenues sur ce PC.** Les clés supprimées sont gardées dans les préférences de la fenêtre (`settings-app.json`, `EditorPreferences`).
  - Elles ne vont pas dans l'export de la disposition.
  - À chaque mise à jour de la liste, on ne garde que les clés encore présentes, pour que le fichier ne grossisse pas.
- **Les données** sont celles que la fenêtre lit déjà :
  - l'état du service, toutes les deux secondes environ (accélération) ;
  - les mises à jour du logiciel ;
  - la vue d'ensemble des modules complémentaires (mises à jour, arrêts, mises de côté).

  La vue d'ensemble des modules n'est relue aujourd'hui que quand les types de modules changent : la cloche la relit elle-même à la connexion puis toutes les **30 secondes**, pour voir les arrêts et les mises à jour.
- **Aperçus.** `Trofeo.SettingsPreview` gagne des images de l'en-tête avec la cloche fermée sans rien, la cloche avec un problème, et le panneau ouvert.
- **Textes.** Tous les textes vont dans `Strings.resx`.

## Hors de la demande

- Les notifications Windows (toasts). L'icône près de l'horloge garde ce qu'elle fait déjà pour les mises à jour du logiciel.
- Un historique des notifications supprimées.
- Les modules d'origine (dans le service) : ils n'ont pas de mises à jour séparées, et leurs pannes ne mettent rien de côté.

## À éviter

- **Une cloche bruyante** : ni un état qui clignote (hors ligne ou en ligne), ni une notification qui revient alors que rien de nouveau n'est arrivé.
- **Une croix qui ne tient pas** après la fermeture de la fenêtre ou un redémarrage.

## Laissé au bâtisseur

- Le dessin exact de la cloche et du panneau, dans le style des contrôles voisins.
- Le découpage des classes, par exemple un `NotificationsViewModel` à part, comme `ScreenPillViewModel`.
