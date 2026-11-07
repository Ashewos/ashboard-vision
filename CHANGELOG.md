# Journal des changements

Les versions sont numérotées selon [SemVer](https://semver.org/lang/fr/) : les bêtas s'écrivent `1.0.0-beta.N`, les stables `1.0.0`, `1.1.0`, etc. Le texte de chaque section sert de notes à la version GitHub du même numéro.

## 1.1.0-beta.1

Première bêta de la 1.1.0 : les **modules complémentaires**, créés par la communauté, et le **SDK** pour en écrire.

### Modules complémentaires

- Un module complémentaire est un fichier `.avmod`. Tu l'installes d'un double-clic, ou depuis **Paramètres, section Modules complémentaires, « Installer un module… »**.
- Avant l'installation, la fenêtre te montre ce que le module demande. Tu accordes ses permissions **une par une** : réseau local, Internet, un service de ce PC sur les ports que tu choisis, capteurs, musique en cours.
- Chaque module tourne dans **son propre processus, enfermé par Windows** (AppContainer et job object). S'il plante, l'écran et les autres modules continuent.
- Le logiciel enregistre l'empreinte du module à l'installation et la revérifie à chaque lancement, même hors ligne. Un module modifié depuis ta validation ne démarre pas. Ses fichiers sont verrouillés pendant qu'il tourne.
- Un **point d'exclamation jaune** signale les modules complémentaires partout dans la fenêtre de réglage.
- Les mises à jour d'un module sont cherchées sur GitHub et **te sont proposées**. Elles ne se téléchargent que si tu cliques.
- Paramètres montre ce que consomme chaque module, et signale ceux qui consomment beaucoup.
- Les modules sont rangés dans `Documents\Ashboard Vision\Modules`. Tu peux changer ce dossier.
- Un mode développeur permet d'essayer un module en cours d'écriture.
- Sans module complémentaire, rien ne tourne en plus : le logiciel reste aussi discret qu'avant.
- À la désinstallation, si tu effaces tes réglages, le dossier des modules est effacé aussi, s'il est à son emplacement par défaut. Les secrets des modules (clés, jetons) sont effacés dans tous les cas, même si tu gardes tes réglages.

> **Avertissement.** Un module complémentaire n'est pas fait par Ashboard Vision et **peut contenir du code malveillant**. L'enfermement limite les dégâts, mais ne les exclut pas. Installe seulement un module dont tu connais et fais confiance à l'auteur.

### SDK pour créer des modules

- Le SDK permet d'écrire un module en C# : un modèle de projet (`dotnet new ashboard-module`), des aides de test et un banc d'essai (`ashboard-module check` et `ashboard-module pack`).
- L'installateur le propose à la page des options : « Outils pour créer des modules (SDK) », décoché par défaut. S'il ne trouve pas de SDK .NET 10 sur ton PC, il télécharge la dernière version chez Microsoft et l'installe, après avoir vérifié son empreinte et sa signature. La barre de l'installateur suit l'installation paquet par paquet.
- Les quatre paquets du SDK sont aussi joints à chaque version GitHub. Ils ne sont pas publiés sur nuget.org.
- Toute la documentation est en français, dans `docs/sdk`.
- Le dépôt contient trois modules d'exemple, dans `examples` : Dépôt GitHub, Démo et Serveur Minecraft.

### Corrections

- Dans l'onglet Disposition, cliquer sur un module de la liste « Ajouter un module » l'ajoute de nouveau. Depuis la 1.0.0, le clic refermait la liste sans rien ajouter.
- À la désinstallation du logiciel, quand tu effaces tes réglages, le dossier des modules est bien supprimé, même dans un dossier Documents synchronisé par OneDrive.

## 1.0.0

Première version stable d'Ashboard Vision. Elle réunit tout ce que les quatre bêtas ont apporté (voir leurs sections ci-dessous) :

- un installateur Windows qui installe tout, propose le démarrage avec Windows et se désinstalle proprement, en gardant tes réglages ou en les effaçant ;
- les mises à jour depuis GitHub en un clic, sur le canal de ton choix (Stable ou Bêta), vérifiées par leur empreinte SHA-256 ;
- le passage de trofeo-HB-dashboard à Ashboard Vision, réglages compris ;
- le mod des quotas de Claude Code, installé même quand Claude Code n'est fourni que par l'application Claude.

Si tu as mis à jour depuis la bêta 2 ou une plus ancienne sans redémarrer Windows depuis, le service tourne peut-être encore en administrateur : un redémarrage de Windows suffit à le remettre avec tes droits normaux.

## 1.0.0-beta.4

- Le mod des quotas s'installe aussi quand Claude Code n'est fourni que par l'application Claude, sans commande `claude` dans le PATH. Merci à Macorou pour ce correctif !
- La mise à jour du mod à chaque nouvelle version et son retrait à la désinstallation trouvent aussi ce Claude Code.
- Depuis la bêta 3, une mise à jour en un clic relance le service avec tes droits normaux : cette version est la première à le montrer.

## 1.0.0-beta.3

- Après une mise à jour en un clic, le service repart avec tes droits normaux, et non plus en administrateur. C'est maintenant l'installateur qui demande l'autorisation à Windows.
- Si tu refuses cette autorisation, Paramètres indique que la mise à jour est annulée.
- Si l'installateur s'arrête sans rien installer, Paramètres l'indique comme un échec.

Note : la mise à jour depuis la bêta 2 passe encore par l'ancienne méthode. Juste après, le service tourne donc en administrateur, jusqu'à sa prochaine relance (redémarrage de Windows, par exemple).

## 1.0.0-beta.2

Deuxième bêta, surtout pour essayer la mise à jour depuis la bêta 1.

- L'ancien dossier `Program Files\trofeo-HB-dashboard`, resté vide après le passage à Ashboard Vision, est maintenant retiré.

## 1.0.0-beta.1

Première bêta publique. Le projet, qui s'appelait trofeo-HB-dashboard, devient Ashboard Vision.

### Nouveau nom

- Le logiciel, sa fenêtre, ses menus, ses fichiers et le mod des quotas de Claude Code s'appellent Ashboard Vision.
- Tes réglages déménagent de `%AppData%\trofeo-HB-dashboard` vers `%AppData%\ashboard-vision` au premier lancement. Rien n'est perdu : le dossier est renommé d'un bloc, tout ou rien, et si le déménagement échoue (un fichier ouvert, par exemple), le démarrage s'arrête plutôt que de repartir de zéro.
- Un nouveau logo, une icône qui suit le thème de Windows et des images aux couleurs de la marque pour l'installateur.

### Installateur

- `Ashboard-Vision-Setup-<version>.exe` installe tout dans `Program Files\Ashboard Vision`, demande les droits d'administrateur et propose le démarrage avec Windows.
- PawnIO, nécessaire à la lecture de certains capteurs, n'est plus embarqué : l'installateur le télécharge pendant l'installation, depuis le dépôt officiel, et vérifie son empreinte SHA-256. Une connexion Internet n'est nécessaire que pour ce pilote : sans elle, ou si tu annules le téléchargement, l'installation continue sans lui, et tu pourras l'installer plus tard depuis la fenêtre de réglage.
- La désinstallation retire le logiciel, le mod des quotas et le démarrage automatique, te demande si tu gardes tes réglages, et journalise ses étapes.
- Le bouton « Lancer » de la dernière page ouvre la fenêtre de réglage, qui démarre le service s'il ne tourne pas.
- L'installateur n'est pas signé : Windows SmartScreen affichera un avertissement la première fois. Le README explique comment le passer.

### Mises à jour

- L'onglet Paramètres cherche les nouvelles versions sur GitHub, une fois au démarrage du service (après 60 secondes), puis toutes les 24 heures. Rien ne tourne entre deux.
- Tu choisis ton canal : Stable ou Bêta. Si tu choisis Stable alors que tu as une bêta, le logiciel te propose de revenir à la dernière version stable dès qu'il y en a une, après avoir sauvegardé tes réglages ; la case « Attendre la prochaine version stable » te garde sur ta bêta jusqu'à ce qu'une stable plus récente sorte.
- Une mise à jour se télécharge, se vérifie par son empreinte SHA-256, puis s'installe sans que tu aies à retourner sur GitHub.

### Écran reconnu

- Le logiciel lit le modèle de l'écran branché. Seul le Thermalright Trofeo Vision 9,16" est pris en charge pour l'instant ; un autre écran est nommé comme non pris en charge au lieu de recevoir des images qu'il ne saurait pas afficher. La recherche d'un écran non pris en charge se fait au plus toutes les 30 secondes.

### Autres changements

- Les programmes livrés portent le nom Ashboard Vision (produit, titre, éditeur).
- Le mod des quotas déjà installé est rafraîchi lors d'une mise à jour du logiciel.
