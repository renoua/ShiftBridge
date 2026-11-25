# ShiftBridge for macOS

ShiftBridge runs on Apple Silicon with macOS 13 or later. It connects to BLE controllers, trainers, and heart-rate sensors through Core Bluetooth. Training apps connect over OpenBikeControl TCP or DirCon. The app advertises both transports through Bonjour and displays its IP addresses and ports under **Application** for clients that accept a manual address.

## Build and run

Follow the [macOS build commands](macos-build-commands.md) to select the .NET 10 SDK and install the workload that matches your Xcode version. Run these commands from the repository root with that SDK selected.

```sh
dotnet build "$(pwd -P)/src/ShiftBridge.MacOS/ShiftBridge.MacOS.csproj"
open src/ShiftBridge.MacOS/bin/Debug/net10.0-macos/osx-arm64/ShiftBridge.app
```

Allow Bluetooth when macOS prompts. On macOS versions with Local Network privacy controls, also allow local network access for discovery and communication with other devices. The Bluetooth banner and **Application → Local Network settings** link back to System Settings. Reopen ShiftBridge after changing a denied permission.

## Verify on hardware

1. Wake a Zwift controller, scan from **Devices**, and connect. Confirm that the virtual gear changes on **Home** when shifting. Rename the controller, quit and relaunch, verify that the name and automatic connection are restored, then forget it and confirm it stops reconnecting.
2. In **Trainer**, choose Bluetooth, scan for an FTMS trainer, select it, and enable virtual shifting. Repeat with a DirCon trainer using **Find trainers** and its manual IP and port. Connect a BLE heart-rate sensor from **Devices** and check that it can run alongside the controller and trainer.
3. In a training app that supports OBC TCP or DirCon, discover ShiftBridge on the LAN and confirm a gear change. Repeat with manual address entry where offered. Run a full ride with the trainer proxy and verify telemetry, resistance changes, and heart rate.
4. Deny and re-enable Bluetooth and Local Network access. Repeat the connection checks after turning Bluetooth off and on, and after putting the Mac to sleep with devices connected.

## Package

> **Note:** `scripts/package-macos.sh` is not in the repository yet. Until it is added, build with the `dotnet build` commands above and package the `.app` manually.

Run `scripts/package-macos.sh` to build the release bundle and create `publish/macos-arm64/ShiftBridge-macos-arm64.dmg` for local testing. For distribution, set `DEVELOPER_ID_APPLICATION` to the Developer ID Application signing identity and `NOTARY_KEYCHAIN_PROFILE` to an existing `notarytool` keychain profile before running the same script. It signs with Hardened Runtime, submits the disk image to Apple, and staples and validates the ticket. Test the signed download on a Mac before uploading the disk image to the [renoua/ShiftBridge releases](https://github.com/renoua/ShiftBridge/releases) under the exact name `ShiftBridge-macos-arm64.dmg`: the app looks for that file to offer updates. Users install updates from the release download in **Settings**.

The macOS release uses network output. BLE peripheral output, global keyboard and HID capture, key suppression, the gear overlay, and login startup are deferred.
