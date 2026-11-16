# Plan 10 — Accélération matérielle : conception

*Écrite le 16/11/2026 avec Alex, pour la préversion `1.2.0-beta.3`.*

## Intention

Le thème « Liquid Glass avancé » (plan 9) demande environ 14,5 % du processeur de la machine d'Alex sur un fond animé, contre 2 à 3 % pour Liquid Glass. Alex a accepté ce coût dans la `1.2.0-beta.2` parce que l'accélération matérielle devait l'alléger : « ça sera le but de l'accélération matérielle d'alléger la charge CPU ».

Ce plan fait passer le dessin de l'image et le décodage des vidéos sur la carte graphique, pour tous les thèmes. Au plan 4, Alex voulait déjà une accélération « totale et bien faite », désactivable, avec le choix de la carte (bilan du plan 4, « Pour plus tard »).

## Ce qu'Alex a décidé (16/11/2026)

- **Étendue de la beta.3** : le dessin de l'image et le décodage des vidéos passent sur la carte. La compression JPEG reste sur le processeur pour l'instant.
- **Tous les thèmes** profitent du dessin sur la carte, pas seulement Liquid Glass avancé.
- **Une API standard, pas le code de chaque fabricant** (sa question : « utiliser une API (DX12 ou Vulkan) ne serait pas plus simple et favoriserait la compatibilité avec tous les GPU ») : Direct3D 12, par Skia.
- **Le choix de la carte**, avec un mode « Automatique » qui prend **la carte intégrée**.
- **Le choix de ce qui est accéléré** une fois l'accélération activée : deux cases indépendantes, « Dessin de l'image » et « Décodage des vidéos ».
- **Pour plus tard, hors de ce plan** : choisir la découpe d'une photo, d'un GIF ou d'une vidéo (aujourd'hui toujours centrée), et laisser le logiciel recouper le fichier au format de l'écran pour ne pas décoder une vidéo 4K entière.

## L'essai (16/11/2026, sur le PC d'Alex)

Un programme jetable (hors du dépôt) a calculé, à 11 images par seconde, un fond qui glisse et cinq cases en verre avancé au préréglage Clair (le programme `liquid-glass.sksl` du dépôt tel quel), puis rapatrié l'image vers le processeur et l'a compressée en JPEG comme le service (qualité 88, 4:4:4). Skia sur Direct3D 12 par `GRContext.CreateDirect3D` (SkiaSharp 3.119.4), carte ouverte par Vortice.Direct3D12 3.8.3.

| Où se calcule l'image | Temps par image | Processeur de la machine (20 cœurs) | Carte (moteur 3D) | Mémoire privée en plus |
|---|---|---|---|---|
| Processeur, en bandes (comme la beta.2) | 41 ms | 12,2 % | — | — |
| **Intel(R) Graphics**, intégrée | 6,7 ms | 0,65 % | 4,3 % (pointe 5,8 %) | +55 Mo |
| NVIDIA GeForce RTX 5070 | 2,4 ms | 0,73 % | 0,3 % | +141 Mo |
| Microsoft Basic Render Driver (carte simulée, WARP) | 227 ms | 84 % | — | — |

- Les images de la carte diffèrent de celles du processeur de 3/255 au plus sur une couleur, en moyenne de 0,06 à 0,13 : identiques à l'œil.
- **La compression JPEG coûte environ 11 ms par image** (≈ 0,6 % de la machine à 11 images/s) : une fois le dessin sur la carte, c'est presque tout ce qui reste au processeur.
- **WARP n'est pas un secours** : 5 fois plus lent que le processeur. Il sert seulement aux tests sans carte graphique.
- Ni le décodage vidéo sur la carte, ni le passage de ses images à Direct3D 12, ni les ombres et le texte des modules n'ont été essayés.

## Objectifs

1. **Le dessin de l'image sur la carte**, pour les quatre thèmes : fond (image, GIF, Aurore, vidéo), panneaux, verre de Liquid Glass et de Liquid Glass avancé (ombres et pastilles comprises), modules, glissement entre les vues avec son flou, aperçu envoyé à la fenêtre de réglage. L'image finie revient au processeur une fois par image pour le JPEG, comme aujourd'hui.
2. **Le décodage des vidéos sur la carte** par Media Foundation (DXVA). Quand les deux cases sont cochées, l'image décodée passe au dessin sans repasser par le processeur.
3. **Les deux cases marchent dans toutes les combinaisons** : vidéo décodée par la carte et dessin par le processeur (l'image décodée revient alors au processeur), ou l'inverse (l'image décodée par le processeur est envoyée à la carte).
4. **Une section « Accélération matérielle » dans Paramètres** (voir « L'interface »), appliquée à chaud, sans redémarrer le service.
5. **Un repli automatique sur le processeur**, sans écran noir ni image figée, quand la carte pose problème (voir « Le repli »).
6. **Activée par défaut**, en Automatique, les deux cases cochées, sur une machine qui a une carte Direct3D 12 matérielle.
7. **Le résultat se voit dans la fenêtre** : la carte réellement utilisée, ce qui y passe, et, en cas de repli, pourquoi.

## Non-objectifs

- La compression JPEG sur la carte (mesurée : ≈ 0,6 % ; un plan suivant pourra l'ajouter comme troisième case).
- Vulkan, OpenGL, ou le code propre à un fabricant (NVENC, nvJPEG, Quick Sync).
- La découpe des fonds et le recoupage des fichiers au format de l'écran (plan suivant, demande d'Alex).
- Le dessin de la fenêtre de réglage (Avalonia), qui reste en rendu logiciel exprès (+150 Mo mesurés avec la carte, voir `src/Trofeo.Settings/Program.cs`).
- Le dessin dans les hôtes des modules complémentaires : ils dessinent toujours sur le processeur, dans leur processus ; leurs images sont envoyées à la carte par le service.
- Le décodage des GIF et des photos sur la carte : ils restent décodés par le processeur (une fois pour une photo), puis envoyés à la carte.
- Utiliser deux cartes en même temps.

## Anti-objectifs (ce qui serait un échec même si ça marche)

- **Un écran noir, figé ou qui clignote** parce que la carte a un problème (pilote mis à jour, plantage du pilote, carte désactivée).
- **La RTX tenue éveillée par défaut** sur une machine qui a une carte intégrée.
- **Une différence visible** entre l'image de la carte et celle du processeur.
- **Une mémoire qui grimpe** au fil des heures (fuite de textures), ou plus de mémoire que l'essai ne le laisse attendre sans raison mesurée.
- **Plus de processeur qu'avant** sur un fond fixe, avec l'accélération.
- **Un logiciel qui ne marche plus** sur une machine sans carte Direct3D 12 (machine virtuelle, vieux PC) : tout doit y tourner sur le processeur, comme aujourd'hui.

## Contraintes

- Windows 10 et 11 64 bits ; carte Direct3D 12 au niveau de fonctionnalités 11_0 au moins ; .NET 10.
- **SkiaSharp reste en 3.119.4** (la 4 changerait tout le dessin) : `GRContext.CreateDirect3D(GRD3DBackendContext)`, `GRBackendTexture` avec `GRD3DTextureResourceInfo`.
- Nouveaux paquets Vortice en **3.8.3**, comme Vortice.MediaFoundation (Direct3D12, DXGI, Direct3D11 au besoin), MIT, ajoutés à `LICENCES-TIERCES` et à l'installateur comme les autres.
- **Un `GRContext` ne s'utilise que sur un seul fil.** Aujourd'hui, l'image de l'écran est composée sur le fil de l'affichage, l'aperçu d'une vue cachée sur le fil d'envoi, et le verre avancé sur un fil de calcul à part (`LiquidGlassLayers`). Le dessin sur la carte doit respecter cette règle (voir « Ce qui est laissé au bâtisseur »).
- Classique, Liquid Glass et Pixel restent **identiques au pixel près sur le processeur** (empreintes SHA-256 du plan 9) ; sur la carte, ils sont comparés au rendu du processeur avec une tolérance (voir « Vérifier »).
- La machine de la recette GitHub n'a pas de carte graphique : les tests du chemin Direct3D 12 tournent sur WARP, et ceux qui exigent une vraie carte sont sautés là où il n'y en a pas.
- Les règles du projet s'appliquent : construction et tests en Debug, rien de poussé ni publié sans l'accord d'Alex, ne jamais arrêter ni lancer son service, mesures par le rechargement des réglages comme au plan 9.

## Approches envisagées

| Approche | Pour | Contre | Choix |
|---|---|---|---|
| **Skia sur Direct3D 12** | Intégré à Windows, toutes les cartes depuis ~2012 ; déjà dans SkiaSharp 3.119.4 ; les images de Media Foundation se partagent avec Direct3D ; WARP pour les tests sans carte ; l'essai le valide | Partage des textures entre Direct3D 11 (Media Foundation) et 12 à écrire | **Retenue** (Alex) |
| Skia sur Vulkan | Toutes les cartes aussi, multiplateforme | Le logiciel ne vise que Windows ; partage avec Media Foundation plus compliqué ; pas de carte simulée fournie par Windows pour la recette | Écartée |
| Skia sur OpenGL par ANGLE (Direct3D 11) | Même appareil Direct3D 11 que Media Foundation | Une couche de plus à livrer ; la fenêtre de réglage a mesuré +150 Mo par ce chemin | Écartée |
| Code de chaque fabricant (CUDA, oneVPL) | Le plus rapide sur chaque carte | Une version par fabricant, aucune pour les autres | Écartée (Alex) |

## Conception

### L'appareil graphique

- Au démarrage, et à chaque changement des réglages, le service énumère les cartes par DXGI (`IDXGIFactory6.EnumAdapterByGpuPreference`), en ignorant les cartes logicielles.
- **Automatique** prend la première carte en préférence « économie d'énergie » (`GpuPreference.MinimumPower`) : la carte intégrée quand il y en a une, sinon la seule carte.
- Une carte choisie à la main est retrouvée par son fabricant, son modèle et son nom (`VendorId`, `DeviceId`, `SubSysId`, `Description`), pas par son LUID, qui change à chaque démarrage de Windows. Si elle n'est plus là, le service prend Automatique et l'écrit dans le journal et dans la fenêtre.
- Le service crée l'appareil Direct3D 12, sa file de commandes et le `GRContext` de Skia sur cette carte. Il fixe une limite au cache de textures de Skia (valeur à mesurer ; l'essai en utilisait 19 Mo).

### Le dessin de l'image

- `FrameComposer.Compose` dessine sur une surface de la carte au lieu d'un `SKBitmap`, puis rapatrie l'image finie dans un `SKBitmap` pour le JPEG (une lecture par image, ≈ 3,5 Mo). Le reste de la boucle (`DisplayLoop`, `JpegEncoder`, envoi) ne change pas.
- **`Compose` rend toujours un `SKBitmap`** (choix de Claude) : la carte est une option du compositeur, donnée à sa construction, et tous les outils (`Trofeo.ThemePreview`, `ModuleTestHost` du SDK, empreintes des tests) restent sur le processeur par défaut. `Trofeo.Core` ne fait que recevoir un `GRContext` ; la création de l'appareil Direct3D 12 (Vortice) vit hors de Core, côté Windows, pour que Core reste testable sous Linux.
- Les modules dessinent sur le `SKCanvas` qu'on leur donne : rien ne change pour eux, ni pour les modules complémentaires dont le service pose les images.
- **Le verre avancé** se calcule sur la carte, pendant la composition : quelques millisecondes pour tout l'écran. Les couches de verre restent gardées tant que le fond, la taille et les réglages ne changent pas, pour que la carte ne travaille presque pas sur un fond fixe. Ces couches deviennent des textures de la carte, à jeter si la carte change ou disparaît.
- **La pastille de l'élément choisi** compare aujourd'hui les pixels peints de la case (`PaintPill` reçoit un `SKPixmap` de l'image en cours). Sur la carte, cette comparaison ne doit pas obliger à rapatrier toute l'image en cours de composition. La façon de décider qu'une pastille est à refaire peut changer (choix de Claude : par exemple d'après le dessin enregistré du module, ou en rapatriant seulement la case), tant que la pastille affichée reste la même à l'œil.
- Le flou du verre, le flou de mouvement du glissement, les ombres et la garde de lisibilité passent aussi sur la carte.

### Le décodage des vidéos

- `MediaFoundationBackground` reçoit un gestionnaire d'appareil Direct3D 11 (`MFCreateDXGIDeviceManager`) créé **sur la même carte** que le dessin, et active le décodage par la carte.
- Quand le dessin est aussi sur la carte, l'image décodée est partagée avec Direct3D 12 (texture partagée, synchronisée) et dessinée par Skia, recadrée à l'écran comme aujourd'hui. Aucune copie par le processeur.
- Quand le dessin est sur le processeur, l'image décodée revient au processeur, déjà à la taille de l'écran si possible.
- Le réglage actuel du décodeur logiciel (un seul fil, `DecoderTuning`) reste pour le décodage par le processeur.
- Le plafond de 12 images vidéo par seconde (`MaxCropsPerSecond`) reste, dans les deux modes : c'est déjà le plafond d'images de l'écran.
- **Changer une case ou la carte pendant qu'une vidéo joue** (choix de Claude) : la vidéo est rouverte avec le nouveau décodeur et reprend à sa position, sans repartir du début ; une image noire entre les deux n'est pas acceptable (on garde la dernière image affichée le temps de la réouverture).
- Les couleurs d'une vidéo décodée par la carte suivent la même conversion que le décodage par le processeur (matrice et plage de la vidéo) ; la comparaison se fait sur une image de la vidéo de mesure, décodée des deux façons.

### Le repli

- **Pas de carte Direct3D 12 matérielle**, échec de création de l'appareil ou du `GRContext`, programme SkSL qui ne compile pas sur la carte : la partie concernée tourne sur le processeur, une ligne au journal, et la fenêtre l'affiche.
- **Carte perdue en cours de route** (`DXGI_ERROR_DEVICE_REMOVED` ou `DEVICE_RESET` : plantage du pilote, mise à jour du pilote, carte désactivée) : l'image en cours est refaite sur le processeur, tout ce qui appartient à la carte est jeté (contexte, textures, couches de verre, décodeur), et le service continue sur le processeur. L'écran ne doit jamais rester noir ni figé.
- **Carte trop lente** (choix de Claude) : en Automatique, si le dessin sur la carte dépasse le temps d'une image (83 ms à 12 images/s) en moyenne pendant 10 s, le service essaie l'autre carte matérielle s'il y en a une, puis le processeur, et la fenêtre le dit. Une carte choisie à la main est toujours gardée ; la fenêtre signale seulement qu'elle est lente.
- **Nouvel essai** : après une perte, le service retente la carte au prochain changement des réglages, ou toutes les 5 minutes (choix de Claude), au plus 3 fois de suite ; ensuite il reste sur le processeur jusqu'au prochain démarrage ou changement des réglages.
- Le décodage vidéo se replie séparément : une vidéo que la carte refuse de décoder (format non pris en charge) est décodée par le processeur, sans toucher au dessin.

### Les réglages

- `layout.json` reçoit un champ facultatif `Acceleration` (choix de Claude : à côté des autres réglages du service, relu à chaud par `ReloadAsync` comme l'apparence) :
  - `Enabled` (vrai par défaut) ;
  - `Adapter` : absent ou vide pour Automatique, sinon l'identité de la carte (fabricant, modèle, nom) ;
  - `Drawing` et `VideoDecoding` (vrais par défaut).
- **Ces réglages sont propres à la machine** (choix de Claude) : `Acceleration` n'est pas exporté avec la disposition (`LayoutTransfer`), et une disposition importée garde les réglages d'accélération de la machine qui l'importe. La fenêtre les enregistre comme les autres réglages du service (images par seconde, apparence), dans `layout.json`, puis demande au service de relire.
- Un fichier sans ce champ donne les valeurs par défaut. Une valeur illisible prend sa valeur par défaut, avec un avertissement, comme les réglages du verre.
- Le canal de contrôle donne à la fenêtre la liste des cartes trouvées par le service et l'état réel (carte utilisée, parties sur la carte, raison d'un repli). Il passe au protocole 5.
- La ligne de commande `--appearance` n'est pas concernée ; une option `--gpu off|auto|<n°>` pour le service construit depuis le dépôt est laissée au bâtisseur.

### L'interface (onglet Paramètres)

Une section « Accélération matérielle », après « Images par seconde » :

```
Accélération matérielle
  Utiliser la carte graphique                          [ activé ]
  Carte        [ Automatique (Intel(R) Graphics)          ▾ ]
               Automatique · Intel(R) Graphics · NVIDIA GeForce RTX 5070
  ☑ Dessin de l'image (thèmes, verre, fonds animés)
  ☑ Décodage des vidéos
  Sur Intel(R) Graphics : dessin et vidéos.
```

- Interrupteur coupé : la liste et les cases sont grisées, tout est sur le processeur.
- La dernière ligne dit ce que fait vraiment le service. En cas de repli, elle passe en avertissement : « La carte graphique a eu un problème (<raison>) : dessin sur le processeur. »
- Sans carte compatible : l'interrupteur est grisé, avec « Aucune carte graphique compatible : tout se calcule sur le processeur. »
- Tous les textes tutoient, comme le reste de la fenêtre. La section « Utilisation » (processeur et mémoire du service) ne change pas.

### Vérifier

- **Tests automatiques** :
  - Classique, Liquid Glass et Pixel gardent leurs empreintes sur le processeur.
  - Les quatre thèmes, avec des modules et leur texte, sur la carte (WARP dans la recette, la vraie carte sur le PC d'Alex), sont comparés à leur rendu par le processeur (choix de Claude) : 99,9 % des pixels à 4/255 au plus par couleur, écart moyen sous 0,5/255. Les bords du texte et des tracés peuvent différer un peu plus, l'anticrénelage de la carte n'étant pas celui du processeur ; c'est accepté tant qu'Alex ne voit pas de différence.
  - Le repli est testé en simulant les pannes (création refusée, carte perdue au milieu d'une image) : l'image sort quand même, par le processeur.
  - Pas de texture qui reste après un changement de carte, de thème ou de fond : sur WARP, le test compte les ressources du cache de Skia et les objets de la carte encore ouverts. La mémoire réelle au fil d'une heure se mesure à la main, sur le PC d'Alex.
- **Mesures sur le PC d'Alex**, comme au plan 9 (changer les réglages dans `layout.json`, recharger, mesurer) :
  - les quatre thèmes sur fond fixe, puis Liquid Glass et Liquid Glass avancé sur le GIF et les deux vidéos de mesure (`apercus-verre/`) ;
  - chaque cas sans accélération, sur l'Intel et sur la RTX ;
  - processeur, images par seconde, temps de rendu, mémoire privée du service, et utilisation de la carte (compteurs « GPU Engine » de Windows, moteurs 3D et décodage vidéo).
- **Critères de réussite** :
  - Liquid Glass avancé sur fond animé : **au niveau de Liquid Glass d'aujourd'hui (2 à 3 %) ou plus bas**, à la même cadence (≈ 10,8 images/s).
  - Sur fond fixe, aucun thème ne coûte plus de processeur qu'en beta.2.
  - Mémoire en plus comparable à l'essai (≈ +55 Mo sur l'Intel), stable au fil d'une heure.
  - Alex juge à l'œil, sur son écran, que l'image est la même.

### La version

- `1.2.0-beta.3`, préversion depuis `Canary`, avec l'accord d'Alex, comme la beta.2.
- `CHANGELOG.md` reçoit une section `## 1.2.0-beta.3` : l'accélération, ses réglages, le repli, et le coût du thème avancé sur fond animé revu avec les nouvelles mesures. La carte du thème (« Verre d'iOS, plus gourmand en processeur. ») est revue si les mesures le justifient.

## Ce qui est laissé au bâtisseur

- **Le découpage en fils** : un seul `GRContext` partagé par sérialisation, ou un par fil qui dessine (affichage, envoi), avec ce que ça coûte en mémoire. Le fil de calcul du verre avancé peut disparaître en mode carte, si le calcul synchrone tient dans l'image.
- **Le partage des images vidéo** entre Direct3D 11 et 12 (texture partagée par handle NT et barrière, ou `D3D11On12`, ou Media Foundation directement sur Direct3D 12 quand Windows le permet), tant qu'aucune copie ne passe par le processeur quand les deux cases sont cochées.
- **La comparaison des pastilles** sans rapatrier l'image en cours.
- **La limite du cache de Skia** et le moment de vider les ressources inutiles.
- **L'ordre des tâches**, et les noms des classes et des fichiers.
- **L'option `--gpu`** pour le service construit depuis le dépôt.
