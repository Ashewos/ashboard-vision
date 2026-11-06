# Publier

Ton module marche. Cette page explique comment le donner aux autres : fabriquer son fichier `.avmod`, le publier sur GitHub, et laisser le logiciel proposer les mises à jour.

Il n'y a pas de boutique. Tu publies ton fichier où tu veux ; en pratique, dans les **versions GitHub** de ton dépôt, parce que c'est là que le logiciel cherche les mises à jour.

## Avant de publier

- [ ] `dotnet test` est vert.
- [ ] `ashboard-module check` conclut « Compatible », et tu as regardé les images PNG qu'il a écrites.
- [ ] Tu as essayé le module dans Ashboard Vision, avec le [mode développeur](demarrer.md#le-voir-dans-ashboard-vision) et ses réglages remplis.
- [ ] Ton manifeste a un `id` définitif (il ne change plus), une `version` nouvelle, une `description` claire, et ne demande que les permissions dont tu as besoin.
- [ ] Si tu veux que le logiciel propose les mises à jour, le champ `repository` du manifeste nomme ton dépôt (voir plus bas).

## Fabriquer le `.avmod`

Un `.avmod` est une archive ZIP : `manifest.json` à la racine, ta DLL, ses dépendances, et ce que ton module lit (une icône, par exemple). Le logiciel installe un `.avmod` quand l'utilisateur double-clique dessus.

Construis ton module, puis, dans un terminal :

```powershell
ashboard-module pack MonModule/bin/Debug/net10.0 --out pack-out
```

Pour publier, tu peux construire en Release (`dotnet build -c Release`) et donner le dossier `bin/Release/net10.0`. Sans dossier, la commande prend le dossier où tu es. Sans `--out`, elle écrit dans `pack-out`, dans le dossier où tu es (jamais dans le dossier du module : elle le refuse, pour ne pas ranger le paquet dans lui-même).

Elle vérifie ton manifeste, puis fabrique deux fichiers :

```text
✔ Manifeste : ashewos.demo 1.0.0
✔ Paquet : C:\dev\demo\pack-out\ashewos.demo-1.0.0.avmod
✔ Empreinte : C:\dev\demo\pack-out\ashewos.demo-1.0.0.avmod.sha256

Joins ces deux fichiers à ta version GitHub (étiquette v1.0.0).
```

- `<id>-<version>.avmod` : le paquet.
- `<id>-<version>.avmod.sha256` : son **empreinte SHA-256**, une suite de caractères qui identifie le fichier au bit près. Elle est écrite comme le fait l'outil `sha256sum` (l'empreinte, deux espaces, le nom du fichier). Elle sert à vérifier qu'un téléchargement n'est ni abîmé ni détourné.

Ces noms sont **imposés** : le logiciel cherche exactement `<identifiant>-<version>.avmod` et le même nom suivi de `.sha256`.

Le même dossier donne le même paquet, octet pour octet.

### Ce qui entre dans le paquet

**Tous les fichiers du dossier.** Si ton dossier de sortie contient des fichiers que tu ne veux pas publier, enlève-les d'abord. Les copies de `AshboardVision.Sdk.dll` et de SkiaSharp que ta construction y dépose sont inutiles (l'hôte utilise les siennes), mais elles ne gênent pas.

Le paquet est inspecté **avant** d'être écrit. Il est refusé, sans rien laisser sur le disque, si :

- il contient plus de 2 000 fichiers, ou plus de 50 Mio une fois décompressé ;
- un chemin dépasse 200 caractères ou 16 niveaux de dossiers ;
- un chemin est dangereux (`..\`, un chemin absolu) ou porte un nom réservé par Windows (`CON`, `NUL`, `COM1`…) ;
- le manifeste est invalide.

## Publier sur GitHub

1. Dans le manifeste, indique ton dépôt : `"repository": "ton-compte/ton-depot"`.
2. Mets la `version` du manifeste au numéro de la publication (`1.0.1`).
3. Crée une **version GitHub** dont l'**étiquette** (le « tag ») est `v` suivi de ce numéro : `v1.0.1`.
4. Joins-y les **deux fichiers**, nommés comme ci-dessus.
5. Écris les notes de la version : l'utilisateur les voit quand une mise à jour lui est proposée.

Avec l'outil `gh` de GitHub, cela donne :

```powershell
gh release create v1.0.1 pack-out/ashewos.demo-1.0.1.avmod pack-out/ashewos.demo-1.0.1.avmod.sha256 --title "1.0.1" --notes "Ce qui change dans cette version."
```

Une version marquée **préversion** (ou dont le numéro porte un `-`, comme `1.1.0-beta.1`) n'est proposée qu'aux utilisateurs qui ont déjà installé une préversion de ton module.

Tu peux aussi publier ton fichier ailleurs que sur GitHub : l'utilisateur l'installe alors à la main (double-clic, ou le bouton « Installer un module… » de l'onglet Paramètres), mais il n'aura pas de mise à jour automatique.

## Ce que voit l'utilisateur à l'installation

Il double-clique sur le `.avmod`. La fenêtre « Installer un module complémentaire » affiche :

- l'**avertissement** : « Ce module a été créé par la communauté, pas par Ashboard Vision. Il peut contenir du code malveillant : installe seulement un module dont tu connais et fais confiance à l'auteur. » ;
- ton nom, ton nom d'auteur, ta version, ta description ;
- tes **permissions**, qu'il coche une par une.

Le module est copié dans `<dossier des modules>\<id>\<version>\` (par défaut `Documents\Ashboard Vision\Modules`). Partout où il apparaît dans la fenêtre de réglage, il porte un point d'exclamation jaune. **Sur l'écran, rien ne le distingue** d'un module d'origine.

## Les mises à jour

Si le manifeste nomme un dépôt GitHub (`propriétaire/dépôt`), le logiciel y cherche les nouvelles versions tout seul : une minute après le démarrage du service, puis toutes les 24 heures.

Une version est proposée si :

- son étiquette est `v<version>` ;
- elle est **strictement plus récente** que celle qui est installée ;
- elle porte les **deux fichiers** (`.avmod` et `.avmod.sha256`), au bon nom. Si tu les ajoutes après coup, la vérification suivante les verra ;
- ce n'est pas une préversion, sauf si l'utilisateur a déjà une préversion.

La mise à jour est **proposée, jamais installée en silence**. Tant que l'utilisateur ne clique pas sur **Mettre à jour**, le logiciel n'a rien téléchargé : une vérification n'interroge que GitHub, pour connaître la version et ses notes. Au clic, le logiciel :

1. télécharge ton `.avmod` ;
2. vérifie son **empreinte SHA-256** avec ton fichier `.sha256` ;
3. lit les permissions que le fichier déclare, et présente les **nouvelles** à valider ;
4. installe **ce fichier-là**, pas un autre.

Côté permissions : celles qui étaient déjà accordées et que tu déclares encore restent cochées ; une permission que tu viens d'ajouter n'est **pas** cochée, l'utilisateur doit la valider ; une permission que tu ne déclares plus disparaît. Évite donc d'ajouter une permission dans une mise à jour sans bonne raison.

### Sans le champ `repository`

Sans champ `repository`, un module **ne se met pas à jour** avec un `.avmod` : le logiciel refuse un fichier qui porte l'identifiant d'un module déjà installé, avec le message « Un autre module porte déjà cet identifiant ». Deux modules sans dépôt ne peuvent pas se remplacer, parce qu'un autre module pourrait sinon prendre la place du tien.

Pour mettre à jour un module qui n'a pas de dépôt :

- pendant que tu l'écris, utilise le **mode développeur** (un dossier de travail et « Recharger ») ;
- chez quelqu'un qui l'a installé, **désinstalle-le d'abord**, puis installe la nouvelle version.

Le dépôt qui compte pour les mises à jour est celui de l'**installation**, enregistré par le logiciel. Changer `repository` dans une nouvelle version ne le change pas.

## Un identifiant, un module

Chaque module a un identifiant unique. Deux modules du même identifiant ne peuvent pas cohabiter : le second est une **mise à jour** du premier s'il nomme le même dépôt GitHub. Sinon, il est refusé : l'utilisateur doit d'abord désinstaller l'autre.

Choisis donc un identifiant qui te ressemble (`ton-compte.nom-du-module`) et ne le change plus : la disposition de l'utilisateur et ses secrets sont rangés sous cet identifiant.

## Et la confiance ?

L'empreinte publiée à côté de ton `.avmod` protège d'un fichier abîmé ou d'un téléchargement détourné. Elle ne protège **pas** d'un dépôt de créateur piraté : si quelqu'un prend le contrôle de ton dépôt, il peut publier un paquet et son empreinte. C'est pour cela que l'avertissement est permanent et que chaque permission nouvelle est redemandée. Protège ton compte GitHub (double authentification). Voir [la sécurité](securite.md).
