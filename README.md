# ShiftBridge

🇬🇧 [Read in English](README.en.md)

Utilisez vos manettes **Zwift Play, Click, Click v2 et Ride** dans d'autres applications d'entraînement (TrainingPeaks Virtual, Les jeux du cycle…) grâce au protocole ouvert [OpenBikeControl](https://openbikecontrol.org). Gratuit, licence GPL-3.0, un seul `.exe` Windows sans installation, et une version **Android** (voir plus bas).

> Projet indépendant, non affilié à Zwift, Inc. Le protocole des manettes est issu de rétro-ingénierie publique et peut changer avec leurs mises à jour de firmware.

## Principe

```
Zwift Play/Click/Ride ──BLE──▶ ShiftBridge (PC) ──OpenBikeControl──▶ appli d'entraînement ──FTMS──▶ trainer
```

ShiftBridge traduit chaque appui en action (vitesse +/−, navigation…). **C'est l'appli d'entraînement qui applique le virtual shifting** sur votre trainer, selon sa propre courbe de rapports.

**Zwift lui-même n'implémente pas OpenBikeControl** : pour jouer sur Zwift, appairez vos manettes directement à Zwift, sans ShiftBridge.

## Utilisation

1. Éteignez/rallumez la manette et déconnectez-la de Zwift, du trainer et du téléphone (une seule connexion à la fois).
2. Page **Appareils** → *Rechercher les manettes* → *Connecter*.
3. Page **Touches** : choisissez l'action de chaque touche (le voyant s'allume quand vous appuyez).
4. Page **Application** : activez *Bluetooth*, *Réseau* ou *DirCon*, puis choisissez OpenBikeControl dans l'appli cible.
   - Réseau (OpenBikeControl en réseau local) — DirCon : TrainingPeaks Virtual en réseau — Bluetooth : TrainingPeaks Virtual
   - Le Bluetooth nécessite un adaptateur supportant le rôle « périphérique ». Sinon, utilisez Réseau.
5. Page **Réglages** → *Affichage* (Windows) : *Afficher la vitesse virtuelle par-dessus le jeu* affiche la vitesse virtuelle (barre des vitesses, clignotement à chaque changement, puissance, cadence et vitesse du trainer en option) au-dessus de votre appli ; glissez-la pour la déplacer, molette pour la redimensionner. Dans la page **Trainer**, *Activer automatiquement au lancement* rétablit le virtual shifting avec le dernier trainer, à la dernière vitesse.
6. Page **Touches** : pour « vitesse + » et « vitesse − », la case *Répéter* enchaîne les changements tant que la touche est tenue (vitesse réglable dans la carte du haut de la page : Lente, Normale, Rapide par défaut, Très rapide ; `RepeatSpeed` dans `config.json`).
7. **Clavier, télécommande, manette de jeu** (Windows) : page **Appareils** → *Clavier et manettes de jeu*, cochez ce que vous voulez utiliser. Page **Touches** → *Ajouter une touche*, appuyez sur la touche ou le bouton voulu, puis choisissez son action.
   - Clavier et télécommandes HID (présentation, touches média, boutons Bluetooth de guidon qui se présentent comme un clavier) : les touches assignées sont **avalées** (elles n'atteignent ni Zwift ni les autres applications ; case décochable). Préférez des touches inutilisées ailleurs : F13 à F24, touches média.
   - Manettes Xbox (XInput, vibration comprise) et joysticks HID génériques (boutons, croix, axes) : lues même quand une autre fenêtre a le focus. Une manette ne peut pas être masquée à l'application d'entraînement : si celle-ci la lit aussi, désactivez-la dans ses réglages pour éviter un double déclenchement.
   - Non disponible sur Android pour l'instant.
8. **Cardio** (page **Appareils**, Windows et Android) : *Rechercher* une ceinture ou une montre cardio Bluetooth (service standard de fréquence cardiaque), puis *Connecter*. Le proxy la republie à l'appli d'entraînement avec le trainer « ShiftBridge Trainer » : puissance, cadence, vitesse et FC arrivent par un seul appareil. Déconnectez d'abord la ceinture de votre téléphone ou de votre montre (beaucoup n'acceptent qu'une ou deux connexions). Connecter ou retirer un capteur redémarre le proxy (l'appli se reconnecte). Le dernier capteur est reconnecté au lancement ; la FC s'affiche aussi dans la superposition (option *Puissance, cadence et vitesse…*).
9. **Vibration des manettes** : la manette dont vous appuyez le bouton vibre à chaque changement de vitesse (deux fois en butée), et toutes quand l'appli le demande (l'écho d'une vibration déjà jouée est ignoré). Page **Appareils** → *Avancé* → *Vibration des manettes* la désactive ; le test *Faire vibrer* du menu d'une manette reste disponible.
10. **Télécommandes et groupes Bluetooth** (bêta, Windows et Android) : Thinkrider VS200, CYCPLUS BC2 et T7H, Magene Onelap Key, Shimano Di2 (D-Fly), SRAM AXS / eTap, Wahoo KICKR Bike, Elite Square, Stages SB20, Wheeltop EDS et L-TWOO eRX / eR9 sont reconnus lors de *Rechercher* (page **Appareils**), puis *Connecter*. Leurs touches apparaissent dans la page **Touches**, avec des assignations par défaut (haut = vitesse +, bas = vitesse −) ; elles ne vibrent pas. Les protocoles viennent de projets publics (BikeControl, qdomyos-zwift…) et n'ont **pas été éprouvés sur du vrai matériel** ici. Si un appareil n'est pas reconnu ou se comporte mal : page **Réglages** → **Diagnostic** → *Inspecteur Bluetooth* → *Rechercher*, *Capturer* l'appareil, appuyez sur chaque bouton, puis *Copier* le journal (rien n'est écrit dans l'appareil).
   - **Shimano Di2** : affectez d'abord les palettes à des canaux D-Fly dans l'appli E-Tube de Shimano (EW-WU111 ou dérailleur arrière sans fil). Les canaux apparaissent dans la page Touches (appui court, long, double). La Di2 passe aussi ses vitesses réellement.
   - **SRAM AXS / eTap** : mode simple, sans appairage ni chiffrement. Un appui sur un levier = « simple appui », deux appuis rapprochés = « double appui » ; la gauche et la droite ne sont pas distinguées et le dérailleur passe aussi sa vitesse. Seul le dérailleur arrière est utilisé.
   - **SRAM AXS, mode avancé** (expérimental, page Appareils → *Avancé*) : ShiftBridge s'appaire au dérailleur (échange de clés SRAMBond, hors ligne), lit son pignon arrière chiffré et traduit chaque changement en « vitesse + » ou « vitesse − » d'après le sens. Le levier utilisé n'est pas connu (gauche et droite se confondent) et un passage en butée est invisible. **L'appairage change la clé du dérailleur : l'appli SRAM devra se reconnecter.** Procédure : cochez le mode, connectez le dérailleur, maintenez le bouton AXS du dérailleur jusqu'au clignotement, puis *Appairer*. La clé est enregistrée dans config.json (secret : ne partagez pas ce fichier). Le chiffrement est validé sur les vecteurs de test publics ; le reste n'a jamais été essayé avec un vrai dérailleur.
   - **L-TWOO** : le groupe ne signale pas ses boutons, ShiftBridge l'interroge et traduit un changement de vitesse en appuis. Saisissez son code PIN à trois caractères (page Appareils → *Avancé*) avant de connecter ; un code faux est signalé dans le journal.
   - **Wahoo KICKR Bike** et **Stages SB20** restent aussi proposés comme trainers ; leurs boutons se connectent ici en parallèle.
10. **Réglages** → *Ouvrir le journal de diagnostic* journalise les trames brutes (utile pour un rapport de bug). Les erreurs inattendues sont écrites dans `%APPDATA%\ShiftBridge\crash.log`.

Une seule instance de ShiftBridge peut tourner à la fois. La recherche des manettes s'arrête d'elle-même une fois toutes les manettes connues connectées (bouton *Rechercher* pour en ajouter une).

## Mises à jour (Windows)

Au lancement puis toutes les 24 h, ShiftBridge consulte la dernière release GitHub. Si une version plus récente existe, un bandeau apparaît sur l'Accueil : **Mettre à jour** télécharge le nouvel `exe`, vérifie son SHA-256 (publié par GitHub), remplace l'exe en place et redémarre ; **Notes de version** ouvre la page de la release. Si le dossier de l'exe n'est pas inscriptible, le bouton ouvre la page de la release pour un remplacement manuel. La vérification se désactive dans **Réglages → Mises à jour** (une requête anonyme vers `api.github.com`, voir [PRIVACY.md](PRIVACY.md)). Sous Android, c'est le Play Store qui met à jour l'application.

Pour publier : créer la release GitHub avec le fichier **`ShiftBridge.exe`** (ce nom exact) et un tag `vX.Y.Z`. Les préversions et brouillons sont ignorés. Pour tester sans publier : `SHIFTBRIDGE_UPDATE_FEED=http://localhost:8000/latest.json` remplace l'adresse de l'API.

## macOS

L’application pour Mac Apple Silicon utilise le Bluetooth pour les appareils et OpenBikeControl TCP ou DirCon pour les applications d’entraînement. Voir [la compilation, l’utilisation et la distribution sur macOS](docs/macos.md).

## Android

Même logique que sur Windows (mêmes manettes, mêmes touches, même virtual shifting), dans une application pour téléphone ou tablette Android 8.0 (API 26) ou plus. Le cœur est partagé avec la version Windows : seuls l'accès Bluetooth et l'interface changent.

| Où tourne votre appli d'entraînement ? | Connexion à utiliser |
|---|---|
| **Sur le même appareil Android** | Onglet **Appli** → *Wi-Fi / réseau local*, puis dans l'appli cible choisissez OpenBikeControl à l'adresse `127.0.0.1` (adresse et port affichés dans l'onglet). Le Bluetooth « périphérique » ne sert pas dans ce cas. |
| **Sur un autre appareil** (PC, Apple TV…) | *Bluetooth* si votre appareil sait annoncer un service (sinon l'option l'indique), ou *Wi-Fi / réseau local* / DirCon. |

À savoir :
- Au premier lancement, ShiftBridge demande les autorisations **Bluetooth** (appareils à proximité) et les **notifications**. Sans Bluetooth, aucune manette ne peut être trouvée.
- Un **service de premier plan** garde le pont actif écran éteint ou quand l'appli d'entraînement est devant. La vitesse virtuelle et l'état des manettes s'affichent dans sa notification, qui propose aussi *Quitter*.
- Selon le fabricant (Samsung, Xiaomi…), il peut falloir exclure ShiftBridge de l'optimisation de la batterie pour que le système ne tue pas le service : le bouton *Garder ShiftBridge actif* de l'Accueil ouvre les réglages correspondants.
- Si l'autorisation Bluetooth est refusée, un bandeau de l'Accueil l'indique et ouvre les réglages de l'application.
- La superposition de la vitesse par-dessus l'appli n'existe pas sur Android : c'est la notification qui la remplace.
- Installation hors Play Store : téléchargez l'APK et autorisez l'installation depuis cette source.

## Click v2 / Ride v2 (firmware ≥ 1.3)

Ces manettes se verrouillent environ une minute après la connexion si Zwift ne les a pas « déverrouillées » dans les dernières 24 h (le service Zwift n'est alors plus exposé). ShiftBridge détecte le verrouillage et reconnecte automatiquement ; pour un déverrouillage durable, ouvrez l'application Zwift, connectez la manette 10 à 30 s, fermez Zwift complètement, puis revenez dans ShiftBridge. L'assistant intégré est prévu pour une version suivante.

## État du projet (v0.22.0)

> **Testé avec un matériel limité.** ShiftBridge a été essayé par son auteur sur un petit nombre de manettes, d'appareils et d'applications. D'autres modèles, trainers, firmwares ou applications peuvent se comporter différemment : considérez-le comme une **bêta**. Les retours sont bienvenus (page *Réglages* → journal de diagnostic, puis une [issue GitHub](https://github.com/renoua/ShiftBridge/issues)). Dans le tableau, « à valider sur matériel réel » signifie « pas encore confirmé sur un éventail large de matériel ».

| Fonction | État |
|---|---|
| Analyse des trames Play / Click / Ride, mapping, vitesse virtuelle, répétition sur appui long, cycle de vie (déconnexion, arrêt, clients bloqués, configuration) | Testé (509 tests automatisés, trames construites à la main) |
| Langue de l'interface : français si le système est en français (fr-FR, fr-BE, fr-CH, fr-CA…), anglais sinon ; sélecteur Automatique / Français / English dans les options avancées (pris en compte au redémarrage). Les textes sont dans `src/ShiftBridge.Core/Localization/Strings.resx` (anglais) et `Strings.fr.resx` (français). Le journal Diagnostic reste en français | Testé (cohérence des ressources, choix de la langue, rendu des libellés) ; **rendu à l'écran non vérifié** (Windows et Android) |
| Clavier, télécommandes HID, manettes Xbox et joysticks (Windows) : identité de touche ouverte, fusion multi-périphériques, mapping XInput/HID, chaîne jusqu'à l'OBC | Testé avec un faux fournisseur d'entrées |
| Manette Nintendo Switch Pro Controller en Bluetooth (rapport complet 0x30, décodage vérifié sur des rapports réels) | Testé sur matériel réel : boutons, croix, sticks, L/R, ZL/ZR |
| Clavier (hook global, avalage des touches), manettes Xbox (XInput), autres joysticks HID | Compile ; **pas encore exercé avec du matériel réel** |
| Refonte de l'interface (0.15 : cinq pages Accueil / Appareils / Touches / Trainer / Application + Réglages, cardio dans Appareils, sections « Avancé » repliables par page, retour d'un clic) | Windows : **vérifiée à l'écran** (capture de chaque page, largeur minimale comprise) ; Android : compile (Debug), **rendu à vérifier sur un téléphone** |
| Autres éléments d'interface (superposition enrichie, bandeau de permission Android, icône de notification) | Compile ; **pas encore exercés à l'écran** : à vérifier visuellement |
| OBC réseau (mDNS + TCP) et DirCon | Testé avec des clients simulés ; **pas encore avec de vraies applications** |
| Connexion BLE aux manettes, vibration, OBC Bluetooth | Écrit, **à valider sur matériel réel** |
| Télécommandes et groupes BLE (VS200, BC2, T7H, Onelap Key, Di2, AXS simple, KICKR Bike, Elite Square, SB20, Wheeltop, L-TWOO) et inspecteur Bluetooth (0.19) | Décodeurs et cycle de vie testés avec des trames publiées et un faux BLE ; **jamais essayés avec le vrai matériel** |
| Cardio : décodage 0x2A37, recherche, source avec reconnexion, service 0x180D republié par le proxy (lecture locale, écriture refusée), redémarrage du proxy au changement de capteur | Testé avec de faux capteurs et un faux trainer ; **pas encore avec une vraie ceinture, ni avec TPV / Les jeux du cycle** (qu'une appli accepte la FC d'un trainer DirCon est à valider) |
| Click v2 : codes `0x0A`/`0x0B`, sens des champs du Click v1, seuil des palettes | Hypothèses à confirmer avec le mode Diagnostic |
| Android : l'APK se compile (Debug et Release) et le manifeste est vérifié ; l'abstraction Bluetooth est couverte par des tests avec une fausse plateforme | Testé côté logique |
| Android : scan, connexion GATT des manettes et du trainer, OBC Bluetooth périphérique, service de premier plan, mDNS sous Android | Écrit, **jamais exécuté sur un appareil** : à valider sur matériel réel (le Bluetooth ne fonctionne pas dans l'émulateur) |
| Zone de notification, assistant Click v2, chiffrement Play | Non implémenté |

## Compiler

```
dotnet test ShiftBridge.sln
dotnet publish src/ShiftBridge.App/ShiftBridge.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Prérequis : SDK .NET 10, Windows 10 (build 19041) ou plus. Les tests tournent aussi sous Linux et macOS.

### Android

Prérequis supplémentaires : le workload `maui-android`, un JDK 17 et le SDK Android (plateforme 36).

```
dotnet workload install maui-android
dotnet publish src/ShiftBridge.Android/ShiftBridge.Android.csproj -f net10.0-android -c Release -o apk
```

Si le SDK Android n'est pas détecté, ajoutez `-p:AndroidSdkDirectory=<chemin du SDK>` (et `-p:JavaSdkDirectory=<chemin du JDK 17>`). L'APK `apk/com.antoinerenou.shiftbridge-Signed.apk` est signé avec une clé de debug ; pour une diffusion, fournissez votre propre clé (`-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=… -p:AndroidSigningKeyAlias=…`). Les projets Android sont dans `ShiftBridge.Android.sln`, à part de `ShiftBridge.sln`, pour que la version Windows se compile sans le workload.

## Structure

- `src/ShiftBridge.Core` : protocoles et logique pure (testable sans Bluetooth).
- `src/ShiftBridge.Platform` : orchestrateur `BridgeService`, serveurs TCP/mDNS/DirCon, et la logique Bluetooth indépendante de la plateforme (`Ble/Abstractions` : scan, liaison GATT, annonce).
- `src/ShiftBridge.Platform.Windows` : Bluetooth WinRT.
- `src/ShiftBridge.Platform.Android` : Bluetooth Android (`BluetoothGatt`, `BluetoothGattServer`).
- `src/ShiftBridge.Presentation` : `MainViewModel`, partagé par les deux interfaces.
- `src/ShiftBridge.App` : interface WPF (Windows).
- `src/ShiftBridge.Android` : interface .NET MAUI et service de premier plan (Android).

Sources et attributions : voir [NOTICE.md](NOTICE.md).

## Confidentialité

ShiftBridge ne collecte aucune donnée et n'envoie rien à son développeur : voir la [politique de confidentialité](PRIVACY.md) ([version web](https://renoua.github.io/ShiftBridge/privacy.html)).

## Licence

Copyright (C) 2025-2026 Antoine Renou. ShiftBridge est un logiciel libre distribué sous licence [GNU GPL v3](LICENSE) : sans aucune garantie.
