# ShiftBridge

🇫🇷 [Lire en français](README.md)

Use your **Zwift Play, Click, Click v2 and Ride** controllers in other training apps (TrainingPeaks Virtual, Les jeux du cycle…) through the open [OpenBikeControl](https://openbikecontrol.org) protocol. Free, GPL-3.0 licensed, a single `.exe` for Windows with no installer, plus an **Android** version (see below).

> Independent project, not affiliated with Zwift, Inc. The controller protocol comes from public reverse engineering and may change with their firmware updates.

## How it works

```
Zwift Play/Click/Ride ──BLE──▶ ShiftBridge (PC) ──OpenBikeControl──▶ training app ──FTMS──▶ trainer
```

ShiftBridge translates each press into an action (gear up/down, navigation…). **The training app applies virtual shifting** on your trainer, using its own gear curve.

**Zwift itself does not implement OpenBikeControl**: to play on Zwift, pair your controllers directly with Zwift, without ShiftBridge.

## Usage

1. Power-cycle the controller and disconnect it from Zwift, the trainer and your phone (only one connection at a time).
2. **Devices** page → *Scan for controllers* → *Connect*.
3. **Keys** page: choose the action of each key (the indicator lights up when you press).
4. **App** page: enable *Bluetooth*, *Network* or *DirCon*, then choose OpenBikeControl in the target app.
   - Network (OpenBikeControl over the local network) — DirCon: TrainingPeaks Virtual over the network — Bluetooth: TrainingPeaks Virtual
   - Bluetooth needs an adapter that supports the "peripheral" role. Otherwise, use Network.
5. **Settings** page → *Display* (Windows): *Show the virtual gear over the game* displays the virtual gear (gear bar, a flash on every change, and optionally power, cadence and trainer speed) on top of your app; drag it to move it, mouse wheel to resize. On the **Trainer** page, *Start automatically on launch* restores virtual shifting with the last trainer, at the last gear.
6. **Keys** page: for "gear up" and "gear down", the *Repeat* box keeps shifting as long as the key is held (speed set in the card at the top of the page: Slow, Normal, Fast by default, Very fast; `RepeatSpeed` in `config.json`).
7. **Keyboard, remotes, gamepads** (Windows): **Devices** page → *Keyboards and gamepads*, tick what you want to use. **Keys** page → *Add a key*, press the key or button you want, then choose its action.
   - Keyboards and HID remotes (presenters, media keys, Bluetooth handlebar buttons that appear as a keyboard): assigned keys are **swallowed** (they reach neither Zwift nor other apps; can be turned off). Prefer keys that are unused elsewhere: F13 to F24, media keys.
   - Xbox controllers (XInput, vibration included) and generic HID joysticks (buttons, d-pad, axes): read even when another window has focus. A gamepad cannot be hidden from the training app: if it reads it too, disable it in its settings to avoid double triggering.
   - Not available on Android yet.
8. **Heart rate** (**Devices** page, Windows and Android): *Scan* for a Bluetooth heart-rate strap or watch (standard heart-rate service), then *Connect*. The proxy republishes it to the training app together with the "ShiftBridge Trainer" trainer: power, cadence, speed and heart rate all arrive through a single device. Disconnect the strap from your phone or watch first (many accept only one or two connections). Connecting or removing a sensor restarts the proxy (the app reconnects). The last sensor is reconnected at launch; heart rate also shows in the overlay (*Power, cadence and speed…* option).
9. **Controller vibration**: the controller whose button you press vibrates on every gear change (twice at the first or last gear), and all of them when the training app asks for it (an echo of a vibration already played is ignored). **Devices** page → *Advanced* → *Controller vibration* turns this off. The *Vibrate* test in a controller's menu still works.
10. **Bluetooth remotes and groupsets** (beta, Windows and Android): Thinkrider VS200, CYCPLUS BC2 and T7H, Magene Onelap Key, Shimano Di2 (D-Fly), SRAM AXS / eTap, Wahoo KICKR Bike, Elite Square, Stages SB20, Wheeltop EDS and L-TWOO eRX / eR9 are recognised when you press *Search* (**Devices** page), then *Connect*. Their keys appear on the **Keys** page with default assignments (up = gear up, down = gear down); they do not vibrate. The protocols come from public projects (BikeControl, qdomyos-zwift…) and have **not been tried on real hardware** here. If a device is not recognised or misbehaves: **Settings** page → **Diagnostic** → *Bluetooth inspector* → *Search*, *Capture* the device, press each button, then *Copy* the log (nothing is written to the device).
   - **Shimano Di2**: first assign the paddles to D-Fly channels in Shimano's E-Tube app (EW-WU111 or wireless rear derailleur). The channels appear on the Keys page (short, long, double press). The Di2 also really shifts its gears.
   - **SRAM AXS / eTap**: simple mode, no pairing or encryption. One lever press = "single tap", two close presses = "double tap"; left and right are not told apart and the derailleur also shifts physically. Only the rear derailleur is used.
   - **SRAM AXS, advanced mode** (experimental, Devices page → *Advanced*): ShiftBridge pairs with the derailleur (SRAMBond key exchange, offline), reads its encrypted rear-cog position and turns each change into "gear up" or "gear down" by direction. The lever used is unknown (left and right are indistinguishable) and a shift at the end of the range is invisible. **Pairing changes the derailleur's key: the SRAM app will have to reconnect.** Steps: tick the mode, connect the derailleur, hold its AXS button until it blinks, then *Pair*. The key is saved in config.json (a secret: do not share that file). The cryptography is validated on the public test vectors; the rest has never been tried with a real derailleur.
   - **L-TWOO**: the groupset does not report its buttons, so ShiftBridge polls it and turns a gear change into presses. Enter its three-character PIN (Devices page → *Advanced*) before connecting; a wrong code is reported in the log.
   - **Wahoo KICKR Bike** and **Stages SB20** are still offered as trainers too; their buttons connect here in parallel.
10. **Settings** → *Open diagnostic log* records the raw frames (useful for a bug report). Unexpected errors are written to `%APPDATA%\ShiftBridge\crash.log`.

Only one instance of ShiftBridge can run at a time. Controller scanning stops by itself once all known controllers are connected (use *Scan* to add one).

## Updates (Windows)

At launch and then every 24 h, ShiftBridge checks the latest GitHub release. If a newer version exists, a banner appears on Home: **Update** downloads the new `exe`, verifies its SHA-256 (published by GitHub), replaces the exe in place and restarts; **Release notes** opens the release page. If the exe's folder is not writable, the button opens the release page for a manual replacement. The check can be turned off in **Settings → Updates** (one anonymous request to `api.github.com`, see [PRIVACY.md](PRIVACY.md)). On Android, the Play Store handles updates.

To publish: create the GitHub release with the file **`ShiftBridge.exe`** (exact name) and a `vX.Y.Z` tag. Pre-releases and drafts are ignored. To test without publishing: `SHIFTBRIDGE_UPDATE_FEED=http://localhost:8000/latest.json` replaces the API address.

## Android

Same logic as on Windows (same controllers, same keys, same virtual shifting), in an app for Android 8.0 (API 26) or newer phones and tablets. The core is shared with the Windows version: only Bluetooth access and the interface differ.

| Where does your training app run? | Connection to use |
|---|---|
| **On the same Android device** | **App** tab → *Wi-Fi / local network*, then in the target app choose OpenBikeControl at address `127.0.0.1` (address and port are shown in the tab). Bluetooth "peripheral" is not needed here. |
| **On another device** (PC, Apple TV…) | *Bluetooth* if your device can advertise a service (the option tells you otherwise), or *Wi-Fi / local network* / DirCon. |

Good to know:
- On first launch, ShiftBridge asks for the **Bluetooth** (nearby devices) and **notifications** permissions. Without Bluetooth, no controller can be found.
- A **foreground service** keeps the bridge alive with the screen off or while the training app is in front. The virtual gear and controller state show in its notification, which also offers *Quit*.
- Depending on the manufacturer (Samsung, Xiaomi…), you may need to exclude ShiftBridge from battery optimization so the system does not kill the service: the *Keep ShiftBridge running* button on Home opens the relevant settings.
- If the Bluetooth permission is denied, a banner on Home says so and opens the app settings.
- The gear overlay on top of the app does not exist on Android: the notification replaces it.
- Installing outside the Play Store: download the APK and allow installation from that source.

## Click v2 / Ride v2 (firmware ≥ 1.3)

These controllers lock about a minute after connecting if Zwift has not "unlocked" them in the last 24 h (the Zwift service is then no longer exposed). ShiftBridge detects the lock and reconnects automatically; for a lasting unlock, open the Zwift app, connect the controller for 10 to 30 s, close Zwift completely, then come back to ShiftBridge. A built-in wizard is planned for a later version.

## Project status (v0.22.0)

> **Tested with limited hardware.** ShiftBridge has been tried by its author on a small number of controllers, devices and apps. Other models, trainers, firmwares or apps may behave differently: treat it as a **beta**. Feedback is welcome (**Settings** → diagnostic log, then a [GitHub issue](https://github.com/renoua/ShiftBridge/issues)). In the table, "to validate on real hardware" means "not yet confirmed on a wide range of hardware".

| Feature | Status |
|---|---|
| Play / Click / Ride frame parsing, mapping, virtual gear, repeat on hold, lifecycle (disconnection, shutdown, stuck clients, configuration) | Tested (509 automated tests, hand-built frames) |
| UI language: French if the system is in French (fr-FR, fr-BE, fr-CH, fr-CA…), English otherwise; Automatic / Français / English selector in the advanced options (applied on restart). Texts are in `src/ShiftBridge.Core/Localization/Strings.resx` (English) and `Strings.fr.resx` (French). The Diagnostic log stays in French | Tested (resource consistency, language choice, label rendering); **on-screen rendering not verified** (Windows and Android) |
| Keyboard, HID remotes, Xbox controllers and joysticks (Windows): open key identity, multi-device merging, XInput/HID mapping, chain up to OBC | Tested with a fake provider |
| Nintendo Switch Pro Controller over Bluetooth (full 0x30 report, decoding verified on real reports) | Tested on real hardware: buttons, d-pad, sticks, L/R, ZL/ZR |
| Keyboard (global hook, key swallowing), Xbox controllers (XInput), other HID joysticks | Compiles; **not yet exercised with real hardware** |
| UI overhaul (0.15: five pages Home / Devices / Keys / Trainer / App + Settings, heart rate in Devices, collapsible "Advanced" sections per page, one-click back) | Windows: **verified on screen** (capture of each page, minimum width included); Android: compiles (Debug), **rendering to be checked on a phone** |
| Other UI elements (richer overlay, Android permission banner, notification icon) | Compiles; **not yet exercised on screen**: to check visually |
| Network OBC (mDNS + TCP) and DirCon | Tested with simulated clients; **not yet with real apps** |
| BLE connection to controllers, vibration, Bluetooth OBC | Written, **to validate on real hardware** |
| BLE remotes and groupsets (VS200, BC2, T7H, Onelap Key, Di2, simple AXS, KICKR Bike, Elite Square, SB20, Wheeltop, L-TWOO) and Bluetooth inspector (0.19) | Decoders and lifecycle tested with published frames and a fake BLE; **never tried with the real hardware** |
| Heart rate: 0x2A37 decoding, scan, source with reconnection, 0x180D service republished by the proxy (local read, write refused), proxy restart on sensor change | Tested with fake sensors and a fake trainer; **not yet with a real strap, nor with TPV / Les jeux du cycle** (whether an app accepts heart rate from a DirCon trainer is to be validated) |
| Click v2: `0x0A`/`0x0B` codes, Click v1 field meanings, paddle threshold | Assumptions to confirm with Diagnostic mode |
| Android: the APK builds (Debug and Release) and the manifest is verified; the Bluetooth abstraction is covered by tests with a fake platform | Tested on the logic side |
| Android: scan, GATT connection to controllers and trainer, peripheral Bluetooth OBC, foreground service, mDNS on Android | Written, **never run on a device**: to validate on real hardware (Bluetooth does not work in the emulator) |
| Notification area, Click v2 wizard, Play encryption | Not implemented |

## Building

```
dotnet test ShiftBridge.sln
dotnet publish src/ShiftBridge.App/ShiftBridge.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Requirements: .NET 10 SDK, Windows 10 (build 19041) or newer. Tests also run on Linux and macOS.

### Android

Additional requirements: the `maui-android` workload, a JDK 17 and the Android SDK (platform 36).

```
dotnet workload install maui-android
dotnet publish src/ShiftBridge.Android/ShiftBridge.Android.csproj -f net10.0-android -c Release -o apk
```

If the Android SDK is not detected, add `-p:AndroidSdkDirectory=<SDK path>` (and `-p:JavaSdkDirectory=<JDK 17 path>`). The APK `apk/com.antoinerenou.shiftbridge-Signed.apk` is signed with a debug key; for distribution, provide your own key (`-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=… -p:AndroidSigningKeyAlias=…`). The Android projects are in `ShiftBridge.Android.sln`, separate from `ShiftBridge.sln`, so the Windows version builds without the workload.

## Structure

- `src/ShiftBridge.Core`: protocols and pure logic (testable without Bluetooth).
- `src/ShiftBridge.Platform`: the `BridgeService` orchestrator, TCP/mDNS/DirCon servers, and the platform-independent Bluetooth logic (`Ble/Abstractions`: scan, GATT binding, advertising).
- `src/ShiftBridge.Platform.Windows`: WinRT Bluetooth.
- `src/ShiftBridge.Platform.Android`: Android Bluetooth (`BluetoothGatt`, `BluetoothGattServer`).
- `src/ShiftBridge.Presentation`: `MainViewModel`, shared by both interfaces.
- `src/ShiftBridge.App`: WPF interface (Windows).
- `src/ShiftBridge.Android`: .NET MAUI interface and foreground service (Android).

Sources and credits: see [NOTICE.md](NOTICE.md).

## Privacy

ShiftBridge collects no data and sends nothing to its developer: see the [privacy policy](PRIVACY.md) ([web version](https://renoua.github.io/ShiftBridge/privacy.html)).

## License

Copyright (C) 2025-2026 Antoine Renou. ShiftBridge is free software distributed under the [GNU GPL v3](LICENSE): without any warranty.
