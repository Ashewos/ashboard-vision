# Plan 10, révision du 18/11 : le processeur dessine sur un fond fixe

Décidée par Alex le 18/11/2026, après les mesures de la tâche 9 sur son PC (1.2.0-beta.3+951d5f4). Elle complète `2026-11-16-plan-10-acceleration-materielle-design.md`, qui reste valable pour tout le reste.

## Pourquoi

Les mesures de la tâche 9 :

| Thème et fond | Processeur | Intel | RTX 5070 |
|---|---|---|---|
| Liquid Glass avancé, image fixe | 1,48 % | 1,44 % | 1,38 % |
| Liquid Glass avancé, GIF animé | 13,76 % | 1,68 % | 1,63 % |
| Liquid Glass avancé, vidéo 1080p | 12,99 % | 1,82 % | 1,65 % |

- **Sur un fond fixe, la carte n'apporte rien au processeur.**
- **Elle coûte de la mémoire au service.** Après un redémarrage, sur l'Intel, la mémoire privée passe de 102 à environ 240 Mo :
  - environ 50 Mo pour le pilote, dès que la carte est ouverte ;
  - 121 Mo de mémoire de la carte : la réserve de 48 Mo de Skia pousse le pilote à ouvrir un 2ᵉ bloc de 64 Mo.
- **Sur un fond animé, le gain est énorme** : environ 14 % → 1,7 % du processeur.

## Ce qu'Alex a décidé

- **Approche 1, « Processeur sur fond fixe ».** Sur une image fixe (Aurore comprise), le dessin se fait sur le processeur, comme dans la beta.2, et la carte n'est pas ouverte pour le dessin. Sur un GIF ou une vidéo, l'accélération matérielle s'applique comme prévu.
- **Approche 2, écartée** : garder la carte partout avec une réserve de Skia réduite à 12 Mo sur un fond fixe. La mémoire aurait été d'environ 170 Mo, toujours plus que la beta.2.

## Ce qui change

**Quand le dessin passe sur la carte.** Il faut que le réglage le demande (interrupteur allumé, « Dessin de l'image » cochée) **et** que le fond de l'écran soit animé (`IBackground.IsAnimated`).
- Avec un fond fixe, le contrôleur n'ouvre aucun appareil de dessin, et `DrawingDevice` vaut `null`.
- Quand le fond devient fixe, l'appareil ouvert est mis de côté, puis libéré dans l'ordre déjà en place (compositeurs d'abord).
- Quand le fond redevient animé, l'appareil est ouvert sur la carte choisie, comme à un `Apply`.

**Le décodage des vidéos ne change pas.** Une vidéo est un fond animé : le dessin et le décodage passent tous les deux sur la carte.

**Le glissement entre les vues** suit le compositeur : sur la carte avec un fond animé, sur le processeur avec un fond fixe.

**Ce qui reste comme avant :**
- les pannes et les nouveaux essais ;
- la carte trop lente : le jugement ne court que tant que la carte dessine, et sa fenêtre de mesure repart à zéro à chaque ouverture ;
- la cloche.

Un fond fixe n'est ni une panne ni un problème, il ne donne aucune phrase de problème.

**La ligne d'état de Paramètres.** Quand l'accélération est allumée, « Dessin de l'image » cochée, une carte disponible et le fond fixe, elle dit : « Fond fixe : l'image se dessine sur le processeur. La carte prend le relais sur un GIF ou une vidéo. »
- Le service le dit à la fenêtre par un nouveau champ optionnel en fin de liste : `AccelerationInfo.DrawingWaitsForAnimation` (`bool`, faux par défaut), et `AccelerationStatus` de même. Le protocole reste **5**.
- Avec une panne, la phrase de la panne garde la priorité, comme aujourd'hui.

**Le service apprend le fond** de la boucle d'affichage. `DisplayLoop` appelle `AccelerationController.SetBackgroundAnimated(bool animated)` sur son fil, au démarrage et à chaque changement de fond (rechargement des réglages, changement de thème). Le contrôleur ouvre ou met de côté l'appareil de dessin à ce moment, sans lever `EffectiveChoiceChanged` : la carte des vidéos ne change pas.

**Notes de version et documentation.**
- La section `## 1.2.0-beta.3` du `CHANGELOG.md` dit que le dessin passe sur la carte avec un fond animé (GIF, vidéo), et que sur un fond fixe le processeur dessine : il y est aussi léger, et le service garde la mémoire de la beta.2.
- `docs/utilisation-avancee.md` précise la même chose pour `Drawing`.

## À éviter

- **Un saut d'image ou un écran noir** au passage d'un fond fixe à un fond animé, ou l'inverse. Ouvrir la carte peut prendre un court instant ; le dessin continue sur le processeur pendant ce temps.
- **Un appareil qui reste ouvert**, ou des surfaces qui survivent à leur appareil, quand le fond devient fixe.
- **Une ouverture et une fermeture répétées** de la carte quand rien ne change, par exemple si un rechargement ou un nouvel essai rappelle `SetBackgroundAnimated` avec la même valeur.

## Vérification

- Tests du contrôleur :
  - fond fixe : aucun appareil créé, et le nouveau champ est vrai ;
  - fond animé : l'appareil est créé ;
  - passage d'animé à fixe : l'appareil est mis de côté puis libéré ;
  - même valeur rappelée : rien ne change ;
  - panne pendant un fond animé, puis passage à un fond fixe : la phrase de panne et la règle des nouveaux essais restent cohérentes.
- Tests de la boucle : `SetBackgroundAnimated` est appelé au démarrage et quand le fond change, et le compositeur est refait sans appareil sur un fond fixe.
- Test de la fenêtre : la nouvelle ligne d'état.
- Sur le PC d'Alex, à refaire avec le nouvel installateur :
  - les mesures de la tâche 9 ;
  - la mémoire à froid sur une image fixe, qui doit revenir à environ 100 Mo ;
  - un passage d'une image fixe à une vidéo et retour, sans à-coup visible.
