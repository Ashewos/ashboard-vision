# Écrans Thermalright : ce qu'on sait pour en prendre d'autres en charge

*Notes du 29/10/2026. Pour l'instant, seul l'écran d'Alex est pris en charge. Ces notes serviront aux prochaines mises à jour : d'autres modèles, et plusieurs écrans à la fois.*

Source : le projet [Lexonight1/thermalright-trcc-linux](https://github.com/Lexonight1/thermalright-trcc-linux) (GPL-3.0), qui a décortiqué le logiciel officiel de Thermalright (TRCC, et ses programmes `USBLCD.exe` et `USBLCDNEW.exe`). Les fichiers lus :
- `doc/REFERENCE_DEVICES.md` ;
- `doc/PROTOCOL_USBLCDNEW.md` ;
- `src/trcc/core/registry.py` ;
- `tests/test_ly_lcd_geometry.py`.

Ce qui suit ne reprend que des faits sur le matériel, pas de code.

## L'écran d'Alex

Ce que Windows en sait :

| Information | Valeur |
|---|---|
| Identifiant USB | `VID_0416` / `PID_5408` (Winbond), révision `0118` |
| Nom annoncé | « USBDISPLAY » : un nom générique, qui ne donne pas le modèle |
| Numéro de série | `2025BCD68E49` |
| Pilote | WinUSB, celui de Windows. L'écran réclame lui-même `USB\MS_COMP_WINUSB`, donc rien à installer |

La réponse de 512 octets à sa poignée de main commence ainsi :
`03 ff 00 00 00 00 00 00 01 00 00 00 00 00 00 00 49 8e d6 bc 02 00 04 00 80 07 00 00 57 02 00 00 41 …`

Elle se lit comme ceci :

| Octets | Valeur | Sens |
|---|---|---|
| 0, 1, 8 | `03`, `FF`, `01` | Réponse valide |
| 16–19 | `49 8E D6 BC` | Identifiant de l'écran : la fin du numéro de série, octets à l'envers (`BCD68E49`). Le logiciel officiel l'écrit en hexadécimal |
| 20 | `02` | Code du modèle. Le logiciel officiel calcule `PM = 64 + octet 20`, et remplace d'abord les valeurs 0 à 3 par 1. Ici : **PM 65 = Trofeo Vision 9,16", 1920×462** |
| 22 | `04` | Sous-modèle : `SUB = octet 22 + 1`, soit 5. Il ne sert pas à choisir la taille |
| 24–27 | `80 07 00 00` = 1920 | Probablement la largeur. Aucun projet ne s'en sert |
| 28–31 | `57 02 00 00` = 599 | **Inconnu** : ce n'est pas la hauteur (462). Aucun projet ne s'en sert |
| 32 | `41` (« A ») | Inconnu pour LY. LY1 lit un « octet de mode » à cet endroit |

## La famille de l'écran d'Alex : LY et LY1

Le même protocole sert à plusieurs tailles, que le code du modèle distingue.

| USB | Variante | Calcul du modèle | Modèles connus |
|---|---|---|---|
| `0416:5408` | LY (celui d'Alex) | `PM = 64 + octet 20` (0 à 3 → 1) ; `SUB = octet 22 + 1` | PM 65 → 1920×462 ; PM 68 → 1280×480 ; PM 69 → 1920×440 |
| `0416:5409` | LY1 | `PM = 49 + octet 20` ; `SUB = octet 22` | PM 65 et 66 → 1920×462 ; PM 69 → 1920×440 |

Ce qui change en LY1 :
- point d'envoi `EP02 OUT` au lieu de `EP09` ;
- poignée de main de 512 octets au lieu de 2048, avec une réponse de 511 octets ;
- octet 8 de chaque paquet à `02` ;
- nombre de paquets non arrondi au multiple de 4 ;
- image envoyée d'une seule écriture, puis 511 octets de validation.

Le logiciel officiel fait tourner ces écrans dans les quatre orientations : 0, 90, 180 et 270°.

**Limite de taille des images.** Le logiciel officiel limite chaque JPEG à **450 000 octets**, pour tous ses écrans. L'écran d'Alex a accepté jusqu'à 700 Ko pendant les essais, mais un autre exemplaire pourrait refuser. Il vaut mieux respecter cette limite.

## Les autres familles (protocoles différents)

| USB | Protocole | Poignée de main | Tailles connues | Exemples |
|---|---|---|---|---|
| `87AD:70DB` | Bulk | `12 34 56 78`, 64 octets envoyés, 1024 lus ; PM à l'octet 24, SUB à l'octet 36 | 480×480, 1600×720 | GrandVision 360, Mjolnir Vision 360, Hyper Vision 360, Peerless Vision 360, Phantom Spirit 120 Vision EVO, Levita Vision |
| `0416:5302` | HID type 2 | `DA DB DC DD`, 512 / 512 ; PM à l'octet 5, SUB à l'octet 4 | 320×240, 240×240, 1280×480 | Frozen Warframe SE et 360, Assassin Spirit 120 Vision, **Trofeo Vision LCD (1280×480)** |
| `0416:5406` | HID type 3 (F5) | `F5 00 01 00`, 1040 / 1024 ; la taille se lit à l'octet 0 | 320×320 | LC1, LC2, LC3, LC5 |
| `0418:5303`, `0418:5304` | HID type 3 | — | — | TARAN ARMS |
| `87CD:70DB` | SCSI | — | 320×320 | Frozen Horizon Pro, Frozen Magic Pro, Frozen Vision V2 |
| `0402:3922` | SCSI | — | 320×320, 320×240 | Frozen Warframe, Frozen Warframe 240 |
| `0416:8001` | HID | — | pas d'écran : afficheur à segments et LED | série Digital |

D'après le projet, un même identifiant USB couvre souvent plusieurs modèles, et seule la poignée de main les distingue.

## Idées pour les prochaines mises à jour (souhaits d'Alex)

- **D'autres écrans.** Pour commencer, les variantes LY : 1280×480 et 1920×440, avec le même protocole. Ensuite, LY1. Les autres familles demandent chacune leur protocole. Dans tous les cas, l'affichage doit s'adapter à la taille : le damier, les thèmes et les modules sont aujourd'hui dessinés pour 1920×462. Rien ne peut être vérifié sans l'écran en main : il faudra des testeurs.
- **Plusieurs écrans à la fois**, reconnus par leur numéro de série (octets 16 à 19, ou le numéro de série USB). Chacun aurait sa disposition et son orientation.
- **Dès le plan 6.** Reconnaître le modèle exact (PM 65 sur `0416:5408`) et dire clairement à la personne quand son écran n'est pas encore pris en charge, au lieu de tenter de l'utiliser.
