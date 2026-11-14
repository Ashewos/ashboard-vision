# Plan 9 — Thème « Liquid Glass avancé » : conception

Conception discutée avec Alex le 14/11/2026. Cible : préversion **`v1.2.0-beta.2`**, publiée depuis `Canary`. La stable 1.2.0 attend ce thème.

## Intention

Alex veut un verre liquide qui ressemble vraiment à celui d'iOS : la déformation du fond sur les bords, les franges de couleur, les reflets, la lumière des bords. Il ne veut pas un effet générique qui « fait IA » (anti-objectif n° 1). Il veut aussi pouvoir régler tous les paramètres de l'effet, avec quelques préréglages pour aller vite.

Ce thème consommera plus que les autres. C'est pourquoi il s'ajoute aux trois existants au lieu de remplacer Liquid Glass : on ne perd pas le verre actuel, plus sobre.

La référence visuelle est la bibliothèque web [ybouane/liquidglass](https://github.com/ybouane/liquidglass) et sa [démo](https://liquid-glass.ybouane.com/). Son effet est jugé très complet. On l'adapte.

## Ce qu'Alex a décidé

- **Un quatrième thème**, à côté de Classique, Liquid Glass et Pixel. Ces trois-là ne changent pas d'un pixel.
- **On adapte l'effet de liquidglass sous licence MIT, en citant l'auteur.** Le dépôt GitHub n'a pas de fichier LICENSE, mais son `package.json` et le paquet npm `@ybouane/liquidglass` (1.0.3) sont déclarés `"license": "MIT"` depuis le 10/04/2026. Le bilan du plan 4 disait « aucune licence », car il ne regardait que la détection de GitHub. Le MIT est compatible avec la GPL-3.0 du projet.
- **Le verre est immobile.** Pas de lumière qui bouge, pas d'ondulation. L'effet d'origine n'a d'ailleurs pas de notion de temps.
- **Tous les réglages valent pour toutes les cases** (pas de verre case par case).
- **Aucune différence visible entre un module natif et un module de la communauté.** La case est dessinée par le service pour tous les modules. La pastille de l'élément choisi est elle aussi dessinée par le service, jamais par le module (voir plus bas).
- **La façon de construire : adapter son programme de dessin (GLSL) en SkSL**, exécuté par le processeur (`SKRuntimeEffect`, disponible dans SkiaSharp 3.119.4). Le résultat est gardé en mémoire tant que rien ne change.
- **L'onglet choisi du module Claude Code / Codex** devient une petite pastille de verre dans ce thème. Elle s'inspire du mode « bouton » de liquidglass.
- Les tâches de rendu (l'effet, les performances, le réglage fin) et la relecture finale passent sur **Fable 5.2**, le reste sur Sonnet, effort élevé. C'est une exception à la règle habituelle « sous-agents sur Sonnet », proposée par Claude et acceptée par Alex.

## Objectifs

1. Un thème `liquid-glass-avance` (« Liquid Glass avancé »), choisi dans l'onglet Apparence comme les autres.
2. Un rendu fidèle à l'effet de liquidglass, et donc à iOS :
   - la déformation du fond (lentille biconvexe ou dôme) ;
   - les franges de couleur ;
   - un fond flou au centre et net sur les bords ;
   - le Fresnel et les reflets de quatre lumières fixes ;
   - le trait intérieur, la lumière et la lueur des bords ;
   - la teinte froide, la saturation et la luminosité ;
   - l'ombre portée ;
   - les bords lissés.
3. Les 17 réglages utiles de liquidglass, exposés dans l'interface, avec 4 préréglages et un état « Personnalisé ».
4. Une aide `ModuleDrawing.DrawSelection`, pour les modules natifs et ceux de la communauté (SDK 1.2.0), qui dessine l'élément choisi dans le style de chaque thème.
5. Sur un fond fixe, une consommation proche de celle du thème Liquid Glass actuel.
6. La préversion `v1.2.0-beta.2`.

## Non-objectifs

- **L'accélération matérielle** (composition ou effet sur la carte graphique) : c'est un chantier à part. Le programme SkSL écrit ici doit pouvoir y passer tel quel plus tard, mais rien n'est préparé pour la carte graphique dans ce plan.
- Le verre case par case.
- Un verre animé : lumière mobile, ondulation, réaction au mouvement.
- Les modes `floating` (déplacer à la souris) et `button` (survol, appui) de liquidglass. L'écran n'a pas de souris. Seul l'aspect « bouton » inspire la pastille.
- Changer les thèmes Classique, Liquid Glass ou Pixel. Seule l'aide `DrawSelection` dessine, dans ces thèmes, exactement ce que le module Claude Code / Codex dessine aujourd'hui.

## Anti-objectifs (ce qui serait un échec même si ça marche)

- **Un rendu « presque iOS »** : un flou avec un contour blanc, des reflets plaqués, des franges qui bavent partout. Le juge, c'est l'œil d'Alex, sur l'écran et sur les aperçus comparés à la démo.
- **Une différence visible entre un module natif et un module de la communauté** dans ce thème.
- **Une consommation qui grimpe sur un fond fixe** parce que le verre serait recalculé à chaque image.
- Un réglage qui casse l'écran : une ombre dessinée par-dessus le verre ou le contenu d'une case voisine, un rayon plus grand que la case, un texte illisible sur un fond clair alors que la garde de lisibilité est activée.

## Contraintes

- **.NET 10, SkiaSharp 3.119.4, dessin par le processeur**, comme le reste du rendu (`FrameComposer`, `PanelPainter`, `Backdrop`).
- L'écran fait 1920 × 462 pixels réels. Les « pixels CSS » de liquidglass deviennent des pixels de l'écran, à l'échelle 1.
- **Licence.**
  - Le texte MIT, avec la mention « Copyright (c) ybouane », accompagne le code adapté dans le dépôt : un fichier à côté du programme SkSL, et un en-tête dans ce fichier.
  - Il part aussi dans la publication, donc dans l'installateur, comme les licences OFL des polices.
  - Le README le cite dans ses remerciements, ou dans une section sur les licences tierces.
- **Isolation des modules de la communauté.** Un module ne voit jamais le fond de l'écran ni le contenu des autres cases. Rien dans ce plan ne doit lui donner ces pixels.
- **Compatibilité du SDK.**
  - Un module construit avec le SDK 1.0 ou 1.1 continue de marcher et voit un thème de forme `Rounded`.
  - Un module qui appelle `DrawSelection` déclare `"sdk": "1.2"`.
  - Le SDK n'est jamais publié sur nuget.org.
- **Les règles du dépôt** (`CLAUDE.md`) s'appliquent : Debug seulement, `Canary`, rien de poussé ni publié sans l'accord d'Alex. Le service et la fenêtre d'Alex ne sont jamais lancés ni arrêtés par Claude.

## Approches envisagées

1. **Adapter le programme de dessin en SkSL, calculé par le processeur, avec le résultat gardé en mémoire (choisie par Alex).**
   - C'est le plus fidèle à la démo.
   - Sur un fond fixe, la couche de verre de chaque case est calculée une fois.
   - Sur une vidéo, elle est recalculée à chaque image. C'est le cas cher, à mesurer.
   - Le même SkSL pourra tourner sur la carte graphique le jour de l'accélération matérielle.
2. **Une carte de déplacement calculée d'avance**, avec le filtre `DisplacementMap` de Skia et des reflets en images.
   - Plus rapide sur une vidéo.
   - Mais moins précise : déplacements sur 8 bits, franges en trois passes. Elle s'éloigne de l'effet d'origine, avec le risque d'un rendu « presque iOS ».
3. **Tout sur la carte graphique.**
   - Le plus rapide.
   - Mais c'est en réalité le chantier de l'accélération matérielle (mémoire du pilote, carte tenue éveillée, choix de la carte), trop gros pour une beta.2.

## Conception

### Le thème

- **Identifiant et nom.** L'identifiant est `liquid-glass-avance`. Le nom affiché est « Liquid Glass avancé ».
- **Ce qu'il reprend de Liquid Glass :**
  - la palette, la police Manrope et `FeedTypeface` ;
  - `Shape = Rounded` ;
  - le fond par défaut Aurore et les écarts du damier.
- **Le panneau.** Un nouveau `PanelKind`, par exemple `LiquidGlass`, porte les réglages du verre.
- **La carte de thème.** Dans Apparence, elle avertit, comme le fait déjà l'avertissement de thème : ce thème demande plus au processeur, surtout avec un fond animé ou une vidéo.

### L'effet

Le programme `FS_GLASS` de liquidglass, avec son flou séparé, est adapté en SkSL. Toutes ses étapes sont reprises, avec les mêmes formules et les mêmes constantes :

- la distance au rectangle arrondi et le biseau ;
- la normale de la surface ;
- la réfraction, biconvexe ou en dôme ;
- la micro-déformation ;
- les franges de couleur sur l'image nette et sur l'image floutée ;
- le mélange flou et net selon le bord ;
- la luminosité, la saturation et la teinte ;
- le Fresnel, les quatre lumières, le trait intérieur, la lumière et la lueur des bords, et le faux reflet d'environnement ;
- l'ombre (portée et de contact) ;
- le masque lissé.

**Ce qui change par rapport à l'effet d'origine :**

- **Ce que le verre déforme :** le fond de l'écran seulement, à la taille réelle, plus sa copie floutée. Les cases ne se chevauchent pas. Le contenu des modules est dessiné après, par-dessus le verre, comme aujourd'hui.
- **Le flou.** Le paramètre « flou » (0 à 1) devient un rayon de flou de Skia. Pour être équivalent aux 6 passes de flou gaussien à 9 points de l'original, la correspondance est calibrée en comparant avec la démo. La copie floutée peut rester au quart de la taille (`Backdrop`) si la comparaison ne montre pas de différence.
- **Ce qui se pose sur le verre** *(choix de Claude)* :
  - Seule la teinte du panneau (`Theme.Panel` à l'opacité appliquée) se pose sur la couche de verre. Elle n'est pas gardée dans la couche.
  - Le reflet en dégradé, le trait du bord haut et le contour de 1 px du thème Liquid Glass ne sont pas repris : l'effet a déjà son propre trait intérieur et sa propre lumière des bords.
  - L'opacité de départ est de 0,22, comme Liquid Glass, ajustable pendant les aperçus.
- **La garde de lisibilité** *(choix de Claude)*.
  - Elle s'applique, sauf si l'utilisateur l'a désactivée. Mais elle ne peut pas rester telle quelle : le nouveau verre peut éclaircir le fond (luminosité, reflets, Fresnel, bords nets).
  - Pour ce thème, la luminance qu'elle utilise est **mesurée sur la couche de verre déjà calculée** de la case (sa zone la plus claire, sur une copie réduite), et non sur le fond.
  - La mesure est faite une fois par calcul de la couche, et gardée avec elle. La teinte renforcée se pose ensuite par-dessus.
  - Un test vérifie qu'avec la garde, le texte reste lisible sur un fond blanc avec le préréglage Prisme.
- **Le curseur général « Opacité ».** Il garde son sens dans tous les thèmes : c'est l'opacité de la teinte du panneau. Le réglage `opacity` de liquidglass devient « Opacité du verre », dans le groupe Couleur.
- **Les ombres** *(choix de Claude)*.
  - Elles sont dessinées dans une première passe, pour toutes les cases de la vue, avant tous les verres. L'ombre d'une case peut ainsi passer dans l'écart et sous le verre de sa voisine, comme sur la démo, et rien ne la coupe.
  - Les curseurs d'ombre gardent les bornes de la démo.
  - L'ombre d'une case est gardée en mémoire à part de son verre. Sa marge dépend de l'étendue et du décalage.

### Garder le verre en mémoire (la sobriété)

- **Pour chaque case**, deux couches sont calculées une fois : son ombre (sur le rectangle élargi de la marge d'ombre) et son verre (sur le rectangle de la case), avec la luminance mesurée pour la garde.
- **Elle est gardée** tant que ne changent pas :
  - l'instance du fond et sa `Version` ;
  - le rectangle de la case ;
  - les réglages du verre ;
  - le thème et l'opacité appliquée par la garde.
- **Sur un fond fixe**, chaque image se contente de poser ces couches. C'est le cas d'Aurore, d'une couleur ou d'une image fixe.
- **Sur un fond animé** (GIF, WebP, vidéo), la couche est recalculée à chaque nouvelle image du fond, pour les cases de la vue affichée seulement.
  - On mesure d'abord.
  - Si le coût est trop élevé aux yeux d'Alex, on calcule la couche à une résolution réduite, puis on l'agrandit. Le bâtisseur choisit le facteur à partir des mesures.
- **Pendant le glissement entre deux vues**, chaque vue a ses propres couches en mémoire. Le flou de mouvement ne change pas.
- **Les couches des cases retirées** sont libérées (aucune fuite quand on change de disposition).
- **Où se fait le calcul** *(choix de Claude)*.
  - Le bâtisseur mesure d'abord le temps de calcul des couches d'une vue entière.
  - S'il dépasse une image, les couches se calculent sur un fil à part. Pendant ce temps, l'écran montre l'ancienne couche, ou à défaut le verre simple du thème Liquid Glass.
  - Ce cas se présente à l'arrivée d'une vue pendant le glissement, et quand on bouge un curseur. Le glissement ne doit jamais saccader à cause du verre.
  - Les vues voisines de la vue affichée peuvent être calculées d'avance, si la mémoire mesurée le permet.

### La pastille de l'élément choisi : `ModuleDrawing.DrawSelection`

- **L'aide publique.** `ModuleDrawing.DrawSelection(SKCanvas canvas, SKRect rect, ModuleRenderContext ctx)` dessine « l'élément choisi est ici ». Le module dessine son texte après.
- **Dans les thèmes Classique, Liquid Glass et Pixel**, elle dessine ce que `AgentPainter.DrawTab` dessine aujourd'hui, au pixel près :
  - le rectangle `Surface` ;
  - un trait de 3 px dans l'accent, ou de 4 px dans `Highlight` en Pixel.
- **Dans Liquid Glass avancé, le dessin est fait par le service :**
  - **La règle, la même pour tous** *(choix de Claude)* : les pastilles passent **sous tout ce que le module dessine**, quel que soit le moment où il appelle l'aide.
  - **Module natif.** `ModuleRenderContext` porte un dessinateur interne, fourni par le service.
    - Dans ce thème, le dessin du module est enregistré (`SKPictureRecorder`), et l'aide y note les rectangles.
    - Le service dessine ensuite les pastilles, puis rejoue le dessin du module par-dessus.
  - **Module de la communauté.**
    - Dans l'hôte, l'aide note le rectangle. La liste des rectangles voyage avec l'image du module (protocole de `Trofeo.ModuleHost` et `RemoteModule`).
    - Le service dessine les pastilles, puis pose l'image du module par-dessus.
    - Ces rectangles sont validés : coupés à la case, en nombre limité, et un rectangle vide ou invalide est ignoré.
  - Les deux chemins produisent **le même dessin**. Un test le vérifie, avec un module qui dessine avant et après l'appel.
- **La pastille** est le même programme SkSL, en forme de pilule.
  - Son rayon est la moitié de sa hauteur. Sa profondeur de biseau est celle du réglage, ramenée à la moitié de sa hauteur au plus.
  - Elle n'a pas d'ombre portée, et elle est coupée à la case.
  - Elle déforme ce qui se trouve sous elle (le fond et la couche de verre de la case), jamais le contenu des modules.
  - Elle est gardée en mémoire avec la même règle que les cases.
- **Sans dessinateur** (un ancien banc d'essai, par exemple), l'aide dessine une approximation locale : une pilule claire avec un trait intérieur. Le banc d'essai du SDK (`AshboardVision.Sdk.Testing`) et `ashboard-module check` doivent, eux, montrer la vraie pastille.
- **Le module Claude Code / Codex** utilise `DrawSelection` pour son onglet choisi. Son rendu dans les trois autres thèmes reste identique.

### Les réglages

- **17 réglages**, rangés en quatre groupes dans l'interface. Les noms entre parenthèses sont ceux de liquidglass.

| Groupe | Réglage affiché | liquidglass | Valeur par défaut d'origine |
|---|---|---|---|
| Verre | Flou | `blurAmount` | 0 |
| Verre | Réfraction | `refraction` | 0,69 |
| Verre | Profondeur du biseau | `zRadius` | 40 |
| Verre | Forme du biseau (Pilule / Dôme) | `bevelMode` | Pilule |
| Verre | Rayon des angles | `cornerRadius` | 65 (ici **30**, voir plus bas) |
| Verre | Micro-déformation | `distortion` | 0 |
| Lumière | Lumière des bords | `edgeHighlight` | 0,05 |
| Lumière | Reflets | `specular` | 0 |
| Lumière | Fresnel | `fresnel` | 1 |
| Lumière | Franges de couleur | `chromAberration` | 0,05 |
| Couleur | Opacité du verre | `opacity` | 1 |
| Couleur | Saturation | `saturation` | 0 |
| Couleur | Teinte froide | `tintStrength` | 0 |
| Couleur | Luminosité | `brightness` | 0 |
| Ombre | Opacité de l'ombre | `shadowOpacity` | 0,3 |
| Ombre | Étendue | `shadowSpread` | 10 |
| Ombre | Décalage vertical | `shadowOffsetY` | 1 |

- **Les bornes des curseurs** reprennent celles de la démo de liquidglass. Le bâtisseur les lit sur la page de la démo, dans le navigateur intégré. Une limite est propre à l'écran : le rayon des angles.
- **Le rayon des angles** *(choix de Claude)*.
  - Il vaut **30** par défaut, dans les quatre préréglages : c'est le rayon du thème Liquid Glass. Les 65 de la démo dépassent les petites cases, qui font environ 108 × 99 px.
  - Son curseur va de 0 à 49, soit la moitié du plus petit côté d'une case 1 × 1.
  - Le programme le ramène en plus, case par case, à la moitié du plus petit côté de la case, comme l'original.
  - Il devient aussi le `CornerRadius` du thème que voient les modules.
- **Les préréglages.** Quatre préréglages, plus « Personnalisé ». Les valeurs ci-dessous sont un point de départ, proposé par Claude, à ajuster avec Alex sur les aperçus et l'écran :

| Préréglage | Intention | Point de départ |
|---|---|---|
| **Clair** (par défaut) | Le verre clair d'iOS | flou 0,1 ; réfraction 0,69 ; franges 0,05 ; bords 0,3 ; reflets 0,4 ; Fresnel 1 ; saturation 0,2 ; luminosité 0,05 ; ombre 0,25 |
| **Givré** | Plus flou, déformation douce | flou 0,6 ; réfraction 0,4 ; franges 0,02 ; bords 0,25 ; reflets 0,25 ; Fresnel 0,8 ; saturation 0,3 ; teinte 0,1 ; luminosité 0,08 |
| **Prisme** | Franges et reflets marqués | flou 0,15 ; réfraction 0,9 ; franges 0,25 (la démo borne ce réglage à 0,3) ; bords 0,5 ; reflets 0,8 ; Fresnel 1,2 |
| **Discret** | Tout en douceur | flou 0,3 ; réfraction 0,3 ; franges 0 ; bords 0,1 ; reflets 0,1 ; Fresnel 0,5 |

  Les valeurs non citées gardent celles d'origine, sauf le rayon des angles (30). Bouger un curseur fait passer la sélection sur « Personnalisé ». Choisir un préréglage remplace les 17 valeurs.
- **L'enregistrement.**
  - `AppearanceSettings` gagne un champ facultatif pour le verre : le préréglage choisi et les 17 valeurs.
  - Un fichier sans ce champ donne le préréglage Clair.
  - Une valeur hors bornes est ramenée dans les bornes. Une valeur illisible prend celle du préréglage.
  - Le relais de l'apparence vers l'hôte des modules (`HostSession.ResolveTheme`) transmet ce champ, pour que `DrawSelection` y ait accès.

### L'interface (onglet Apparence)

- **La section « Verre »** n'apparaît que lorsque Liquid Glass avancé est choisi. Elle se place sous les cartes de thème.
- **Le contenu de la section :**
  - une rangée de préréglages ;
  - l'indication « Personnalisé » ;
  - un repli « Réglages fins », qui contient les quatre groupes de curseurs.

```
Verre   [ Clair ] [ Givré ] [ Prisme ] [ Discret ]  Personnalisé
        ▸ Réglages fins
            Verre    Flou ─●──────  Réfraction ────●──  …
            Lumière  Bords ●───────  Reflets ──●─────  …
            Couleur  …
            Ombre    …
```

- **Comme le reste de la fenêtre :**
  - l'écran se met à jour en direct pendant le réglage ;
  - chaque changement passe par la session d'édition, avec annulation partagée ;
  - les textes viennent de `Strings.resx` et tutoient ;
  - la section est accessible au clavier et aux lecteurs d'écran ;
  - les aperçus de `Trofeo.SettingsPreview` montrent la nouvelle section.

### Le SDK 1.2.0

- **Le contenu :**
  - `DrawSelection` ;
  - le quatrième thème dans `ashboard-module check` (une image PNG par taille et par thème) et dans `AshboardVision.Sdk.Testing`.
- **Le numéro.** Les paquets passent en 1.2.0, et le manifeste accepte `"sdk": "1.2"`.
- **La documentation** est mise à jour :
  - `docs/sdk/dessin.md` : quatre thèmes, `DrawSelection`, avec un exemple ;
  - `compatibilite.md` ;
  - `contrat.md`.

  Les extraits restent vérifiés par `DocsTests`.

### Vérifier que ça ressemble à iOS

1. **Les aperçus.** `Trofeo.ThemePreview` produit des images du nouveau thème pour chaque préréglage, sur Aurore, sur une image claire et sur une image sombre, avec le module Claude Code / Codex et sa pastille.
2. **La comparaison avec la référence.**
   - La démo de liquidglass est ouverte dans le navigateur intégré, avec le même fond et les mêmes valeurs.
   - On compare côte à côte la forme de la déformation, les franges, les reflets et les bords.
   - Les écarts sont notés et corrigés avant de montrer le thème à Alex.
3. **Le jugement d'Alex** sur les aperçus, puis sur son écran avec la bêta. Ses retours ajustent les préréglages.

### Les mesures (sur l'écran d'Alex, comme au plan 4)

- **Ce qu'on mesure :** le processeur et la mémoire du service, en Liquid Glass avancé (Clair), sur :
  - Aurore ;
  - une image fixe ;
  - un GIF 1920 × 462 ;
  - une vidéo 1920 × 462 ;
  - une vidéo 1080p.
- **La comparaison** se fait avec le tableau du bilan du plan 4 pour Liquid Glass.
- **L'objectif** : sur un fond fixe, un écart faible avec Liquid Glass. Sur une vidéo, le chiffre est présenté à Alex, qui décide s'il faut calculer la couche à une résolution réduite.

### La version

- **Les fichiers de version :**
  - `Directory.Build.props` passe à `1.2.0-beta.2` ;
  - `CHANGELOG.md` reçoit une section `## 1.2.0-beta.2` : le nouveau thème, ses préréglages et ses réglages, `DrawSelection` et le SDK 1.2.0, et la mention de liquidglass sous MIT.
- **La publication.** On suit la procédure habituelle (voir `CLAUDE.md`) : Alex construit et essaie l'installateur, puis le commit, `Canary` et l'étiquette sont poussés avec son accord, une action après l'autre.
- **Le bilan du plan 9** est écrit à la fin.

## Ce qui est laissé au bâtisseur

- L'organisation du code de l'effet (fichiers, classes), tant que les autres thèmes ne changent pas.
- **La correspondance du flou**, calibrée sur la démo.
- La résolution des couches de verre sur un fond animé, à décider après les mesures.
- La forme exacte des rectangles de sélection dans le protocole de l'hôte (nombre maximal, format), tant qu'ils sont validés.
- Les bornes exactes de chaque curseur, lues sur la démo, avec la limite du rayon ci-dessus.
- Le seuil de calcul sur un fil à part, et le calcul d'avance des vues voisines, à décider après les mesures.
- La mise en page fine de la section Verre, dans le style des autres sections d'Apparence.
