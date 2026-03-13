# WireGuard Client and WiFi Switcher

**Version 0.7 beta** — by [Harold Masselink](https://github.com/masselink/WGClientWifiSwitcher)

A native Windows companion app for WireGuard that automatically connects and disconnects VPN tunnels based on your current WiFi network (SSID). Built with C# / WPF on .NET 8 — no Electron, no bloat.

---

## Features

- **Automatic tunnel switching** — reacts instantly to WiFi changes using the Windows WLAN API (`WlanRegisterNotification`). No polling.
- **Rule manager** — map any WiFi SSID to any WireGuard tunnel. Leave the tunnel field blank to disconnect on that network.
- **Default action** — configurable behaviour for unknown networks: do nothing, disconnect all, or activate a named fallback tunnel.
- **Tunnel panel** — manually connect or disconnect any tunnel with one click, without opening the WireGuard GUI.
- **System tray** — minimises to tray on close. Right-click to connect/disconnect any tunnel directly. Icon turns green when a tunnel is active.
- **Auto-discovery** — finds `.conf` and `.conf.dpapi` (DPAPI-encrypted) config files across all known WireGuard install locations. Falls back to reading installed Windows services if files are not accessible.
- **Activity log** — colour-coded, timestamped log of every WiFi change, tunnel switch, and error.
- **Dark theme UI** — frameless WPF window, Consolas font, draggable, resizable.
- **Single `.exe`** — no installer. Config stored in `%APPDATA%\WGClientWifiSwitcher\config.json`.
- **UAC elevation** — requests administrator rights automatically on launch via app manifest.

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or Windows 11 (x64) |
| Runtime | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — free, ~50 MB, pre-installed on many machines |
| WireGuard | Official WireGuard for Windows, tunnels already imported |
| Rights | Administrator (requested automatically via UAC on launch) |

---

## Build

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 x64

### Steps

Double-click `BUILD.bat`, or run in a terminal:

```
dotnet build WGClientWifiSwitcher.csproj -c Release -o dist
```

Output: `dist\WGClientWifiSwitcher.exe`

---

## Configuration

Config is saved automatically to:

```
%APPDATA%\WGClientWifiSwitcher\config.json
```

### Config folder

The app searches for WireGuard `.conf` / `.conf.dpapi` files in this order:

1. The folder set in the **Config Folder** field in the UI
2. `%APPDATA%\WireGuard`
3. `%LOCALAPPDATA%\WireGuard`
4. `%PROGRAMDATA%\WireGuard`
5. `C:\Program Files\WireGuard\Data\Configurations`
6. Installed `WireGuardTunnel$*` Windows services (fallback when config files are not readable)

The first location that yields results is used, and the Config Folder field updates automatically.

### Rules

Each rule maps a **WiFi SSID** to a **WireGuard tunnel**. When the app detects a network change it scans rules top-to-bottom and applies the first match. Leave the tunnel field blank to disconnect all tunnels on that network.

### Default action

Applies when no rule matches the current SSID:

| Setting | Behaviour |
|---|---|
| Do nothing | Leave tunnels as-is |
| Disconnect all | Stop all active tunnels |
| Activate tunnel | Start a named fallback tunnel |

---

## Auto-start at Login (optional)

Run once in an elevated PowerShell to register a scheduled task that launches the app at login with admin rights — no UAC prompt every time:

```powershell
$exe     = "C:\path\to\dist\WGClientWifiSwitcher.exe"
$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$prin    = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest
Register-ScheduledTask -TaskName "WGClientWifiSwitcher" `
    -Action $action -Trigger $trigger -Principal $prin
```

---

## Project Structure

```
WGClientWifiSwitcher/
├── BUILD.bat                  ← Double-click to build
├── WGClientWifiSwitcher.csproj
├── app.manifest               ← requireAdministrator UAC elevation
├── App.xaml                   ← Global dark theme styles
├── App.xaml.cs                ← App startup, tray icon, tray menu
├── MainWindow.xaml            ← Main UI layout
├── MainWindow.xaml.cs         ← All app logic
└── Views/
    ├── RuleDialog.xaml        ← Add / Edit rule dialog
    └── RuleDialog.xaml.cs
```
