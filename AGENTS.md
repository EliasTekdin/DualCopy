# DualCopy agent notes

## Build

This is an Apelin external-build workspace. Build the iOS package with `apelin_project_build` targeting `roothide` (or `rootless`); Apelin runs `apelin/build.sh` with the prepared iPhoneOS SDK and toolchain. The script compiles `Sources/DualCopyTweak.m`, signs the dylib with `ldid`, and writes exactly one package through `APELIN_PACKAGE_DIR`.

The tweak is injected into SpringBoard by the generated `DualCopy.plist`. It listens on TCP port `49732` and can also initiate a connection to the configured Windows endpoint, persists at most 50 text, image, and file entries, and exchanges UTF-8 JSON Lines messages with the Windows companion. The iPhone reads encoded image bytes directly and performs optional DNG/RAW decoding on a utility queue so large RAW items do not block SpringBoard. The Windows companion has categorised, described settings pages and light/midnight themes; its DNG conversion remains best effort using codecs available on Windows.

Do not hard-code the device's RootHide jbroot path. The build selects the bootstrap macro from `APELIN_BOOTSTRAP`; the runtime substrate lookup derives the current RootHide path from the loaded image.

## Testing

After choosing a permanent package identifier, install the Apelin build, confirm it loaded into SpringBoard, respring if needed, then connect the Windows app to the phone's LAN/VPN IP on port `49732`. Exercise text in both directions, ordinary files in both directions, and supported images in both directions. For DNG, verify whether the source platform decodes it; if not, the original file should still arrive. Verify the unified log for connection, conversion, and clipboard events.

Windows code cannot be compiled on this iPhone because the .NET SDK is not installed here. Validate it on Windows 11 with `dotnet build`, or use the GitHub Actions Windows workflow.
