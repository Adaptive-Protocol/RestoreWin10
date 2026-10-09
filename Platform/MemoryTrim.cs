using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RestoreWin10;

/// <summary>
/// Gives memory back once the user is done with something (the Start menu closed, startup finished):
/// a full garbage collection, then the working set is trimmed so pages the shell no longer touches are
/// released to the standby list. They come back on demand, so the visible cost is a few milliseconds the
/// next time the menu opens; the benefit is that an idle shell stays small.
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);

    static DispatcherTimer? _timer;

    /// <summary>Trims after <paramref name="delayMs"/> of quiet; a later call restarts the wait.</summary>
    public static void Schedule(int delayMs = 1500)
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.ApplicationIdle) { };
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _timer.Tick -= OnTick; _timer.Tick += OnTick;
        _timer.Start();
    }

    static void OnTick(object? s, EventArgs e)
    {
        _timer!.Stop();
        Now();
    }

    public static void Now()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        s_afterTrim = PrivateBytes();
    }

    // ---------- while idle ----------

    static DispatcherTimer? s_idle;
    static long s_afterTrim;
    const long GrowthBeforeIdleTrim = 8 * 1024 * 1024;

    static long PrivateBytes() { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.PrivateMemorySize64; }

    /// <summary>
    /// Every 5 minutes: when nothing is open (<paramref name="busy"/> is false) and the shell has grown by 8 MB or more
    /// since its last trim, trims again. Covers what does not end with Start closing: taskbar menus, jump lists, the
    /// notification-area flyout, programs' icons coming and going over hours.
    /// </summary>
    public static void StartIdle(Func<bool> busy)
    {
        if (s_idle != null) return;
        s_afterTrim = PrivateBytes();
        s_idle = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMinutes(5) };
        s_idle.Tick += (_, _) => { if (!busy() && PrivateBytes() - s_afterTrim >= GrowthBeforeIdleTrim) Now(); };
        s_idle.Start();
    }
}
