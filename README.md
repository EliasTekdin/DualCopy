# DualCopy

DualCopy shares text and image clipboard history between a jailbroken iPhone and a Windows 11 PC over a local TCP connection.

- **iPhone:** a SpringBoard tweak listens on TCP port `49732` by default, records a configurable number of entries, and applies text or PNG images received from Windows to the general pasteboard.
- **Windows:** a .NET 8 WinForms tray application connects to the phone by IP address, records text and PNG image entries, and updates the Windows clipboard when the phone sends a new entry.
- **Settings:** the iPhone adds a `DualCopy` pane under Settings → Tweaks with enable/disable, port, polling interval, history limit, image sync, and image-size controls.
- **Protocol:** UTF-8 JSON Lines. The first message is `hello` with a history array; subsequent messages are `clipboard` entries. Images are PNG bytes encoded as base64. The full history is exchanged once per connection.

## Security

This first version uses plain TCP and has no pairing secret or encryption. The iPhone listener binds to all local interfaces. Use it only on a trusted LAN, restrict TCP `49732` in Windows Firewall, and do not expose the port to the Internet. A future release should add authenticated encryption before use on untrusted networks.

## Windows 11 companion

Requirements:

- Windows 11
- .NET 8 SDK or Runtime
- A network path from the PC to the iPhone (same LAN or a reachable VPN address)

Build and run:

```powershell
dotnet build Windows\DualCopy.Windows.csproj -c Release
dotnet run --project Windows\DualCopy.Windows.csproj -c Release
```

To publish a self-contained x64 executable on Windows 11:

```powershell
powershell -ExecutionPolicy Bypass -File Windows\Publish.ps1
```

The public repository also contains a GitHub Actions workflow that publishes `DualCopy.exe` artifacts for `win-x64` and `win-arm64` whenever the Windows sources change. Those artifacts include the .NET runtime and do not require .NET to be installed on the target PC.

The tray menu's **Configure** dialog accepts the iPhone IP address and port. The default port is `49732`. After saving, the client reconnects automatically. A connection status is shown in the tray tooltip.

## iPhone build and install

This repository is an Apelin workspace. On the device, `apelin_project_build` runs `apelin/build.sh`; it compiles the Objective-C SpringBoard tweak and Settings bundle for the current RootHide target, signs both Mach-Os with `ldid`, and creates one package. The package is scoped to `com.apple.springboard` and depends on `mobilesubstrate` and `preferenceloader`.

After installation, open Settings → Tweaks → DualCopy, choose the port and performance options, then respring when changing the port. The iPhone starts listening after SpringBoard launches; the Windows app can then connect to the phone's Wi-Fi/VPN IP.

## Repository status

The permanent package identifier is `com.eliastekdin.dualcopy`. Version `0.2.0` adds Settings configuration and image clipboard transport.

## License

MIT
