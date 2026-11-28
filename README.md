# DualCopy

DualCopy shares text, images, raw camera data, and ordinary files between a jailbroken iPhone and a Windows 11 PC over a local TCP connection.

- **iPhone:** a SpringBoard tweak listens on TCP port `49732` by default, can initiate a connection to the configured Windows endpoint, keeps a configurable history, and applies text, images, and data-backed files received from Windows to the general pasteboard.
- **Windows:** a .NET 8 WinForms companion starts with a visible tabbed window for Connection, History, Settings, Logs and About, plus a small automatic connection-check window. It keeps a minimal tray menu for reopening or exiting, and can continue in the tray when the main window is closed. It listens for the iPhone and maintains an outgoing connection to the configured iPhone address. It can transfer text, images, DNG/PDF/TXT/ZIP/DEB and other file types, saves received files in a configurable folder, and includes a history viewer and local troubleshooting log.
- **Settings:** the iPhone adds a `DualCopy` pane under Settings → Tweaks with connection, performance, history, file, and image conversion controls. The Windows Settings tab provides matching transfer limits, polling, history, receive-folder, format, quality, and dark-mode options.
- **Protocol:** UTF-8 JSON Lines. The first message is `hello` with a history array; subsequent messages are `clipboard` entries. Entries use `kind` values `text`, `image`, or `file`; binary data is base64 encoded. iPhone-originated diagnostic messages report transfer stages and image filtering failures to the Windows companion without sending clipboard contents. Older clients can still exchange text and image entries.

## Image and file compatibility

The default image format is `auto`: DualCopy uses a compatible representation and can fall back from PNG to JPEG when the configured limit requires it. Raw camera data such as DNG is converted to PNG/JPEG when the receiving platform can decode it. If decoding is not available, the original file is transferred instead of being discarded. The Windows companion uses the codecs available on Windows, so a DNG codec installed on that PC may be required for DNG-to-image conversion.

The iPhone has conservative transfer limits to avoid blocking SpringBoard: the default image limit is 4 MiB and the default file limit is 50 MiB. These are safety limits, not a claim that every device should have an unlimited limit. The Windows companion permits `0` for unlimited, but the iPhone still enforces its own configured maximum.

**History limit** controls how many recent entries DualCopy stores and exchanges. The Windows companion provides the visible history viewer; the iPhone stores the history for synchronisation and exposes a Clear stored clipboard history action in Settings.

## Security

This version uses plain TCP and has no pairing secret or encryption. The iPhone listener binds to all local interfaces. Use it only on a trusted LAN or VPN, restrict TCP `49732` in Windows Firewall, and do not expose the port to the Internet.

## Windows 11 companion

Requirements:

- Windows 11
- A network path from the PC to the iPhone (same LAN or a reachable VPN address)

The recommended download is the self-contained `DualCopy.exe` artifact from the GitHub Actions workflow or a GitHub Release. It includes the .NET runtime and does not require the .NET SDK on the target PC. It is a portable program: double-click opens its tabbed window and runs the connection check; close/minimise it to leave it in the notification area. Settings and history are stored under `%LOCALAPPDATA%\\DualCopy`; logs are under `%LOCALAPPDATA%\\DualCopy\\Logs`; received files default to `%USERPROFILE%\\Downloads\\DualCopy`.

For development, build and run with:

```powershell
dotnet build Windows\\DualCopy.Windows.csproj -c Release
dotnet run --project Windows\\DualCopy.Windows.csproj -c Release
```

To publish a self-contained x64 executable on Windows 11:

```powershell
powershell -ExecutionPolicy Bypass -File Windows\\Publish.ps1
```

**Windows diagnostics:** the companion writes a rotating log to `%LOCALAPPDATA%\\DualCopy\\Logs\\DualCopy.log` and retains one previous log as `DualCopy.log.1`. The Logs tab follows the current log, and **Open log folder** is available in that tab. Logs distinguish retry attempts, orderly peer closes, transport/protocol failures, time spent waiting for a peer writer versus actual socket writes, and iPhone-detected clipboard outcomes; image and file entries include type/size and filter decisions. iPhone peer logs also record endpoints, protocol message types, bytes, errno, and close reason in the iOS unified log. iPhone diagnostics travel only over an active peer connection; they are not written to a log file on the phone and cannot reach Windows during a disconnection. Clipboard payload contents are not logged.


## iPhone build and install

This repository is an Apelin workspace. On the device, `apelin_project_build` runs `apelin/build.sh`; it compiles the Objective-C SpringBoard tweak and Settings bundle for the current RootHide target, signs both Mach-Os with `ldid`, and creates one package. The package is scoped to `com.apple.springboard` and depends on `mobilesubstrate` and `preferenceloader`.

After installation, open Settings → Tweaks → DualCopy, set the Windows computer IP address and port, keep the iPhone listen port matching the Windows port, choose the image/file limits, and respring when changing the listen port. The iPhone starts listening after SpringBoard launches.

## Repository status

The iPhone retries its saved Windows endpoint only while its Wi‑Fi interface has an active address; it pauses outbound attempts on cellular-only connections. Both peers increase failed-connection retry intervals up to 60 seconds, then retry automatically when the saved peer becomes reachable. If the phone stays connected to the home Wi‑Fi while cellular data is enabled, local clipboard traffic uses Wi‑Fi; cellular data alone cannot reach a private home-LAN address without a VPN or other routed connection.

The iPhone publishes a compact live state (listener ready, connected, disconnected, retrying, waiting for Wi‑Fi, invalid address, or disabled) and a short human-readable last-event detail to its preference domain. Settings → Tweaks → DualCopy refreshes that status while the pane is open; the phone still does not create a persistent log file. The Windows Logs tab shows remote iPhone diagnostics only while a peer connection is available.

The Windows companion keeps the existing TCP/JSON Lines protocol and default port `49732` for compatibility. Changing the port alone would not fix reachability and would require changing both devices and firewall rules.

The Windows Connection tab and startup check report the current peer state and give setup guidance instead of using a one-off TCP probe. The Settings tab explains RAW/DNG conversion and keeps its controls together.

The permanent package identifier is `com.eliastekdin.dualcopy`. Version 0.3.12 adds the tabbed Windows shell and iPhone live connection status; version 0.3.11 improves peer send isolation and adds connection diagnostics. Version 0.3.10 adds Wi‑Fi-aware reconnect backoff and Windows Configure dialog fixes.

## License

MIT
