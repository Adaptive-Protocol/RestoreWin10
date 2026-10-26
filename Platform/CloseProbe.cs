#if DEV_BUILD
using System.IO;
using System.Text;

namespace RestoreWin10;

/// <summary>
/// Development only (--closeprobe[=elevated]): closes windows with the taskbar's own Close (<see cref="Win.CloseWindow"/>,
/// what the preview's X and "Close window" use) in every window state and checks each one closes. Without a value it
/// closes test windows whose titles start with "RestoreWin10 close test" (opened beforehand by a test script); with
/// "elevated" it waits for Task Manager and Registry Editor windows opened by the user, puts each one in the next state
/// itself and closes it. Writes closeprobe.txt next to the self-test's file.
/// </summary>
static class CloseProbe
{
    const uint SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
    static readonly string[] States = { "normal", "maximised", "minimised from normal", "minimised from maximised" };

    public static int Run(string mode)
    {
        var log = new StringBuilder();
        void Line(string s) { log.AppendLine($"{DateTime.Now:HH:mm:ss} {s}"); Flush(log); }

        var parts = mode.Split(':');   // "elevated", or "elevated:1,3" for only those states
        if (parts[0] != "elevated")
        {
            var targets = TaskTracker.OpenWindows().Where(h => Win.Text(h).StartsWith("RestoreWin10 close test", StringComparison.Ordinal)).ToList();
            Line($"test windows: {targets.Count}");
            foreach (var h in targets) Line(Close(h, Win.Text(h)));
            return 0;
        }

        // States per program: "1,3" for both, or "taskmgr.exe=3;regedit.exe=1,3".
        var all = Enumerable.Range(0, States.Length).ToArray();
        var wantedBy = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase) { ["taskmgr.exe"] = all, ["regedit.exe"] = all };
        if (parts.Length > 1)
            foreach (var spec in parts[1].Split(';'))
            {
                var kv = spec.Split('=');
                var list = kv[^1].Split(',').Select(int.Parse).ToArray();
                if (kv.Length == 2) wantedBy[kv[0]] = list; else foreach (var k in wantedBy.Keys.ToList()) wantedBy[k] = list;
            }
        var done = wantedBy.Keys.ToDictionary(a => a, _ => 0, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<nint>();
        Line("waiting for: " + string.Join("; ", wantedBy.Select(w => $"{w.Key} {string.Join(", ", w.Value.Select(n => States[n]))}")));
        var deadline = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < deadline && done.Any(d => d.Value < wantedBy[d.Key].Length))
        {
            foreach (var h in TaskTracker.OpenWindows())
            {
                if (seen.Contains(h)) continue;
                Win.GetWindowThreadProcessId(h, out uint pid);
                var app = Path.GetFileName(Win.ExePath(pid) ?? "");
                if (!done.TryGetValue(app, out int i) || i >= wantedBy[app].Length) continue;
                seen.Add(h);
                int n = wantedBy[app][i];
                Thread.Sleep(1500);   // let the program finish opening
                // Windows lets another program minimise, restore and close an administrator's window (what a taskbar
                // needs), but not maximise it: those states are left to the user, and the probe waits for them.
                if (States[n].Contains("maximised"))
                {
                    Line($"{app}: waiting up to 2 minutes for the user to make it {States[n]}");
                    var until = DateTime.Now.AddMinutes(2);
                    while (Win.IsWindow(h) && State(h) != States[n] && DateTime.Now < until) Thread.Sleep(250);
                    Thread.Sleep(500);
                }
                else Line($"{app}: wanted {States[n]}; set: {SetState(h, n)}");
                // Gone before the probe acted (closed by the user, or replaced by the program): not a result.
                if (!Win.IsWindow(h)) { Line($"  {app}: the window went away before the probe closed it; waiting for the next one"); continue; }
                if (State(h) != States[n]) { Line($"  {app}: not {States[n]} after 2 minutes ({State(h)}); waiting for the next one"); continue; }
                Line(Close(h, app));
                done[app] = i + 1;
            }
            Thread.Sleep(250);
        }
        Line(done.All(d => d.Value >= wantedBy[d.Key].Length) ? "all states done" : "stopped waiting: " + string.Join(", ", done.Select(d => $"{d.Key} {d.Value}/{wantedBy[d.Key].Length}")));
        return 0;
    }

    /// <summary>Closes <paramref name="h"/> as the taskbar does and reports its state before and whether it closed.</summary>
    static string Close(nint h, string what)
    {
        string before = State(h), title = Win.Text(h).Replace(" (Not Responding)", "");
        Win.GetWindowThreadProcessId(h, out uint pid);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Win.CloseWindow(h);
        while (Win.IsWindow(h) && sw.ElapsedMilliseconds < 8000) Thread.Sleep(50);
        if (!Win.IsWindow(h)) return $"  PASS {what} [{before}]: closed in {sw.ElapsedMilliseconds} ms";
        // A program that does not respond cannot close itself: Windows asks the user whether to end it instead.
        if (before.EndsWith("not responding") && WindowsAsks(title, pid)) return $"  PASS {what} [{before}]: Windows asks whether to end it";
        return $"  FAIL {what} [{before}]: still open 8 s after Close";
    }

    /// <summary>Whether Windows shows its "end the program" question for a frozen window: another process's window naming it.</summary>
    static bool WindowsAsks(string title, uint pid)
    {
        bool found = false;
        Win.EnumWindows((w, _) =>
        {
            Win.GetWindowThreadProcessId(w, out uint p);
            if (p != pid && Win.IsWindowVisible(w) && Win.Text(w) is { Length: > 0 } t && t != title && t.Contains(title)) { found = true; return false; }
            return true;
        }, 0);
        return found;
    }

    /// <summary>Puts another program's window into state <paramref name="n"/> with system commands, which Windows passes to programs running as administrator.</summary>
    static string SetState(nint h, int n)
    {
        void Cmd(uint sc) { Win.PostMessage(h, Win.WM_SYSCOMMAND, (nint)sc, 0); Thread.Sleep(700); }
        if (Win.IsIconic(h) || Win.IsZoomed(h)) Cmd(SC_RESTORE);
        if (n is 1 or 3) Cmd(SC_MAXIMIZE);
        if (n is 2 or 3) Cmd(SC_MINIMIZE);
        return State(h);
    }

    static string State(nint h)
    {
        var p = new Win.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Win.WINDOWPLACEMENT>() };
        Win.GetWindowPlacement(h, ref p);
        bool min = Win.IsIconic(h), max = Win.IsZoomed(h), toMax = (p.flags & 2) != 0;   // WPF_RESTORETOMAXIMIZED
        string state = min ? (toMax ? "minimised from maximised" : "minimised from normal") : max ? "maximised" : "normal";
        return Win.IsHungAppWindow(h) ? state + ", not responding" : state;
    }

    static void Flush(StringBuilder log)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RestoreWin10.Dev");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "closeprobe.txt"), log.ToString());
    }
}
#endif
