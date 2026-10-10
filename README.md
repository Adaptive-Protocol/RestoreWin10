# RestoreWin10

![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

**RestoreWin10** brings the Windows 10 Start menu and taskbar back to Windows 11. It is written from scratch in C#
(.NET 10, WPF) and runs as its own program: it never injects code into Explorer, so Windows updates cannot break it the
way they break injection-based shell tools.

It was built security-first: it installs into Program Files with one administrator prompt, runs without administrator
rights, starts programs only by full path, and treats every file other programs can write as untrusted.

## How it works

At sign-in, a scheduled task starts RestoreWin10 for your account. It hides the Windows 11 taskbar, docks its own
Windows 10 style taskbar at the bottom of every screen, and hosts the notification area; the Windows key and the Start
button open the Windows 10 Start menu. RestoreWin10 follows Windows' own settings (light or dark mode, accent colour,
transparency, taskbar and Start options), so the Settings app keeps working as you expect.

If RestoreWin10 is closed, Windows' own taskbar comes back. If it crashes, it is started again a few seconds later.

## Key features

**Start menu**

* **Windows 10 app list**: "Recently added" at the top, "Most used" (when enabled in Windows), `&`, `#` and A–Z groups,
  Start Menu folders that open in place, and the letter grid for jumping
* **Live updates**: programs you install or remove appear and disappear without a restart, Store apps included
* **Tiles**: theme-aware tiles in groups; drag to reorder, create groups, move whole groups by their handle,
  resize, and drop one tile onto another to make a **tile folder**
* **Left strip**: account panel (lock, sign out, switch user), power panel (sleep, hibernate, shut down, restart,
  "update and …"), and the folders chosen in *Settings › Personalization › Start › Folders*
* **Search**: finds apps by name or program name (`regedit`) and anything the Run box would start (`services.msc`);
  Enter starts only a recognised result

**Taskbar**

* **Windows 10 look**: Windows 10 logo, search icon, Task View, program buttons with the accent-tinted line under running
  programs, two-line clock, notifications button and show-desktop strip
* **Styles**: *Clear* (the wallpaper shows through), *Acrylic*, *Opaque* or *Normal* (follows Windows), from the
  taskbar's right-click menu; the normal look returns while Start, Search or Task View is open
* **Program buttons**: your Windows taskbar pins carried over, live thumbnails, drag to reorder, attention flashing;
  pin any app from Start, Store apps included (they are pinned by app ID, so the pin survives their updates)
* **Jump lists**: pinned and recent items, File Explorer's pinned folders, and each program's own tasks
  (Firefox: *New private window*); every task shows what it runs
* **Notification area**: program tray icons (with the `^` overflow as a square grid), volume flyout, network, and a
  battery icon on PCs that have a battery
* **Several screens**: a taskbar on every screen, following Windows' multiple-display settings

## Requirements

* Windows 11 (developed and tested on build 26200, 64-bit)
* [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0); Microsoft Update keeps it patched
* An administrator account for installing and uninstalling (the program itself runs without administrator rights)

## Installation

1. Download `RestoreWin10.exe` from the [latest release](../../releases/latest).
2. Check that the file is the one published (the release lists its SHA-256):
   ```
   certutil -hashfile RestoreWin10.exe SHA256
   ```
3. Install it:
   ```
   RestoreWin10.exe --install
   ```
   Approve the UAC prompt. The file is not code-signed, so Windows shows the publisher as "Unknown", and SmartScreen may
   ask for confirmation the first time.

The installer copies the program to `C:\Program Files\RestoreWin10`, adds RestoreWin10 to *Settings › Apps › Installed
apps*, registers the sign-in task, and starts it. Running `--install` again with a newer file updates it.

**Uninstall** from *Settings › Apps › Installed apps*, or with `RestoreWin10.exe --uninstall`. The Windows 11 Start menu
and taskbar come back; your tiles and settings stay in `%APPDATA%\RestoreWin10` in case you install it again.

## Usage

| Command | What it does |
|---|---|
| `RestoreWin10.exe --replace` | Normal use (what the sign-in task runs): replaces the Windows 11 taskbar |
| `RestoreWin10.exe` | Preview: docks the taskbar at the top and leaves the Windows 11 taskbar alone; when already running, opens or closes Start |
| `RestoreWin10.exe --no-taskbar` | Start menu only |
| `RestoreWin10.exe --exit` | Closes the running copy and gives the Windows taskbar back |
| `RestoreWin10.exe --restore-taskbar` | Brings the Windows 11 taskbar back after RestoreWin10 was ended abruptly |
| `--install`, `--uninstall` | Install or remove (add `--quiet` to skip message boxes) |

Right-click an empty part of the taskbar for the Windows 10 taskbar menu, including **Taskbar appearance**.
Right-click the Start button for the Win+X menu. Ctrl+click an icon in the `^` panel to pin it to the taskbar.

Settings and diagnostics live in `%APPDATA%\RestoreWin10`: `state.json` (tiles, pins), `crash.log`, `tray.log` and
`dock.log`.

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

## Building from source

Requires the .NET SDK 10.0.401 or later in the 10.0 band (pinned in `global.json`).

```
dotnet publish -c Release -o publish
```

The result is the single file `publish\RestoreWin10.exe`. A debug build (`dotnet build`) is a development copy: it runs
beside an installed RestoreWin10 with its own settings (`%APPDATA%\RestoreWin10.Dev`), never installs itself, and has a
self-test:

```
bin\Debug\net10.0-windows\RestoreWin10.exe --selftest
```

### Project structure

| Folder | Contents |
|---|---|
| `Start\` | Start menu: app list, tiles and folders, search, the left strip |
| `Taskbar\` | Taskbars, program buttons, thumbnails, jump lists, search box |
| `NotificationArea\` | Tray host, `^` overflow, volume flyout, battery |
| `Platform\` | Launching, installer, hardening, screens, app-bar docking, personalization, session actions |
| `Assets\` | Icon |

## Troubleshooting

* **The Windows key does nothing**: check your keyboard's game mode (many gaming keyboards turn the Windows key off
  in it). Windows also does not pass keys to RestoreWin10 while a program running as administrator is in front.
* **The Windows taskbar stays on auto-hide** after RestoreWin10 was ended from Task Manager: run
  `RestoreWin10.exe --restore-taskbar`.
* **Something went wrong**: see `%APPDATA%\RestoreWin10\crash.log`.

## Known limitations

* No notification count on the notifications button (Windows gives it only to packaged apps with a special permission)
* System tray icons such as Bluetooth are not hosted; battery, sound and network are RestoreWin10's own
* The taskbar is always at the bottom and locked
* Explorer's file-window ribbon cannot be restored without injecting into Explorer

## Acknowledgements

The taskbar styles follow the ideas of [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) (no code is
shared). Windows, Windows 10 and Windows 11 are trademarks of Microsoft.

## License

[MIT](LICENSE)
