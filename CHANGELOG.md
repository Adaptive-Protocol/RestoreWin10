# Changelog

## 2.0.0 (2026-10-09)

First public release: a rebuild of RestoreWin10 1.x on .NET 10, based on a threat-model review.

### Security

* Installs into Program Files with one UAC prompt; the shell runs without administrator rights
* The installer copies only the file Windows started and never writes to the user's profile
* One launcher for everything: full paths, argument lists, local programs and documents only
* DLL and .NET search limited to Windows' and .NET's folders; .NET startup hooks ignored
* Single-instance lock and signals restricted to the user's account, with an ownership check
* Settings, jump lists and program task lists size-limited and validated
* Notification-area requests checked; only the messages Windows itself sends are passed to programs
* Search runs only recognised items; command lookup stays on this PC

### Start menu

* Recently added, Start Menu folders, letter grid, live updates when programs are installed or removed
* Theme-aware tiles, group handles, tile folders
* Left strip with account and power panels and the Folders setting; Settings opens on its main page

### Taskbar

* Windows 10 details: logo, search icon, button size, running-program line, centred clock
* Taskbar styles (Clear, Acrylic, Opaque, Normal)
* Battery icon on PCs with a battery
* Jump lists with pinned folders and programs' own tasks
* `^` overflow as a square grid
* *Close window* also closes programs running as administrator
* Restart after a crash; `--restore-taskbar` for recovery

### Performance

* About 48 MB private memory (down from 68 MB in early 2.0 builds and 99–160 MB in 1.x)
