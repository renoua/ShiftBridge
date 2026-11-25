# Politique de confidentialité / Privacy Policy

**ShiftBridge** (`com.antoinerenou.shiftbridge`) — Windows et Android / Windows and Android
Date d'effet / Effective date : 2026-11-21

Éditeur / Publisher : Antoine Renou — renouantoine@gmail.com
Code source / Source code : https://github.com/renoua/ShiftBridge (GPL-3.0)

---

## Français

### En bref
ShiftBridge **ne collecte aucune donnée personnelle et n'envoie rien au développeur ni à un tiers**. Il n'y a ni compte, ni publicité, ni statistiques d'usage, ni rapport d'erreurs automatique. Tout reste sur votre appareil.

### Ce que l'application traite
Pour fonctionner, ShiftBridge lit et relaie en temps réel, sur votre appareil :
- les appuis de touches de vos manettes Zwift Play / Click / Ride, de votre clavier ou de votre manette de jeu (Windows) ;
- les mesures de votre home-trainer (puissance, cadence, vitesse) ;
- la fréquence cardiaque d'une ceinture ou d'une montre Bluetooth, si vous en connectez une ;
- les noms et adresses Bluetooth des appareils que vous choisissez de connecter.

Ces informations ne sont transmises **qu'à l'application d'entraînement que vous avez choisie** (TrainingPeaks Virtual, Les jeux du cycle), par Bluetooth ou sur votre réseau local (OpenBikeControl, DirCon). Elles ne sortent jamais vers Internet. Ce que fait ensuite cette application est régi par sa propre politique.

### Mises à jour (Windows et macOS)
Les versions Windows et macOS recherchent une nouvelle version de ShiftBridge au lancement, puis une fois par jour, via l'API publique de GitHub (`api.github.com`). Windows télécharge le fichier depuis `github.com` si vous choisissez de mettre à jour. macOS affiche un avis et vous laisse télécharger le fichier dans votre navigateur. GitHub reçoit votre adresse IP à chaque requête. La comparaison des versions et des dates se fait sur votre appareil. Vous pouvez désactiver la recherche automatique dans **Réglages → Mises à jour**. La version Android utilise les mises à jour du Play Store.

### Ce qui est enregistré sur votre appareil
- `config.json` : vos réglages (affectation des touches, derniers appareils connectés, options d'affichage).
- Le journal de diagnostic (trames brutes des manettes) et, sous Windows, `crash.log` (erreurs inattendues). Ils restent locaux ; ils ne sont partagés que si **vous** les joignez vous-même à un rapport de bug.

Sous Android, la sauvegarde cloud est désactivée (`allowBackup=false`). La désinstallation (ou l'effacement des données de l'application) supprime ces fichiers ; sous Windows, supprimez le dossier `%APPDATA%\ShiftBridge`.

### Autorisations Android
- **Appareils à proximité (Bluetooth scan / connexion / annonce)** : trouver et connecter vos manettes, trainer et capteur cardio, et se présenter comme appareil Bluetooth à votre application d'entraînement. L'autorisation de scan est déclarée « jamais utilisée pour la localisation ».
- **Localisation (Android 11 et antérieur uniquement)** : exigée par ces versions d'Android pour toute recherche Bluetooth. ShiftBridge n'accède pas à votre position.
- **Internet, état du Wi-Fi, multidiffusion Wi-Fi** : serveurs locaux de l'application (OpenBikeControl, DirCon, découverte mDNS) sur votre réseau.
- **Service de premier plan (appareil connecté), notifications, maintien de l'éveil** : garder le pont actif écran éteint et afficher son état dans une notification.

### Partage, enfants, sécurité
Aucune donnée n'est vendue, partagée ou transmise au développeur. L'application ne s'adresse pas spécifiquement aux enfants et ne collecte aucune donnée sur eux. Les communications locales avec votre application d'entraînement n'étant pas chiffrées par ces protocoles, utilisez un réseau de confiance.

### Modifications et contact
Cette politique peut évoluer ; la version en vigueur est celle du dépôt, avec sa date d'effet. Questions : renouantoine@gmail.com ou https://github.com/renoua/ShiftBridge/issues.

---

## English

### In short
ShiftBridge **collects no personal data and sends nothing to the developer or to any third party**. There are no accounts, ads, usage analytics or automatic crash reporting. Everything stays on your device.

### What the app processes
To work, ShiftBridge reads and relays in real time, on your device:
- button presses from your Zwift Play / Click / Ride controllers, keyboard or gamepad (Windows);
- your trainer's readings (power, cadence, speed);
- heart rate from a Bluetooth strap or watch, if you connect one;
- the names and Bluetooth addresses of the devices you choose to connect.

This information is passed **only to the training app you chose** (TrainingPeaks Virtual, Les jeux du cycle), over Bluetooth or your local network (OpenBikeControl, DirCon). It never leaves for the Internet. What that app does with it is governed by its own policy.

### Updates (Windows and macOS)
The Windows and macOS versions check for a new ShiftBridge release at startup and once a day using GitHub's public API (`api.github.com`). Windows downloads the file from `github.com` if you choose to update. macOS shows a notification and lets you download the file in your browser. GitHub receives your IP address with each request. Version and date comparisons happen on your device. You can turn off automatic checks in **Settings → Updates**. The Android version uses Play Store updates.

### What is stored on your device
- `config.json`: your settings (key mapping, last connected devices, display options).
- The diagnostic log (raw controller frames) and, on Windows, `crash.log` (unexpected errors). They stay local and are shared only if **you** attach them to a bug report yourself.

On Android, cloud backup is disabled (`allowBackup=false`). Uninstalling (or clearing the app's data) removes these files; on Windows, delete the `%APPDATA%\ShiftBridge` folder.

### Android permissions
- **Nearby devices (Bluetooth scan / connect / advertise)**: find and connect your controllers, trainer and heart-rate sensor, and present itself as a Bluetooth device to your training app. The scan permission is declared as "never used to derive location".
- **Location (Android 11 and earlier only)**: required by those Android versions for any Bluetooth scan. ShiftBridge does not access your location.
- **Internet, Wi-Fi state, Wi-Fi multicast**: the app's local servers (OpenBikeControl, DirCon, mDNS discovery) on your network.
- **Foreground service (connected device), notifications, wake lock**: keep the bridge running with the screen off and show its status in a notification.

### Sharing, children, security
No data is sold, shared or sent to the developer. The app is not directed at children and collects no data about them. The local links to your training app are not encrypted by these protocols, so use a trusted network.

### Changes and contact
This policy may change; the version in the repository, with its effective date, is the current one. Questions: renouantoine@gmail.com or https://github.com/renoua/ShiftBridge/issues.
