# La compatibilité

Question simple : **si j'écris mon module aujourd'hui, marchera-t-il encore dans un an ?** Oui, tant que tu respectes les règles de cette page. Un module n'est jamais refusé parce qu'il est ancien.

## Trois numéros à ne pas confondre

| Numéro | Exemple | Où il est écrit |
|---|---|---|
| La version **d'Ashboard Vision** (le logiciel) | `1.0.0` | Les versions GitHub du logiciel. |
| La version **du SDK** | `1.0` (paquet `1.0.0`) | `"sdk": "1.0"` dans ton manifeste, et la version du paquet `AshboardVision.Sdk` dans ton `.csproj`. |
| La version **de ton module** | `1.2.3` | `"version"` dans ton manifeste. |

Le SDK est numéroté **à part du logiciel**. Il ne change que quand ce qu'il offre aux modules change.

## Les règles

1. **Le SDK suit la numérotation `MAJEUR.MINEUR`.** Le deuxième chiffre augmente quand on **ajoute** quelque chose (un champ, une méthode). **Rien n'est retiré** tant que le premier chiffre ne change pas.
2. **Ton module déclare la version _minimale_ du SDK dont il a besoin**, dans `"sdk"`. Un module écrit pour la 1.2 tourne dans un logiciel qui parle la 1.5.
3. **Un module qui demande une version plus récente que celle du logiciel est refusé**, avec ce message : « Ce module demande une version plus récente d'Ashboard Vision : mets à jour le logiciel. »
4. **Quand le premier chiffre change (2.0)**, le logiciel continue de parler l'ancienne version pendant une période de transition. Un module n'est refusé qu'après, avec ce message : « Ce module a été écrit pour une ancienne version du SDK qu'Ashboard Vision ne prend plus en charge : demande à son auteur de le mettre à jour. »

Aujourd'hui, le logiciel parle le **SDK 1.0** et n'accepte que la **version majeure 1**.

### Des exemples

| Ton module demande | Le logiciel parle | Résultat |
|---|---|---|
| `1.0` | `1.0` | Il tourne. |
| `1.0` | `1.5` | Il tourne : le logiciel sait faire tout ce que la 1.0 savait. |
| `1.2` | `1.5` | Il tourne. |
| `1.3` | `1.0` | Refusé : « Ce module demande une version plus récente d'Ashboard Vision… ». L'utilisateur met le logiciel à jour. |
| `2.0` | `1.x` | Refusé de la même façon. |
| `1.0` | `2.0`, qui accepte encore la majeure 1 (transition) | Il tourne. |
| `1.0` | `2.0`, qui n'accepte plus la majeure 1 | Refusé : « …une ancienne version du SDK qu'Ashboard Vision ne prend plus en charge… ». |

Le banc d'essai (`ashboard-module check`) applique la même règle : il te dit tout de suite si ton module demande plus que le logiciel qu'il embarque.

## Ce que tu dois faire

- **Garde `"sdk"` au plus bas.** Écris la version la plus ancienne qui a tout ce dont tu te sers. Plus elle est basse, plus d'utilisateurs ont un logiciel assez récent.
- **Ne change `"sdk"` que si tu utilises quelque chose de plus récent.** Si le SDK 1.1 ajoute un champ de réglage et que tu l'utilises, mets `"sdk": "1.1"`. Sinon, laisse `1.0`.
- **Le modèle fixe le paquet à la version exacte `1.0.0`.** Quand un SDK 1.x plus récent sort, tu changes ce numéro à la main dans ton `.csproj` **et** le champ `"sdk"` du manifeste, ensemble.

*Le modèle : `templates/AshboardVision.Sdk.Templates/content/ashboard-module/MonModule/MonModule.csproj`.*

```xml
<PackageReference Include="AshboardVision.Sdk" Version="1.0.0" />
```

Tu n'as **pas** besoin de recompiler un module ancien quand le logiciel est mis à jour.

## Pourquoi c'est vrai

- **L'API est gelée.** Un instantané de toute l'API publique de la ligne 1.x est dans le dépôt (`tests/AshboardVision.Sdk.Tests/api-1.0.txt`). Un test échoue si une ligne disparaît (une méthode retirée, une signature changée), ou si un membre **abstrait** est ajouté à un type qui existait déjà, ce qui casserait toute classe qui en hérite. Ajouter est permis ; retirer ne l'est pas, avant la 2.0.
- **La version d'assemblage reste `1.0.0.0` pour toute la ligne 1.x.** Un module compilé contre la 1.0 se lie donc à la bibliothèque de l'hôte, quelle que soit la version mineure du logiciel.
- **L'hôte de module fournit lui-même le SDK et SkiaSharp.** Les copies que tu glisses dans ton `.avmod` (le dossier de sortie de ta construction en contient) sont ignorées : ton module se lie toujours à celles de l'hôte. SkiaSharp est figé à sa version majeure (3) pour toute la ligne 1.x du SDK.

## Les types réservés

Certains types publics de `Trofeo.ModuleKit` ne font **pas** partie du contrat : `IModule`, `IModuleFactory`, `ICommandableModule`, `ModuleContext`, `ModuleDescriptor`, `ModuleCommand`, `ModuleCommandKind`, `ModuleStatus`, `IConfigurableModuleFactory`, `IModuleNotices`, `SettingsDocument`, `CertificatePinning` et `ModulePermissions`. Ils sont publics parce que les modules d'origine d'Ashboard Vision s'en servent.

Un module qui les utilise n'a **aucune garantie de compatibilité** : ils peuvent changer dans une version mineure. Voir aussi [le contrat](contrat.md#ce-qui-ne-fait-pas-partie-du-contrat).

## Et la version de mon module ?

Elle est à toi. Mets-la au format SemVer (`1.2.3`, ou `1.2.3-beta.1` pour une préversion) et augmente-la à chaque publication : le logiciel ne propose une mise à jour que si la version publiée est **strictement plus récente** que celle de l'utilisateur. Voir [Publier](publier.md#les-mises-à-jour).
