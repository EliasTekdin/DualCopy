# DualCopy agent notes

## Build

This is an Apelin external-build workspace. Build the iOS package with `apelin_project_build` targeting `roothide` (or `rootless`); Apelin runs `apelin/build.sh` with the prepared iPhoneOS SDK and toolchain. The script compiles `Sources/DualCopyTweak.m`, signs the dylib with `ldid`, and writes exactly one package through `APELIN_PACKAGE_DIR`.

The tweak is injected into SpringBoard by the generated `DualCopy.plist`. It listens on TCP port `49732` and can also initiate a connection to the configured Windows endpoint, persists at most 50 text and image entries, and exchanges UTF-8 JSON Lines messages with the Windows companion. The Windows companion is the .NET 8 WinForms project under `Windows/`; build it on Windows 11 with `dotnet build Windows\\DualCopy.Windows.csproj -c Release`. `Windows/Publish.ps1` creates a self-contained `win-x64` single-file executable. `.github/workflows/windows.yml` publishes `win-x64` and `win-arm64` artifacts on GitHub Actions. The iPhone environment has no .NET SDK and cannot produce a Windows PE executable locally.

Do not hard-code the device's RootHide jbroot path. The build selects the bootstrap macro from `APELIN_BOOTSTRAP`; the runtime substrate lookup derives the current RootHide path from the loaded image.

## Testing

After choosing a permanent package identifier, install the Apelin build, confirm it loaded into SpringBoard, respring if needed, then connect the Windows app to the phone's LAN/VPN IP on port `49732`. Exercise both directions with short text and verify the unified log for connection and clipboard events.
