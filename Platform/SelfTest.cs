#if DEV_BUILD
using System.IO;
using System.Text;

namespace RestoreWin10;

/// <summary>
/// Development builds only: `RestoreWin10.exe --selftest` checks the security rules without starting any program or
/// showing any window, writes the results to selftest.txt in the development settings folder, and exits with the number
/// of failures.
/// </summary>
static class SelfTest
{
    public static int Run()
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(string what, bool ok)
        {
            if (!ok) failures++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        }

        // ---- Hardening.Apply ran in App's static constructor ----
        Check("current folder is System32", string.Equals(Environment.CurrentDirectory.TrimEnd('\\'), Environment.SystemDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        // ---- what Launcher accepts ----
        var sys = Environment.SystemDirectory;
        foreach (var (path, expected) in new (string, bool)[]
        {
            (@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Firefox.lnk", true),
            (@"C:\Program Files\Example\Example.EXE", true),
            (Path.Combine(sys, "eventvwr.msc"), true),
            (@"shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", true),
            ("https://example.com/setup.exe", false),
            (@"\\server\share\tool.exe", false),
            (@"\\?\C:\Windows\notepad.exe", false),
            ("//server/share/tool.exe", false),
            (@"notepad.exe", false),
            (@"C:relative.exe", false),
            (@"C:\Tools\app.exe:hidden", false),
            (@"C:\Tools\script.bat", false),
            (@"C:\Tools\page.url", false),
            ("C:\\Tools\\a\".exe", false),
            (@"shell:AppsFolder\..\..\cmd.exe", false),
            (@"shell:AppsFolder\NoBangHere", false),
            ("", false),
        })
            Check($"IsLaunchable({(path.Length == 0 ? "(empty)" : path)}) = {expected}", Launcher.IsLaunchable(path) == expected);

        // ---- state.json validation ----
        const string json = """
        {
          "Groups": [ { "Name": "Test", "Tiles": [
              { "Path": "C:\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\Firefox.lnk", "Size": "W" },
              { "Path": "https://example.com/x.exe", "Size": "M" },
              { "Path": "\\\\server\\share\\x.lnk", "Size": "Q" },
              { "Path": "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Size": "Q" } ] } ],
          "TaskbarPins": [ "C:\\Windows\\explorer.exe", "C:\\Tools\\evil.bat", "ftp://x/y.exe",
                           "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "shell:AppsFolder\\..\\x!y" ],
          "TaskbarPinLaunch": { "C:\\Windows\\explorer.exe": "C:\\Users\\a\\x.url", "C:\\Program Files\\A\\a.exe": "C:\\Users\\a\\a.lnk" },
          "Usage": { "C:\\ok.lnk": 3, "\\\\srv\\x.lnk": 2, "C:\\zero.lnk": 0 },
          "StartHeight": 7.5, "SearchMode": 9, "KeepBlueWhileAccent": "not-hex"
        }
        """;
        var st = State.Parse(json, out var rejected);
        Check("tiles: 2 of 4 kept (local shortcut, Store id)", st.Groups.Single().Tiles.Count == 2);
        Check("tiles: unknown size becomes M", st.Groups.Single().Tiles.All(t => t.Size is "W" or "M"));
        Check("taskbar pins: the local program and the Store app id kept",
              st.TaskbarPins.SequenceEqual(new[] { @"C:\Windows\explorer.exe", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" }));
        Check("pin shortcuts: only the .lnk kept", st.TaskbarPinLaunch.Count == 1 && st.TaskbarPinLaunch.ContainsKey(@"C:\Program Files\A\a.exe"));
        Check("usage: network path and zero count left out", st.Usage.Count == 1 && st.Usage.ContainsKey(@"C:\ok.lnk"));
        Check("numbers brought into range", st.StartHeight == 0 && st.SearchMode == 2 && st.KeepBlueWhileAccent == null);
        Check($"rejected list names what was left out ({rejected.Count})", rejected.Count == 7);

        // ---- packaged apps on the taskbar (pinned by app id, not by their versioned exe) ----
        Check("package family from a WindowsApps exe",
              TaskTracker.PackageFamilyOf(@"C:\Program Files\WindowsApps\Example.App_2.1.0.0_x64__abcdefghjkmnp\app\example.exe") == "Example.App_abcdefghjkmnp");
        Check("no package family for an ordinary program", TaskTracker.PackageFamilyOf(@"C:\Program Files\Example\example.exe") == null);
        Check("no package family for an odd WindowsApps folder", TaskTracker.PackageFamilyOf(@"C:\Program Files\WindowsApps\odd\x.exe") == null);
        Check("Store key from an app id", TaskTracker.StoreKey("Example.App_abcdefghjkmnp!App") == @"shell:AppsFolder\Example.App_abcdefghjkmnp!App");
        Check("no Store key from a malformed id", TaskTracker.StoreKey(@"..\x") == null && TaskTracker.StoreKey(null) == null);
        Check("this process is not a packaged app", Win.PackagedAumid((uint)Environment.ProcessId) == null);
        var rk = State.Parse("""{ "TaskbarPins": [ "C:\\Windows\\explorer.exe", "C:\\Program Files\\WindowsApps\\Example.App_2.1.0.0_x64__abcdefghjkmnp\\app\\example.exe", "C:\\Windows\\notepad.exe" ] }""", out _);
        bool moved = TaskTracker.RekeyPin(rk, @"C:\Program Files\WindowsApps\Example.App_2.1.0.0_x64__abcdefghjkmnp\app\example.exe", @"shell:AppsFolder\Example.App_abcdefghjkmnp!App");
        Check("an old exe pin becomes the app id in the same place",
              moved && rk.TaskbarPins.SequenceEqual(new[] { @"C:\Windows\explorer.exe", @"shell:AppsFolder\Example.App_abcdefghjkmnp!App", @"C:\Windows\notepad.exe" }));
        Check("re-keying an exe that is not pinned changes nothing", !TaskTracker.RekeyPin(rk, @"C:\x\y.exe", @"shell:AppsFolder\A_b!C"));

        // ---- named objects ----
        string name = Identity.Name + ".SelfTest." + Environment.ProcessId;
        using (var first = NamedObjects.Lock(name, out bool isFirst, out bool trusted))
        {
            Check("lock: first holder is first and trusted", first != null && isFirst && trusted);
            using var second = NamedObjects.Lock(name, out bool isFirst2, out bool trusted2);
            Check("lock: second taker sees it held, made by this account", second != null && !isFirst2 && trusted2);
        }
        using (var signal = NamedObjects.CreateSignal(name + ".Signal"))
        {
            Check("signal: created", signal != null);
            Check("signal: can be set by name", NamedObjects.SetExisting(name + ".Signal"));
            Check("signal: setting it wakes a waiter", signal != null && signal.WaitOne(1000));
        }
        Check("signal: one that does not exist cannot be set", !NamedObjects.SetExisting(name + ".Missing"));

        // ---- setup pieces that can be checked without administrator rights ----
        try
        {
            var image = Setup.ReadOwnImage();
            Check("installer: own exe read and confirmed as the running image", image.Length == new FileInfo(Environment.ProcessPath!).Length && image[0] == 'M' && image[1] == 'Z');
        }
        catch (Exception ex) { Check("installer: own exe read and confirmed as the running image (" + ex.Message + ")", false); }
        using (var other = new FileStream(Path.Combine(Environment.SystemDirectory, "notepad.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Check("installer: a different file is not taken for the running image", !Setup.IsRunningImage(other));
        var copy = Path.Combine(Path.GetTempPath(), "RestoreWin10_selftest_" + Environment.ProcessId + ".exe");
        try
        {
            File.Copy(Environment.ProcessPath!, copy, overwrite: true);   // same bytes, different file
            using var twin = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
            Check("installer: an identical copy is not taken for the running image", !Setup.IsRunningImage(twin));
        }
        finally { try { File.Delete(copy); } catch { } }

        var xml = new System.Xml.XmlDocument();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        xml.LoadXml(SignInTask.Xml(@"C:\Program Files\RestoreWin10\RestoreWin10.exe", user));
        var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        Check("sign-in task: well-formed, starts the Program Files copy with --replace",
              xml.SelectSingleNode("//t:Exec/t:Command", ns)?.InnerText == @"C:\Program Files\RestoreWin10\RestoreWin10.exe" &&
              xml.SelectSingleNode("//t:Exec/t:Arguments", ns)?.InnerText == "--replace");
        Check("sign-in task: this account, not elevated, normal priority",
              xml.SelectSingleNode("//t:Principal/t:UserId", ns)?.InnerText == user.Value &&
              xml.SelectSingleNode("//t:Principal/t:RunLevel", ns)?.InnerText == "LeastPrivilege" &&
              xml.SelectSingleNode("//t:Settings/t:Priority", ns)?.InnerText == "4");
        try
        {
            SignInTask.DeleteIfPresent(SignInTask.RootFolder(), "RestoreWin10.SelfTest.NoSuchTask");
            Check("sign-in task: deleting a task that is not there is not an error (second install)", true);
        }
        catch (Exception ex) { Check($"sign-in task: deleting a task that is not there is not an error ({ex.GetType().Name}: {ex.Message})", false); }
        Check("sign-in task: also starts RestoreWin10 again 5 s after Windows records it crashing",
              xml.OuterXml.Contains("<EventTrigger>") && xml.OuterXml.Contains("Application Error") && xml.OuterXml.Contains("RestoreWin10.exe") && xml.OuterXml.Contains("<Delay>PT5S</Delay>"));
        var crashClock = new DateTime(2026, 10, 9, 12, 0, 0);
        var crashLines = new[]
        {
            "2026-10-09 11:52:00 [fatal] System.InvalidOperationException", "   at somewhere", "",
            "2026-10-09 11:55:30 [UI thread] System.Exception", "2026-10-09 11:58:10 [fatal] x", "2026-10-09 11:40:00 [fatal] too old",
        };
        Check("crash loop: fatal crashes counted only within the ten minutes", CrashLog.CountFatal(crashLines, crashClock, TimeSpan.FromMinutes(10)) == 2);
        Check("install folder is under Program Files", Setup.InstalledExe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\RestoreWin10\", StringComparison.OrdinalIgnoreCase));

        var st2 = State.Parse("""{ "ExplorerTaskbarState": 9 }""", out _);
        Check("taskbar state out of range is dropped", st2.ExplorerTaskbarState == null);
        var st3 = State.Parse("""{ "ExplorerTaskbarState": 2 }""", out _);
        Check("taskbar state in range is kept", st3.ExplorerTaskbarState == 2);
        Check("taskbar style: a known one is kept", State.Parse("""{ "TaskbarStyle": "acrylic" }""", out _).TaskbarStyle == "acrylic");
        var big = Path.Combine(Path.GetTempPath(), "RestoreWin10.SelfTest.big.json");
        try
        {
            File.WriteAllText(big, """{ "Groups": [] }""" + new string(' ', 5 * 1024 * 1024));
            var loaded = State.LoadFrom(big);
            Check("settings: an oversized state.json is not read, and is kept aside as .bad", loaded.Groups.Count == 0 && File.Exists(big + ".bad"));
        }
        finally { try { File.Delete(big); File.Delete(big + ".bad"); } catch { } }
        Check("taskbar style: anything else means the default (clear)", State.Parse("""{ "TaskbarStyle": "<script>" }""", out _).TaskbarStyle == null);

        // ---- the app list (Phase 3) ----
        Check("groups: & # A-Z globe", AppList.KeyOf("(Beta) Tool") == "&" && AppList.KeyOf("7-Zip") == "#" && AppList.KeyOf("calibre") == "C" &&
                                       AppList.KeyOf("Écran") == "E" && AppList.KeyOf("Пример") == "globe" && AppList.KeyOf("") == "&");
        Check("groups: Windows 10 order", new[] { "globe", "B", "#", "&", "A" }.OrderBy(AppList.Order).SequenceEqual(new[] { "&", "#", "A", "B", "globe" }));

        var now = new DateTime(2026, 10, 8, 12, 0, 0);
        AppEntry App(string name, string? folder = null) => new() { Name = name, Path = $@"C:\SM\{folder}\{name}.lnk".Replace(@"\\", @"\"), Folder = folder };
        var apps = new List<AppEntry> { App("Access"), App("7-Zip File Manager", "7-Zip"), App("7-Zip Help", "7-Zip"), App("Word"), App("Excel"),
                                        App("Weather"), App("Calculator"), App("calibre 64bit", "calibre"), App("E-book viewer", "calibre") };
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        DateTime? Created(string p) => p.Contains("Weather") ? now.AddDays(-1) : p.Contains("Word") ? now.AddDays(-2) : now.AddDays(-30);
        AppList.NoteFirstSeen(seen, apps, now, Created);
        Check("first run: shortcut dates decide what is recent", seen[apps[5].Path] == now.AddDays(-1) && seen[apps[0].Path] == now.AddDays(-30));
        var newcomer = App("Zoom");
        AppList.NoteFirstSeen(seen, apps.Append(newcomer).ToList(), now, Created);
        Check("later: a new app is stamped now", seen[newcomer.Path] == now);
        apps.Add(newcomer);

        var rows = AppList.Build(apps, new Dictionary<string, int>(), seen, now, showRecent: true, showMostUsed: false, new HashSet<string>());
        var recent = rows.SkipWhile(r => r.Header != "Recently added").Skip(1).TakeWhile(r => r.IsApp).Select(r => r.App!.Name).ToList();
        Check("recently added: top of the list, newest first, at most 3", rows[0].Header == "Recently added" && recent.SequenceEqual(new[] { "Zoom", "Weather", "Word" }));
        Check("folders: listed once under their letter, closed", rows.Count(r => r.IsFolder) == 2 && rows.Any(r => r.Folder?.Name == "7-Zip") && !rows.Any(r => r.IsIndented));
        Check("headers: & first is absent, # before A", rows.Where(r => r.IsHeader && r.Key != AppList.Recent).Select(r => r.Key).First() == "#");
        var open = AppList.Build(apps, new Dictionary<string, int>(), seen, now, true, false, new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { "calibre" });
        int fi = open.FindIndex(r => r.Folder?.Name == "calibre");
        Check("open folder: its apps follow it, indented", fi >= 0 && open[fi + 1].IsIndented && open[fi + 2].IsIndented && !open[fi + 3].IsIndented);
        var noRecent = AppList.Build(apps, new Dictionary<string, int>(), seen, now, showRecent: false, showMostUsed: false, new HashSet<string>());
        Check("Windows setting off: no Recently added", !noRecent.Any(r => r.Header == "Recently added"));
        AppList.NoteLaunched(seen, newcomer.Path, now);
        var afterLaunch = AppList.Build(apps, new Dictionary<string, int>(), seen, now, true, false, new HashSet<string>());
        Check("started once: leaves Recently added", !afterLaunch.TakeWhile(r => r.Header != "#").Any(r => r.App == newcomer));
        var keys = AppList.JumpKeys(rows);
        Check("jump grid: clock, &, #, A-Z, globe; empty ones dimmed", keys.Count == 30 && keys[0].Enabled && !keys[1].Enabled && keys[2].Enabled &&
                                                                        keys.First(k => k.Key == "B").Enabled == false && keys.First(k => k.Key == "W").Enabled);
        var later = apps.Where(a => a.Name != "Excel").ToList();
        AppList.NoteFirstSeen(seen, later, now, Created);
        Check("uninstalled: forgotten", !seen.ContainsKey(apps.First(a => a.Name == "Excel").Path));

        // ---- Start's left strip: Windows' Folders setting (Phase 4) ----
        var docs = new Guid("2d34d5ce-fa5a-4543-82f2-22e6eaf7773c");
        var pics = new Guid("383f07a0-e80a-4c80-b05a-86db845dbc4d");
        var setting = new Guid("52730886-51aa-4243-9f7b-2776584659d4");
        var raw = docs.ToByteArray().Concat(pics.ToByteArray()).Concat(setting.ToByteArray()).Concat(new byte[5]).ToArray();   // + a stray tail
        var on = StartPlaces.Enabled(raw);
        Check("strip folders: Windows' list of ids is read (Documents, Pictures, Settings)", on.SetEquals(new[] { docs, pics, setting }));
        Check("strip folders: nothing set means none", StartPlaces.Enabled(null).Count == 0 && StartPlaces.Enabled(Array.Empty<byte>()).Count == 0);
        // ---- tile folders (Phase 4b) ----
        const string folders = """
        { "Groups": [ { "Name": "G", "Tiles": [
            { "Path": "", "Size": "M", "Name": "Office", "Items": [
                { "Path": "C:\\SM\\Word.lnk", "Size": "M" }, { "Path": "C:\\SM\\Excel.lnk", "Size": "Q" }, { "Path": "https://x/y.exe", "Size": "M" } ] },
            { "Path": "", "Size": "W", "Items": [ { "Path": "C:\\SM\\Only.lnk", "Size": "S" }, { "Path": "\\\\srv\\a.lnk", "Size": "M" } ] },
            { "Path": "", "Size": "M", "Items": [ { "Path": "", "Size": "M", "Items": [ { "Path": "C:\\SM\\Deep.lnk", "Size": "M" } ] } ] },
            { "Path": "C:\\SM\\Plain.lnk", "Size": "M" } ] } ] }
        """;
        var fs = State.Parse(folders, out var fr);
        var ft = fs.Groups.Single().Tiles;
        Check("folders: a folder keeps its good tiles and name", ft[0].Items?.Count == 2 && ft[0].Name == "Office" && ft[0].Items![1].Size == "M");
        Check("folders: one good tile left = that tile", ft[1].Items == null && ft[1].Path == @"C:\SM\Only.lnk" && ft[1].Size == "S");
        Check("folders: a folder inside a folder is refused (and the empty folder goes)", ft.Count == 3 && ft[2].Path == @"C:\SM\Plain.lnk");
        Check($"folders: what was left out is listed ({fr.Count})", fr.Count == 3);
        var bandTiles = new[] { new Tile { App = Tile.FolderPlaceholder, Size = "M" }, new Tile { App = Tile.FolderPlaceholder, Size = "W" } };
        Check("folder band: chrome plus one row of medium/wide tiles", Math.Abs(TilePanel.BandHeight(bandTiles) - (TilePanel.BandChrome + 98)) < 0.01);

        // ---- Phase 6: the notification-area host (F-06) ----
        Check("tray: only app-bar requests and icon-position queries go on to Explorer",
              TrayHost.Relays(0) && TrayHost.Relays(3) && !TrayHost.Relays(2) && !TrayHost.Relays(7));
        Check("tray: callbacks only in the application range",
              !TrayHost.IsAppMessage(0x10) && TrayHost.IsAppMessage(0x400) && TrayHost.IsAppMessage(0xC123) && !TrayHost.IsAppMessage(0x10000));
        using (var owner = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("RestoreWin10.SelfTest")))
        {
            var host = new TrayHost(new State());
            bool Send(int cmd, nint hwnd, uint uid, uint flags, uint callback)
            {
                var raw = new byte[8 + 804];
                BitConverter.GetBytes(cmd).CopyTo(raw, 4);
                BitConverter.GetBytes((int)hwnd).CopyTo(raw, 8 + 4);
                BitConverter.GetBytes(uid).CopyTo(raw, 8 + 8);
                BitConverter.GetBytes(flags).CopyTo(raw, 8 + 12);
                BitConverter.GetBytes(callback).CopyTo(raw, 8 + 16);
                var mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(raw.Length);
                try { System.Runtime.InteropServices.Marshal.Copy(raw, 0, mem, raw.Length); return host.HandleTray(mem, raw.Length); }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mem); }
            }
            Check("tray: an icon for a window that does not exist is refused", !Send(0, 0x7FFFFFF0, 7, 1, 0x8001) && host.Find(0x7FFFFFF0, 7) == null);
            Check("tray: a system message (WM_CLOSE) as callback is not kept", Send(0, owner.Handle, 7, 1, 0x10) && host.Find(owner.Handle, 7)?.Callback == 0);
            Check("tray: an application message is kept, with the owner's process",
                  Send(1, owner.Handle, 7, 1, 0x8001) && host.Find(owner.Handle, 7) is { Callback: 0x8001 } t && t.Pid == (uint)Environment.ProcessId);
            Check("tray: removed on request", Send(2, owner.Handle, 7, 0, 0) && host.Find(owner.Handle, 7) == null);
            // Windows Security re-registers after a taskbar restart with a "modify" and no callback, and sends the full
            // "add" only if that fails, as it does with Explorer.
            Check("tray: a modify for an unknown icon without a callback shows it but reports failure",
                  !Send(1, owner.Handle, 9, 0x2, 0) && host.Find(owner.Handle, 9) is { Callback: 0 });
            Check("tray: the full add that follows supplies the callback",
                  Send(0, owner.Handle, 9, 0x3, 0x8002) && host.Find(owner.Handle, 9) is { Callback: 0x8002 });
            Check("tray: a modify for an unknown icon that carries its callback still succeeds",
                  Send(1, owner.Handle, 10, 0x3, 0x8003) && host.Find(owner.Handle, 10) is { Callback: 0x8003 });
            // An Electron app re-registers after a taskbar restart with only the icon and the callback (flags 0x3).
            Check("tray: an icon added without a tooltip shows its program's name",
                  Send(0, owner.Handle, 8, 0x3, 0x8001) && host.Find(owner.Handle, 8) is { Tooltip.Length: 0 } nt &&
                  nt.DisplayTip.Length > 0 && nt.DisplayTip == TrayIcon.ProgramName(Environment.ProcessPath!));
        }
        // ---- choosing the playback device in the volume flyout (read-only here: the default is never changed) ----
        var (audioDevices, audioDefault) = Audio.Devices();
        Check($"audio: the active playback devices are listed ({audioDevices.Count})", audioDevices.All(d => d.Id.Length > 0 && d.Name.Length > 0));
        Check("audio: the default device is one of them", audioDefault == null || audioDevices.Any(d => d.Id == audioDefault));
        Check("audio: Windows' default-device interface is available", Audio.CanSetDefault());
        Check("audio: an id that is not an active device is refused", !Audio.SetDefault("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"));
        log.AppendLine($"info  this PC's playback devices: {string.Join(" | ", audioDevices.Select(d => d.Name + (d.Id == audioDefault ? " (default)" : "")))}");
        // The volume slider: a point on the track maps straight to a level (a 200-px track, 8-px thumb, 0..100).
        Check("volume slider: the ends of the thumb's travel are 0 and 100",
              VolumeFlyout.LevelFromX(4, 200, 8, 0, 100) == 0 && VolumeFlyout.LevelFromX(196, 200, 8, 0, 100) == 100);
        Check("volume slider: the middle is 50, a quarter of the travel is 25",
              VolumeFlyout.LevelFromX(100, 200, 8, 0, 100) == 50 && VolumeFlyout.LevelFromX(4 + 48, 200, 8, 0, 100) == 25);
        Check("volume slider: points beyond the ends stay at 0 and 100",
              VolumeFlyout.LevelFromX(-30, 200, 8, 0, 100) == 0 && VolumeFlyout.LevelFromX(260, 200, 8, 0, 100) == 100);
        Check("volume slider: the same point always gives the same level (no dependence on the previous value)",
              Enumerable.Range(0, 5).Select(_ => VolumeFlyout.LevelFromX(137, 200, 8, 0, 100)).Distinct().Count() == 1);

        // ---- closed by an installer, started again afterwards ----
        Check("restart after updates: '--replace', and not after a crash, a hang or a restart of the PC",
              AppRestart.CommandLine == "--replace" && AppRestart.Flags == (1 | 2 | 8));
        if (AppRestart.Register())
        {
            var reg = AppRestart.Registered();
            Check($"restart after updates: Windows has it on record ({reg?.CommandLine} / {reg?.Flags})",
                  reg is { } r && r.CommandLine == AppRestart.CommandLine && r.Flags == AppRestart.Flags);
            AppRestart.Unregister();
        }
        else Check("restart after updates: registration accepted", false);
        Check("an installer's close (WM_ENDSESSION, ENDSESSION_CLOSEAPP) is told apart from a sign-out",
              AppRestart.IsInstallerClose(1, 0x1) && !AppRestart.IsInstallerClose(1, 0) &&
              !AppRestart.IsInstallerClose(1, unchecked((nint)0x80000000) /* ENDSESSION_LOGOFF */) && !AppRestart.IsInstallerClose(0, 0x1));

        // ---- a frozen program cannot freeze the taskbar ----
        // A window whose thread stops handling messages, as a game's does when it hangs. Minimising it, switching to
        // it and closing it must return at once; only a worker thread may wait on it.
        {
            using var frozen = new ManualResetEventSlim();
            using var created = new ManualResetEventSlim();
            nint frozenHwnd = 0;
            var owner = new Thread(() =>
            {
                // Hidden and off-screen: the test never shows anything or takes the foreground from the user.
                var src = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("RestoreWin10.SelfTest.Frozen")
                    { WindowStyle = unchecked((int)0x80000000) /* WS_POPUP, not visible */, PositionX = -32000, PositionY = -32000, Width = 10, Height = 10 });
                frozenHwnd = src.Handle;
                created.Set();
                // "Frozen": no messages handled until released. (Not frozen.Wait: a wait on an STA thread still
                // handles sent messages, so the window would answer.)
                var until = DateTime.Now.AddSeconds(10);
                while (!frozen.IsSet && DateTime.Now < until) Thread.Sleep(20);
                src.Dispose();
            }) { IsBackground = true };
            owner.SetApartmentState(ApartmentState.STA);
            owner.Start();
            created.Wait(5_000);
            int before = Win.WaitingRequests;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Win.MinimizeOther(frozenHwnd);
            // What switching does to a frozen window (a call that waits on it), run the way switching runs it.
            Win.OffUiThread(() => Win.SendMessage(frozenHwnd, 0 /* WM_NULL: waits for the program, changes nothing */, 0, 0), "self-test");
            Check($"frozen program: minimise and switch return at once ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds < 500);
            Thread.Sleep(200);
            Check("frozen program: the waiting is done by a worker thread", Win.WaitingRequests == before + 1);
            frozen.Set();
            owner.Join(5_000);
            var deadline = DateTime.Now.AddSeconds(5);
            while (Win.WaitingRequests > before && DateTime.Now < deadline) Thread.Sleep(50);
            Check("frozen program: the worker finishes once the program responds again", Win.WaitingRequests == before);
        }
        {
            using var gate = new ManualResetEventSlim();
            int before = Win.WaitingRequests;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Win.OffUiThread(() => gate.Wait(5_000), "self-test");
            Check($"window requests run off the UI thread ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds < 200 && Win.WaitingRequests == before + 1);
            for (int i = 0; i < 6; i++) Win.OffUiThread(() => gate.Wait(5_000), "self-test");
            Check("at most four requests wait at a time; more are dropped", Win.WaitingRequests == 4);
            gate.Set();
            var deadline = DateTime.Now.AddSeconds(5);
            while (Win.WaitingRequests > before && DateTime.Now < deadline) Thread.Sleep(50);
            Check("waiting requests are released", Win.WaitingRequests == before);
        }

        Check("tray: only System32's SecurityHealthSystray.exe counts as Windows Security",
              TrayHost.IsWindowsSecurity(new TrayIcon { Owner = Path.Combine(Environment.SystemDirectory, "SecurityHealthSystray.exe") }) &&
              !TrayHost.IsWindowsSecurity(new TrayIcon { Owner = @"C:\Users\a\Downloads\SecurityHealthSystray.exe" }));
        Check("tray: the program's own tooltip wins when it gives one",
              new TrayIcon { Owner = Path.Combine(Environment.SystemDirectory, "notepad.exe"), Tooltip = "Mine" }.DisplayTip == "Mine");
        Check("tray: a program name comes from its file description",
              TrayIcon.ProgramName(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) is { Length: > 0 } en && en != "explorer");

        // ---- Phase 6: jump lists (F-09) and the Run-box lookup (F-10) ----
        Check("jump lists: full paths are offered, local or network",
              Launcher.IsDocumentPath(@"C:\Users\a\report.docx") && Launcher.IsDocumentPath(@"\\server\share\report.docx"));
        Check("jump lists: shell names, web addresses, device paths and quotes are not",
              !Launcher.IsDocumentPath("knownfolder:{FDD39AD0-238F-46AF-ADB4-6C85480369C7}") && !Launcher.IsDocumentPath("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}") &&
              !Launcher.IsDocumentPath("https://example.com/a.docx") && !Launcher.IsDocumentPath(@"\\?\C:\a.txt") && !Launcher.IsDocumentPath("C:\\a\"b.txt"));
        Check("network paths are not on this PC (no icon lookups there)", Launcher.IsOnThisPc(Environment.SystemDirectory) && !Launcher.IsOnThisPc(@"\\server\share\a.txt"));
        Check("Run-box lookup: a protocol link, a web address or a network path is never a command",
              AppEntry.FromCommand("ms-msdt:/id") == null && AppEntry.FromCommand("https://example.com") == null && AppEntry.FromCommand(@"\\server\x.exe") == null);
        Check("Run-box lookup: Windows' own commands are still found", AppEntry.FromCommand("regedit")?.Path.StartsWith(
              Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) == true && AppEntry.FromCommand("services.msc") != null);

        // ---- memory (Phase 7) ----
        Check("memory: the managed heap has its 64 MB ceiling", GC.GetGCMemoryInfo().TotalAvailableMemoryBytes == 64L * 1024 * 1024);
        Check("memory: no tiered compilation (precompiled code is not compiled again)",
              AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool tiered) ? !tiered : AppContext.GetData("System.Runtime.TieredCompilation") is "false" or false);

        // ---- program tasks in jump lists (Phase 6) ----
        var sample = JumpTasks.Read(JumpTasks.Sample(Environment.ProcessPath!));
        Check("tasks: a named category and the tasks are read, separators kept",
              sample.Count == 4 && sample[0] is { Category: "Pinned sites", Title: "Open example", Arguments: "--site example" } &&
              sample[1] is { Category: "Tasks", Title: "New window", Arguments: "-new-window" } && sample[2].IsSeparator && sample[3].Title == "New private window");
        var cut = JumpTasks.Sample(Environment.ProcessPath!);
        Check("tasks: a file cut short gives what was whole before the cut, no more", JumpTasks.Read(cut[..(cut.Length / 2)]).Count < 4);
        Check("tasks: only programs from the program's own folder, or the folder above it",
              JumpTasks.SameHome(@"C:\Program Files\Mozilla Firefox\firefox.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe") &&
              JumpTasks.SameHome(@"C:\Users\a\AppData\Local\Discord\Update.exe", @"C:\Users\a\AppData\Local\Discord\app-1.0\Discord.exe") &&
              !JumpTasks.SameHome(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe") &&
              !JumpTasks.SameHome(@"C:\Program Files\Other\x.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe"));
        Check("tasks: not a jump-list file", JumpTasks.Read(new byte[] { 2, 0, 0, 0, 200, 0, 0, 0, 0, 0, 0, 0, 9, 9, 9, 9 }).Count == 0);
        int taskFiles = 0, tasksFound = 0;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\CustomDestinations"), "*.customDestinations-ms"))
        {
            taskFiles++;
            tasksFound += JumpTasks.Read(File.ReadAllBytes(f)).Count(t => !t.IsSeparator);
        }
        log.AppendLine($"info  program tasks on this PC: {tasksFound} in {taskFiles} files");

        // ---- Windows key (Phase 5 fix): the hook lives on its own thread, so a busy UI thread cannot get it dropped ----
        var winKey = new KeyHook();
        winKey.Install();
        Check("Windows key: hook accepted by Windows, on its own thread", winKey.Installed);
        winKey.Dispose();
        Check("Windows key: hook released on exit", !winKey.Installed);

        // ---- battery icon (Phase 5): only when Windows reports a system battery ----
        static Battery.Status Power(byte ac, byte flag, byte pct, byte saver = 0, int left = -1) =>
            new() { ACLineStatus = ac, BatteryFlag = flag, BatteryLifePercent = pct, SystemStatusFlag = saver, BatteryLifeTime = left, BatteryFullLifeTime = -1 };
        Check("battery: a desktop (no system battery) shows no icon", Battery.Describe(Power(1, 128, 255)) == null);
        Check("battery: Windows unable to tell shows no icon", Battery.Describe(Power(255, 255, 255)) == null);
        Check("battery: on battery, 57% with time left", Battery.Describe(Power(0, 0, 57, left: 2 * 3600 + 5 * 60)) is { Glyph: "", Tooltip: "2 hr 5 min (57%) remaining" });
        Check("battery: under an hour, minutes only", Battery.Describe(Power(0, 2, 30, left: 45 * 60))?.Tooltip == "45 min (30%) remaining");
        Check("battery: plugged in and charging", Battery.Describe(Power(1, 8, 57)) is { Glyph: "", Tooltip: "57% available (plugged in)" });
        Check("battery: plugged in and full", Battery.Describe(Power(1, 1, 100)) is { Glyph: "", Tooltip: "Fully charged (100%)" });
        Check("battery: battery saver on", Battery.Describe(Power(0, 0, 4, saver: 1))?.Glyph == "");
        Check("battery: charge unknown", Battery.Describe(Power(0, 1, 255))?.Glyph == "");
        var here = Battery.Read();
        log.AppendLine("info  this PC's battery: " + (here == null ? "none (no icon)" : here.Tooltip));

        // How many of File Explorer's frequent folders read back (a count only: the paths themselves stay private).
        var fe = JumpLists.Recent(new[] { "f01b4d95cf55d32a" }, 15);
        log.AppendLine($"info  File Explorer jump list: {fe.Count} entries readable, {fe.Count(e => e.Pinned)} pinned, {fe.Count(e => e.Name != null)} Windows folders by name" +
                       $" ({string.Join(", ", fe.Where(e => e.Name != null).Select(e => e.Name))}); pinned in order: {fe.Where(e => e.Pinned).Select(e => e.PinOrder).SequenceEqual(fe.Where(e => e.Pinned).Select(e => e.PinOrder).Order())}");
        log.AppendLine("info  this PC's Folders setting: " + StartPlaces.Signature() + " -> " +
                       string.Join(", ", StartPlaces.Read(_ => { }, () => { }, () => { }, () => { }).Select(p => p.Label)));

        log.AppendLine($"{failures} failure(s)");
        Directory.CreateDirectory(Identity.DataDir);
        File.WriteAllText(Identity.DataFile("selftest.txt"), log.ToString());
        return failures;
    }
}
#endif
