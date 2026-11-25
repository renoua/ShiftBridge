# macOS build commands

From the repository root, install the .NET `10.0.404` SDK for Apple Silicon under `.dotnet/`. Run this once per checkout. [Microsoft's .NET install script](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)

```sh
repo_root="$(pwd -P)"
mkdir -p "$repo_root/.dotnet" &&
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$repo_root/.dotnet/dotnet-install.sh" &&
  bash "$repo_root/.dotnet/dotnet-install.sh" \
    --version 10.0.404 --architecture arm64 --install-dir "$repo_root/.dotnet" --no-path
```

From the repository root, run these exports in each new shell to use the local SDK and keep the CLI state and NuGet packages in Git-ignored directories.

```sh
repo_root="$(pwd -P)"
export DOTNET_CLI_HOME="$repo_root/.dotnet"
export NUGET_PACKAGES="$repo_root/.nuget/packages"
export PATH="$repo_root/.dotnet:$PATH"
```

From the repository root, check which SDK, workload, and Xcode your shell selects.

```sh
dotnet --version
dotnet workload list
xcodebuild -version
```

For Xcode 27.2, use .NET SDK `10.0.404` and the `macos` workload `27.2.10000`. If `dotnet --version` reports .NET 11, put a .NET 10-only installation first on `PATH` or invoke its `dotnet` by absolute path. With SDK `10.0.404` selected, install the matching workload set once. [Microsoft's .NET 10 release for Xcode 27.2](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode27.2.10000)

```sh
dotnet workload install macos --version 10.0.404
```

Build the Apple Silicon Debug and Release app bundles.

```sh
project="$(pwd -P)/src/ShiftBridge.MacOS/ShiftBridge.MacOS.csproj"
dotnet build "$project" -c Debug
dotnet build "$project" -c Release
```

`pwd -P` resolves symlinks in the checkout path. Use the physical path consistently for builds and packaging. Switching between a symlink and the physical path can cause MSBuild to delete a current reference assembly during incremental cleanup and fail with `CS0006`. [MSBuild issue #9670](https://github.com/dotnet/msbuild/issues/9670)

The bundles are written to `src/ShiftBridge.MacOS/bin/{Debug,Release}/net10.0-macos/osx-arm64/ShiftBridge.app`. The executable inside each bundle is `Contents/MacOS/ShiftBridge`.

## Package the Release DMG

> **Note:** `scripts/package-macos.sh` is not in the repository yet. Until it is added, build with the `dotnet build` commands above and package the `.app` manually.

From the repository root, with the same .NET 10 installation first on `PATH`, run these commands to create and verify an unsigned test image.

```sh
env -u DEVELOPER_ID_APPLICATION -u NOTARY_KEYCHAIN_PROFILE ./scripts/package-macos.sh
hdiutil verify publish/macos-arm64/ShiftBridge-macos-arm64.dmg
```

The script runs `dotnet publish -c Release`, verifies the app bundle's signature, and packages the complete `ShiftBridge.app` and an Applications shortcut into `publish/macos-arm64/ShiftBridge-macos-arm64.dmg`. Each successful run replaces the previous image at that path. The `dotnet build` commands above update the app bundles. Rerun the packaging script to refresh the DMG after a build.

For a signed release, export `DEVELOPER_ID_APPLICATION` with your Developer ID Application signing identity and `NOTARY_KEYCHAIN_PROFILE` with your existing `notarytool` keychain profile, then run `./scripts/package-macos.sh`. The script signs the app and the disk image, submits the image to Apple for notarization, and staples and validates the ticket.
