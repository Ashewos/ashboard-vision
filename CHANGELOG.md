# Journal des changements

Les versions sont numérotées selon [SemVer](https://semver.org/lang/fr/) : les bêtas s'écrivent `1.0.0-beta.N`, les stables `1.0.0`, `1.1.0`, etc. Le texte de chaque section sert de notes à la version GitHub du même numéro.

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
