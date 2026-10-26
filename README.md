# RestoreWin10

![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

**RestoreWin10** brings the Windows 10 Start menu and taskbar back to Windows 11.

- Runs as its own program: nothing is injected into Explorer, so Windows updates don't break it.
- Built security-first: installs into Program Files, runs without administrator rights.

## Requirements

- Windows 11, 64-bit (tested on build 26200)
- [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)

## Install, update, uninstall

1. Download `RestoreWin10.exe` from the [latest release](../../releases/latest).
2. Check it: `certutil -hashfile RestoreWin10.exe SHA256` must match `SHA256SUMS.txt`.
3. Double-click it, choose **Install** and approve the UAC prompt.

- **Update:** run a newer `RestoreWin10.exe` the same way. It restarts by itself; your settings are kept.
- **Uninstall:** *Settings › Apps › Installed apps*. Windows' own Start menu and taskbar come back.
- The file is not code-signed, so Windows shows the publisher as "Unknown" and SmartScreen may ask once.

## Features

**Start menu**
- Windows 10 app list: Recently added, Most used, A–Z with the letter grid
- Tiles in groups, tile folders, drag to rearrange
- Account and power menus, your Start folders, search (apps and Run-box commands)

**Taskbar**
- Windows 10 look, with Clear, Acrylic, Opaque or Normal styles
- Pinned programs and Store apps, live previews, jump lists
- Notification area with volume (device switcher), network and battery
- A taskbar on every screen

## Usage

| Command | What it does |
|---|---|
| `RestoreWin10.exe` | Downloaded copy: opens the installer. Installed copy: starts RestoreWin10 |
| `--preview` | Taskbar at the top, Windows' taskbar left alone |
| `--no-taskbar` | Start menu only |
| `--exit` | Closes RestoreWin10 and gives Windows' taskbar back |
| `--restore-taskbar` | Brings Windows' taskbar back after an abrupt end |
| `--install`, `--uninstall` | Without the window (add `--quiet` for no message boxes) |

- Right-click the taskbar for its menu and **Taskbar appearance**.
- Right-click the Start button for the Win+X menu.
- Settings and logs: `%APPDATA%\RestoreWin10`.

## Security

* Installed into Program Files, where only administrators can change it; the shell runs with your normal rights
* One UAC prompt to install or uninstall; the installer copies only the file Windows actually started, and writes
  nothing to your profile
* Every program it starts is named by full path, with arguments passed as a list; apps, tiles, pins and recent files
  must be on this PC, and web addresses and network paths are refused
* DLLs and .NET are loaded only from Windows' and .NET's own folders; .NET startup hooks are ignored
* Its single-instance lock and signals can be opened only by your account
* Settings, jump lists and program task lists are size-limited and validated before use
* No network access at all

Code comments refer to the findings of an internal threat-model review by ID (for example *F-09*, *V2-02*).

**Reporting a security problem:** please report it privately through
[Security › Report a vulnerability](../../security/advisories/new), not as a public issue, so that it can be fixed
before it becomes known. Use [issues](../../issues) for everything else.

## Troubleshooting

- **Windows key does nothing:** check your keyboard's game mode. Windows also blocks it while an administrator program is in front.
- **Windows' taskbar stays hidden** after RestoreWin10 was ended in Task Manager: run `RestoreWin10.exe --restore-taskbar`.
- **Something went wrong:** see `%APPDATA%\RestoreWin10\crash.log`.

## Known limitations

- No notification count on the notifications button
- System tray icons such as Bluetooth are not hosted
- Taskbar is always at the bottom and locked
- Explorer's ribbon can't be restored without injecting into Explorer

## Building from source

- Needs the .NET SDK 10.0.401 or later (pinned in `global.json`).
- `dotnet publish -c Release -o publish` produces `publish\RestoreWin10.exe`.
- A debug build runs beside an installed copy with its own settings; self-test: `RestoreWin10.exe --selftest`.
- Signing: `tools\sign-release.ps1 -File publish\RestoreWin10.exe -Thumbprint <thumbprint>`.

## Acknowledgements

Taskbar styles follow the ideas of [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) (no shared code).
Windows is a trademark of Microsoft.

## License

[MIT](LICENSE)
