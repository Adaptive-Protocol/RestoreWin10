# Changelog

## 2.0.12 (2026-10-26)

### Taskbar

* A taskbar button's preview stays open on the way up to it, even when the path crosses a neighbouring button.
  Touching another button closed it (when that program was not running) or replaced it at once, so a slightly
  slanted path from the button to its preview often closed it. Another button now takes over an open preview only
  once the pointer stays on it for a moment (0.2 s). Moving to another running program's button still shows its
  preview, after that moment.
* The thin gap between a preview and the taskbar counts as part of the way between them, so pausing there no
  longer closes the preview.

## 2.0.11 (2026-10-26)

### Taskbar

* The preview's X and "Close window" close programs running as administrator (Task Manager, for example) and
  programs that do not respond again. Since 2.0.4 they did nothing for these windows: closing them goes through
  Windows' own "end task" request, which failed when sent from the background thread the taskbar uses so that a
  frozen program cannot freeze it. Checked in every window state (normal, maximised, minimised from either),
  for ordinary programs and programs running as administrator; a program that does not respond gets Windows'
  own question whether to end it.
* As in Windows 10, a window's preview disappears as soon as its X is clicked, instead of when the program has
  closed and the taskbar next noticed. A program that asks to save keeps its window, and it is in the preview
  again the next time.

## 2.0.10 (2026-10-26)

### Taskbar

* A UAC prompt waiting on the taskbar opens again when its button is clicked. When a program in the background (an
  installer, for example) asks for permission, Windows puts a flashing button on the taskbar instead of showing the
  prompt at once; clicking it did nothing, because Windows refuses the usual restore request from a program with
  normal rights to a window with more. The same applied to minimised programs running as administrator (Task
  Manager, for example). RestoreWin10 now switches to such windows the way Alt+Tab does, and minimising them from
  their button works again too.
* Esc closes a jump list or taskbar menu, and the arrow keys and Enter work in it, as in Windows 10.

### Performance

* Fixed a handle leak: each time File Explorer was started from the taskbar or Start, RestoreWin10 kept some of the
  shell's registry handles open (about 800 after a few days of use). Windows' File Explorer and Control Panel
  shortcuts point at a shell folder rather than a program; they are now started as their program directly.

## 2.0.9 (2026-10-22)

### Installing and updating

* Double-click `RestoreWin10.exe` to install: an install window says whether it will install, update (for example
  2.0.8 → 2.0.9) or reinstall, and offers "Start RestoreWin10 when I sign in" (remembered for the next update). The
  finish page offers "Start RestoreWin10 now". There is no folder choice: RestoreWin10 always goes into Program Files,
  where only administrators can change it.
* Updating closes a running RestoreWin10 through Windows' Restart Manager and starts it again by itself afterwards,
  always with your normal rights.
* An older installer never replaces a newer installed version; the window says so and changes nothing.
* The installed copy opened by double-click starts RestoreWin10 (replacing the taskbar); the preview is now
  `--preview`. `--install` still installs without the window, for scripts.
* `tools\sign-release.ps1` signs and timestamps a release once a code-signing certificate is available. Releases are
  still unsigned for now, so check the SHA-256 before installing.

## 2.0.8 (2026-10-20)

### Volume flyout

* Choose the playback device, as in Windows 10: with two or more playback devices connected, the device name at
  the top of the volume flyout has a chevron and opens the list of devices; the current one is marked, and
  choosing another makes it the default for sound, media and calls. Windows' Default Beep then plays on the new
  device. If Windows does not allow the change, Settings > Sound opens instead.
* The slider jumps to wherever you press it and follows the mouse until you let go (it used to drag smoothly only
  from its thumb), and a click lands exactly where you click.
* Letting go of the slider plays Windows' Default Beep at the new level, as Windows 10 does.

## 2.0.5 (2026-10-19)

### Shell

* RestoreWin10 comes back after an installer or update closes it. Updaters (Windows Installer, Office
  Click-to-Run, Windows Update) close programs that hold files they must replace, through Windows' Restart
  Manager; RestoreWin10 can hold such files (icons and shell extensions of installed programs) and then stayed
  closed until the next sign-in. It now registers with Windows to be started again once the update is done.
  Crashes are still handled by the sign-in task, and End task in Task Manager still leaves it closed.
* Tested with Restart Manager: closed in order in about half a second, Windows' taskbar shown meanwhile, and
  started again with the taskbar replaced about four seconds later.

## 2.0.4 (2026-10-15)

### Taskbar

* A program that freezes (for example a game that hangs) can no longer freeze the taskbar with it. Minimising,
  switching to a window, cascading or tiling windows and closing them used Windows calls that wait for the program
  to respond, on the taskbar's own thread; they now hand the request over without waiting, or wait on a worker
  thread instead, as Explorer does. Windows of programs that do not respond are left out of cascading and tiling.
* Requests left waiting on frozen programs are limited and noted in `crash.log`.

## 2.0.3 (2026-10-12)

### Notification area

* Windows Security's icon opens Windows Security again after RestoreWin10 restarts (for example after an update).
  Some programs register their icon again with a short "modify" and send the full registration, which says where
  clicks go, only if the taskbar answers that the icon is unknown, as Explorer does. RestoreWin10 now answers the
  same way, so their icons stay clickable.
* If Windows Security's own icon still cannot take clicks, clicking it opens Windows Security directly.

## 2.0.2 (2026-10-12)

### Notification area

* An icon whose program gives no tooltip shows the program's name on hover, in the `^` flyout and on the taskbar.
  Electron apps such as Claude register their icon again without a tooltip after the taskbar restarts (for
  example when RestoreWin10 is updated), so their icons used to show nothing. A program's own tooltip still wins.

## 2.0.1 (2026-10-09)

### Taskbar

* Store and packaged apps (such as Claude) can be pinned to the taskbar from Start, from the app list and from tiles
* Their buttons and pins go by the app's ID instead of the program file inside its version-named folder, so a pin
  keeps working after the app updates, and a pinned Store app starts the way Windows starts it
* Pins of such apps made by earlier versions are converted automatically and keep their place

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
