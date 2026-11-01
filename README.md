# DualCopy

DualCopy shares text clipboard history between a jailbroken iPhone and a Windows 11 PC over a local TCP connection.

- **iPhone:** a SpringBoard tweak listens on TCP port `49732`, records up to 50 text entries, and applies entries received from Windows to the general pasteboard.
- **Windows:** a .NET 8 WinForms tray application connects to the phone by IP address, records up to 50 text entries, and updates the Windows clipboard when the phone sends a new entry.
- **Protocol:** UTF-8 JSON Lines. The first message is `hello` with a history array; subsequent messages are `clipboard` entries. The full history is exchanged once per connection.

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

The tray menu's **Configure** dialog accepts the iPhone IP address and port. The default port is `49732`. After saving, the client reconnects automatically. A green tray connection status means the TCP session is active.

## iPhone build and install

This repository is an Apelin workspace. On the device, `apelin_project_build` runs `apelin/build.sh`; it compiles the Objective-C SpringBoard tweak for the current RootHide target, signs it with `ldid`, and creates one package. The package is intentionally scoped to `com.apple.springboard` and depends on the `mobilesubstrate` provider.

After installation, respring and confirm the package's `ApelinLoaded` marker. The tweak logs connection and clipboard events under the package subsystem. The iPhone starts listening after SpringBoard launches; the Windows app can then connect to the phone's Wi-Fi/VPN IP.

## Repository status

The package metadata currently uses the development identifier `local.apelin.dualcopy` and version `0.1.0`. Choose a permanent reverse-DNS package identifier before the first device installation.

## License

MIT
