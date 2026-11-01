# DualCopy

DualCopy shares text, images, raw camera data, and ordinary files between a jailbroken iPhone and a Windows 11 PC over a local TCP connection.

- **iPhone:** a SpringBoard tweak listens on TCP port `49732` by default, can initiate a connection to the configured Windows endpoint, keeps a configurable history, and applies text, images, and data-backed files received from Windows to the general pasteboard.
- **Windows:** a .NET 8 WinForms tray application listens for the iPhone and also maintains an outgoing connection to the configured iPhone address. It can transfer text, images, DNG/PDF/TXT/ZIP/DEB and other file types, saves received files in a configurable folder, and includes a history viewer.
- **Settings:** the iPhone adds a `DualCopy` pane under Settings → Tweaks with connection, performance, history, file, and image conversion controls. The Windows tray application uses categorised Connection, Content, Performance, and Appearance pages. Every Windows setting includes a plain-language explanation of its default, trade-offs, and limits; the light and midnight themes use a high-contrast blue/navy visual system rather than an unstyled black/white form.
- **Protocol:** UTF-8 JSON Lines. The first message is `hello` with a history array; subsequent messages are `clipboard` entries. Entries use `kind` values `text`, `image`, or `file`; binary data is base64 encoded. Older clients can still exchange text and image entries.

## Image and file compatibility

The default image format is `auto`: DualCopy uses a compatible representation and can fall back from PNG to JPEG when the configured limit requires it. Raw camera data such as DNG is converted to PNG/JPEG when the receiving platform can decode it. If decoding is not available, the original file is transferred instead of being discarded. The Windows companion uses the codecs available on Windows, so a DNG codec installed on that PC may be required for DNG-to-image conversion.

The iPhone uses a conservative image limit by default and now reads already-encoded PNG/JPEG/HEIC pasteboard data directly. Optional DNG/RAW decoding is moved to a utility queue instead of running on SpringBoard’s main thread, so a large RAW clipboard item cannot block Home Screen scrolling. If the source exposes only unsupported RAW data, DualCopy sends the original file without conversion. The default image limit is 4 MiB and the default file limit is 50 MiB; Windows can use `0` for unlimited, but the iPhone still enforces its own configured maximum.


**History limit** controls how many recent entries DualCopy stores and exchanges. The Windows companion provides the visible history viewer; the iPhone stores the history for synchronisation and exposes a Clear stored clipboard history action in Settings.

## Security

This version uses plain TCP and has no pairing secret or encryption. The iPhone listener binds to all local interfaces. Use it only on a trusted LAN or VPN, restrict TCP `49732` in Windows Firewall, and do not expose the port to the Internet.

## Windows 11 companion

Requirements:

- Windows 11
- A network path from the PC to the iPhone (same LAN or a reachable VPN address)

The recommended download is the self-contained `DualCopy.exe` artifact from the GitHub Actions workflow or a GitHub Release. It includes the .NET runtime and does not require the .NET SDK on the target PC. It is a portable tray program: double-click it, then open its tray icon menu and choose **Configure**. There is no installer and no required installation directory. Settings and history are stored under `%LOCALAPPDATA%\DualCopy`; received files default to `%USERPROFILE%\Downloads\DualCopy`.

For development, build and run with:

```powershell
dotnet build Windows\DualCopy.Windows.csproj -c Release
dotnet run --project Windows\DualCopy.Windows.csproj -c Release
```

To publish a self-contained x64 executable on Windows 11:

```powershell
powershell -ExecutionPolicy Bypass -File Windows\Publish.ps1
```

The tray menu provides **Configure**, **Clipboard history**, **About DualCopy**, and **Exit**. Configure the iPhone address and port, test the connection, choose transfer limits and image format, select the receive folder, and enable dark mode. The application listens on its configured port for an iPhone-initiated connection and retries its outgoing connection automatically.

## iPhone build and install

This repository is an Apelin workspace. On the device, `apelin_project_build` runs `apelin/build.sh`; it compiles the Objective-C SpringBoard tweak and Settings bundle for the current RootHide target, signs both Mach-Os with `ldid`, and creates one package. The package is scoped to `com.apple.springboard` and depends on `mobilesubstrate` and `preferenceloader`.

After installation, open Settings → Tweaks → DualCopy, set the Windows computer IP address and port, keep the iPhone listen port matching the Windows port, choose the image/file limits, and respring when changing the listen port. The iPhone starts listening after SpringBoard launches.

## Repository status

The permanent package identifier is `com.eliastekdin.dualcopy`. Version `0.3.0` adds file transport, raw-image conversion attempts, configurable transfer formats and limits, a Windows history viewer, dark mode, and About information.

## License

MIT
