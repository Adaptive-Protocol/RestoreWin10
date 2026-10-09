#if DEV_BUILD
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace RestoreWin10;

/// <summary>
/// Development builds only (Phase 7): "--memprobe" measures where memory goes, step by step, without showing anything
/// on screen. Start is opened off-screen and without taking focus; no taskbar is created. Each step ends with the same
/// clean-up the shell does after Start closes (full collection, working set trimmed), so the numbers are what stays.
/// "--software" switches WPF to software rendering first, for comparison. Results go to memprobe-{variant}.txt in the
/// development copy's folder; the process then exits.
/// </summary>
static class MemProbe
{
    public static async void Run(StartMenu menu, Task preload, string variant)
    {
        var log = new StringBuilder($"memory probe ({variant}), {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n");
        try
        {
            await preload;
            await Task.Delay(4500);   // past the startup clean-up
            log.AppendLine(Snapshot("apps loaded, Start never shown"));
            await menu.ProbeShowAsync();
            await Settle();
            log.AppendLine(Snapshot("Start shown once"));
            for (int i = 0; i < 5; i++) await menu.ProbeShowAsync();
            await Settle();
            log.AppendLine(Snapshot("Start shown 6 times"));
            await menu.ProbeSearchAsync("e");
            await Settle();
            log.AppendLine(Snapshot("after a search"));

            // The taskbars, as the shell makes them (main + one per other screen), off-screen and never docked.
            Taskbar.Offscreen = true;
            var bars = new List<Taskbar> { new(menu, replace: false) };
            foreach (var s in Screens.All().Where(s => !s.Primary)) bars.Add(new Taskbar(menu, false, s.Device));
            foreach (var b in bars) b.Show();
            await Task.Delay(4000);
            await Settle();
            log.AppendLine(Snapshot($"{bars.Count} taskbars added"));
            await Task.Delay(60000);
            log.AppendLine(Snapshot("taskbars after 60 s, as is"));   // no clean-up: what the shell holds between trims
            await Settle();
            log.AppendLine(Snapshot("  ...after clean-up"));

            // Hours of taskbar refreshes in a few seconds: short-lived objects, no forced collection. What stays committed
            // afterwards is the GC's allocation budget for new objects (gen0), not anything still in use.
            var keep = new object[64];
            for (int i = 0; i < 400_000; i++) keep[i & 63] = new byte[512];
            await Task.Delay(1000);
            log.AppendLine(Snapshot("after 200 MB of short-lived objects"));
            foreach (var b in bars) b.Close();
        }
        catch (Exception ex) { log.AppendLine("failed: " + ex.Message); }
        File.WriteAllText(Identity.DataFile($"memprobe-{variant}.txt"), log.ToString());
        Application.Current.Shutdown();
    }

    static async Task Settle()
    {
        await Task.Delay(500);
        MemoryTrim.Now();
        await Task.Delay(1500);
    }

    static string Snapshot(string label)
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        using var p = Process.GetCurrentProcess();
        double mb(long v) => v / 1048576.0;
        return string.Format(CultureInfo.InvariantCulture,
            "{0,-32} private {1,6:F1} MB | working set {2,6:F1} MB | GC heap {3,5:F1} MB, committed {4,5:F1} MB | everything else {5,5:F1} MB",
            label, mb(p.PrivateMemorySize64), mb(p.WorkingSet64), mb(GC.GetTotalMemory(false)), mb(info.TotalCommittedBytes),
            mb(p.PrivateMemorySize64 - info.TotalCommittedBytes));
    }
}
#endif
