# DualCopy agent notes

## Build

This is an Apelin external-build workspace. Build the iOS package with `apelin_project_build` targeting `roothide` (or `rootless`); Apelin runs `apelin/build.sh` with the prepared iPhoneOS SDK and toolchain. The script compiles `Sources/DualCopyTweak.m`, signs the dylib with `ldid`, and writes exactly one package through `APELIN_PACKAGE_DIR`.

The tweak is injected into SpringBoard by the generated `DualCopy.plist`. It listens on TCP port `49732`, persists at most the configured history entries, and exchanges UTF-8 JSON Lines messages with the Windows companion. Entries may be text, images, or data-backed files; raw-image entries are converted when UIKit can decode them. The Windows companion is the .NET 8 WinForms project under `Windows/`; build it on Windows 11 with `dotnet build Windows\\DualCopy.Windows.csproj -c Release`. `Windows/Publish.ps1` creates a self-contained `win-x64` single-file executable. `.github/workflows/windows.yml` publishes `win-x64` and `win-arm64` artifacts on GitHub Actions. The iPhone environment has no .NET SDK and cannot produce a Windows PE executable locally.

Do not hard-code the device's RootHide jbroot path. The build selects the bootstrap macro from `APELIN_BOOTSTRAP`; the runtime substrate lookup derives the current RootHide path from the loaded image.

## Testing

After choosing a permanent package identifier, install the Apelin build, confirm it loaded into SpringBoard, respring if needed, then connect the Windows app to the phone's LAN/VPN IP on port `49732`. Exercise text in both directions, ordinary files in both directions, and supported images in both directions. For DNG, verify whether the source platform decodes it; if not, the original file should still arrive. Verify the unified log for connection, conversion, and clipboard events.

Windows code cannot be compiled on this iPhone because the .NET SDK is not installed here. Validate it on Windows 11 with `dotnet build`, or use the GitHub Actions Windows workflow.
