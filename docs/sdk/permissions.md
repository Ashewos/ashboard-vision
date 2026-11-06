# Les permissions

Un module enfermé ne peut presque rien faire tout seul : pas de réseau, pas de fichiers, pas de capteurs. Pour sortir de sa boîte, il demande des **permissions**, comme une appli de téléphone.

## Comment ça marche

1. Tu **déclares** les permissions dont ton module a besoin, dans le manifeste (`"permissions": ["internet"]`).
2. À l'installation, la fenêtre de réglage les montre, avec l'avertissement. L'utilisateur **coche chacune, une par une**. Ce qu'il ne coche pas est refusé.
3. Ce que tu n'as **pas déclaré** est refusé d'office, sans question.
4. L'utilisateur peut changer ses choix plus tard, dans les réglages du module (bouton « Permissions… »).
5. Si une mise à jour de ton module déclare une permission **nouvelle**, elle est demandée de nouveau. Celles qui étaient déjà accordées restent cochées, les nouvelles ne le sont pas.

Une permission refusée n'est jamais une panne : le module reçoit un « refusé » et c'est à lui de le dire proprement dans sa case.

## Les cinq permissions

| Nom dans le manifeste | Dans le code | Ce qu'elle ouvre | Qui l'impose |
|---|---|---|---|
| `reseau-local` | `ModulePermission.LocalNetwork` | Les appareils et serveurs de ton réseau (Home Assistant, Frigate, Proxmox…). | Windows (capacité `privateNetworkClientServer`) |
| `internet` | `ModulePermission.Internet` | Les services sur Internet (Downdetector…). | Windows (capacité `internetClient`) |
| `ce-pc` | `ModulePermission.ThisComputer` | Un service lancé sur ce PC, sur les **ports choisis par l'utilisateur** dans les réglages du module (serveur Minecraft sur le 25565, Home Assistant sur le 8123…). | Le logiciel, qui relaie la connexion |
| `capteurs` | `ModulePermission.Sensors` | Les températures, les charges et la mémoire, lues par le logiciel. | Le logiciel |
| `musique` | `ModulePermission.NowPlaying` | Le titre, l'artiste et la pochette de la musique en cours. | Le logiciel |

Sont toujours accordés, **sans permission** : la taille de ta case, le thème, l'heure, tes propres réglages, ton coffre à secrets, ton dossier de données privé et le journal.

Il n'y a **pas de permission « lancer un programme »** : elle permettrait de sortir de l'enfermement. Ton module ne peut lancer aucun autre processus.

## Savoir ce qui est accordé

`Services.Has(permission)` dit si l'utilisateur a accordé la permission. Utilise-la au démarrage : « une permission que l'utilisateur a retirée n'est pas une panne ». Dis-le dans la case, au lieu de laisser une erreur réseau obscure.

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
        // Une permission que l'utilisateur a retirée n'est pas une panne : on le dit, au lieu de laisser une erreur réseau obscure.
        if (!Services.Has(ModulePermission.Internet))
        {
            SetStatus(ModuleHealth.Offline, "Ce module a besoin de la permission « Internet » : accorde-la dans la fenêtre de réglage d'Ashboard Vision.");
            return Task.CompletedTask;
        }
```

Si l'utilisateur retire une permission pendant que ton module tourne, le logiciel **relance** le processus du module : les capacités de l'enfermement sont fixées au lancement. `StartAsync` est donc rappelé, avec la nouvelle liste.

## Réseau local et Internet

Prends le client HTTP du SDK. Il marche pour les deux permissions, et pour « Cet ordinateur » (voir plus bas).

*Le module « Dépôt GitHub » : `examples/GitHubRepo/GitHubRepo/GitHubRepoModule.cs`.*

```csharp
    internal virtual HttpClient CreateHttpClient() => Services.CreateHttpClient();
```

(Le mot `virtual` sert à tes tests : voir [Tester](tests.md#tester-sans-réseau).)

Sans `reseau-local` ni `internet`, ton module n'a **aucun réseau**. Avec l'une des deux, il atteint les adresses que Windows range de ce côté. Un module qui peut viser les deux sortes d'adresses (comme le serveur Minecraft, qui peut être chez toi ou sur Internet) déclare les deux : l'utilisateur ne coche que ce qui correspond à son serveur.

Le client HTTP du SDK n'utilise pas le proxy du système.

### Un appareil à certificat « maison »

Un appareil de ton réseau a souvent un certificat HTTPS qu'aucune autorité ne signe. `Services.CreateHttpClient("ma-cle")` l'accepte la **première fois**, range son empreinte dans ton coffre (sous `cert:ma-cle`), et refuse ensuite tout autre certificat pour cette clé. Sans clé, seuls les certificats valides sont acceptés.

## Cet ordinateur

Windows interdit à un module enfermé de joindre un serveur de **ce PC** (`localhost`). Le logiciel, lui, n'est pas enfermé : il **relaie** la connexion. Ton module dit « ouvre-moi le port 25565 de ce PC », le logiciel se connecte à `127.0.0.1` sur ce port et fait passer les octets dans les deux sens.

Le relais n'ouvre que les ports que **l'utilisateur** a saisis dans les champs `port` des réglages **de la case qui le demande**, et seulement si `ce-pc` est accordée. Un module peut avoir 16 connexions ouvertes en même temps au plus.

Conséquences pour toi :

- **N'écris jamais un port en dur.** Tu déclares un champ `port` avec une valeur proposée (25565, par exemple), et l'utilisateur la change s'il a un autre port ou plusieurs serveurs. Voir [les réglages](reglages.md#les-champs-port-et-nombre).
- Un module qui déclare `ce-pc` doit avoir **au moins un champ `port`** dans son schéma.
- Un champ que l'utilisateur n'a pas touché vaut sa valeur proposée : le relais accepte alors ce port-là.
- Un port qui n'est la valeur d'**aucun** champ `port` est refusé.

### La connexion brute : `ConnectLocalAsync`

*Le module « Serveur Minecraft » : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
            // « Refusé » veut dire ici que rien n'écoute sur ce port : la permission, elle, a déjà été vérifiée au démarrage. « Indisponible » veut
            // dire que le logiciel lui-même n'a pas répondu : ce n'est pas le serveur qui est en cause, on le dit à part.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ConnectTimeout);
            var result = await ConnectLocalAsync(_port, limit.Token).ConfigureAwait(false);
            return result switch
            {
                { Status: DataStatus.Ok, Value: { } stream } => stream,
                { Status: DataStatus.Unavailable } => throw new ServerProblem(RelayUnavailable),
                _ => throw new ServerProblem(Unreachable),
            };
```

`Services.ConnectLocalAsync(port, ct)` rend un `DataResult<Stream>` :

| `Status` | Ce que ça veut dire |
|---|---|
| `Ok` | Le flux est ouvert. Il se lit et s'écrit comme une connexion TCP ; le fermer ferme la connexion. |
| `Denied` | La permission « Cet ordinateur » manque, ou le port n'est pas la valeur d'un champ `port`, ou rien n'écoute sur ce port. |
| `Unavailable` | Le logiciel n'a pas répondu. Ce n'est pas le serveur qui est en cause. |

### Le client HTTP passe par le relais tout seul

Pour une page web de ce PC, ne te complique pas la vie : `Services.CreateHttpClient()` envoie `localhost`, `127.0.0.1` et `::1` dans le relais. Le port de l'adresse doit alors être celui d'un champ `port` :

```text
http://localhost:8123/api/states
```

### Reconnaître « ce PC » : `IsThisComputer`

Tout ce qui désigne ce PC passe par `ConnectLocalAsync` : `localhost`, `127.0.0.1`, `127.0.0.2`, `::1`, `0.0.0.0`… Une **connexion directe** vers la boucle locale est **bloquée** par l'enfermement, même si elle est correcte.

Le module « Serveur Minecraft » reconnaît toutes ces écritures avant de choisir entre le relais et une connexion ordinaire.

*Le module « Serveur Minecraft » : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
    internal static bool IsThisComputer(string host)
    {
        var name = host.TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(name, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }
```

Il ne reconnaît pas ce qui demande un DNS : le nom de la machine, ou l'adresse de ton réseau local sur ce PC, passent par le réseau ordinaire. Si cela échoue, le message du module te le dit (« écris localhost »).

Puis il vérifie la bonne permission selon le cas :

*Même fichier : `examples/Minecraft/Minecraft/MinecraftModule.cs`.*

```csharp
        _local = IsThisComputer(_host);
        if (_local && !Services.Has(ModulePermission.ThisComputer))
        {
            SetStatus(ModuleHealth.Offline, "Ce module a besoin de la permission « Cet ordinateur » pour joindre un serveur de ce PC : accorde-la dans la fenêtre de réglage d'Ashboard Vision.");
            return Task.CompletedTask;
        }
```

## Capteurs

Avec `capteurs`, `await Services.GetSensorsAsync(ct)` rend un `DataResult<SensorReading>`. Sans la permission, le résultat est `Denied` et le logiciel n'est même pas interrogé.

`SensorReading` contient le nom du processeur (`CpuName`), celui de la carte graphique (`GpuName`) et un dictionnaire `Values`. **Une valeur absente n'est pas dans le dictionnaire** : teste toujours `TryGetValue`.

| Clé | Valeur | Unité |
|---|---|---|
| `cpu.load`, `gpu.load` | charge | % |
| `cpu.temp`, `gpu.temp`, `disk.temp` | température | °C |
| `cpu.clock`, `gpu.clock` | fréquence | MHz |
| `cpu.power`, `gpu.power` | puissance | W |
| `gpu.vram.used`, `gpu.vram.total` | mémoire vidéo | Mo |
| `gpu.fan` | ventilateur | tr/min |
| `ram.used`, `ram.total` | mémoire | Go |
| `disk.used` | occupation | % |

La température du processeur n'arrive que si le pilote des températures est installé : prévois qu'elle manque.

## Musique en cours

Avec `musique`, `await Services.GetNowPlayingAsync(ct)` rend un `DataResult<NowPlayingInfo>` : `Title`, `Artist`, `AppName`, `Position`, `Duration`, `IsPlaying` et `CoverPng` (la pochette en PNG, ou `null`). Sans musique, le résultat est `Unavailable`.

Le logiciel ne lit les capteurs et la musique que lorsqu'un module les demande, et libère ces sources après 30 secondes sans demande.

## Les trois états d'un `DataResult`

`GetSensorsAsync`, `GetNowPlayingAsync` et `ConnectLocalAsync` rendent le même genre de réponse :

| `DataStatus` | Sens |
|---|---|
| `Ok` | La donnée est là (`Value`). |
| `Denied` | Pas de permission : dis-le dans la case. |
| `Unavailable` | Pas disponible pour le moment (capteur absent, aucune musique, logiciel injoignable) : réessaie plus tard. |

## Le piège des 60 requêtes par heure

Deux cases « Dépôt GitHub » se partagent la limite de **60 requêtes par heure** que GitHub accorde sans jeton. Chaque case en fait 12 par heure au minimum. Si tu poses plusieurs cases, ajoute un **jeton** (un secret, dans les réglages du module) : la limite est alors bien plus haute. Le module le dit dans le message de la case quand GitHub le refuse.
