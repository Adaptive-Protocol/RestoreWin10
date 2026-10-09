using System.Runtime.InteropServices;

namespace RestoreWin10;

/// <summary>
/// The battery icon in the notification area, as on Windows 10. It is shown only while Windows reports a system battery
/// (a laptop or tablet; a UPS on USB can count too, just as it does for Windows' own icon), so a desktop never shows it.
/// Read once a second with the clock: GetSystemPowerStatus only reads what the power manager already knows.
/// </summary>
public static class Battery
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public byte ACLineStatus;        // 0 on battery, 1 plugged in, 255 unknown
        public byte BatteryFlag;         // 8 charging, 128 no system battery, 255 unknown
        public byte BatteryLifePercent;  // 0-100, 255 unknown
        public byte SystemStatusFlag;    // 1 battery saver on
        public int BatteryLifeTime;      // seconds left on battery, -1 unknown (or plugged in)
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out Status status);

    /// <summary>What the icon shows: a Segoe MDL2 glyph and the tooltip.</summary>
    public sealed record Reading(string Glyph, string Tooltip);

    /// <summary>The icon for this PC right now, or null when it has no battery.</summary>
    public static Reading? Read()
    {
#if DEV_BUILD
        if (Fake is { } fake) return Describe(fake);
#endif
        return GetSystemPowerStatus(out var s) ? Describe(s) : null;
    }

    // Segoe MDL2 Assets' "Mob" battery set, levels 0 to 10: the taller battery Windows 10's notification area shows
    // (checked by drawing them and comparing with Windows 10). Plain, plugged in, battery saver.
    static readonly string[] Plain = Run(0xEBA0), Charging = Run(0xEBAB), Saver = Run(0xEBB6);
    const string Unknown = "";
    static string[] Run(int first) => Enumerable.Range(first, 11).Select(c => ((char)c).ToString()).ToArray();

    /// <summary>Pure: the icon for a power status, or null when there is no system battery or Windows can't tell.</summary>
    public static Reading? Describe(Status s)
    {
        if (s.BatteryFlag is 128 or 255) return null;
        if (s.BatteryLifePercent > 100) return new Reading(Unknown, "Battery status unknown");
        int pct = s.BatteryLifePercent;
        int level = Math.Min(10, (pct + 5) / 10);
        bool plugged = s.ACLineStatus == 1, charging = (s.BatteryFlag & 8) != 0;
        if (plugged)
            return new Reading(Charging[level], pct >= 100 && !charging ? "Fully charged (100%)" : $"{pct}% available (plugged in)");
        string glyph = s.SystemStatusFlag == 1 ? Saver[level] : Plain[level];
        return new Reading(glyph, s.BatteryLifeTime > 0 ? $"{Remaining(s.BatteryLifeTime)} ({pct}%) remaining" : $"{pct}% remaining");
    }

    /// <summary>Windows 10's wording: "2 hr 5 min", or "45 min" under an hour.</summary>
    static string Remaining(int seconds)
    {
        int h = seconds / 3600, m = seconds / 60 % 60;
        return h > 0 ? $"{h} hr {m} min" : $"{m} min";
    }

#if DEV_BUILD
    /// <summary>
    /// Development builds only (for PCs without a battery): "--fake-battery=57" pretends to run on battery at 57%,
    /// "--fake-battery=57,ac" plugged in and charging, "--fake-battery=57,saver" on battery with battery saver on.
    /// Release builds have no way to override what Windows reports.
    /// </summary>
    public static Status? Fake;

    public static void FakeFrom(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--fake-battery=", StringComparison.Ordinal));
        if (arg == null) return;
        var parts = arg["--fake-battery=".Length..].Split(',');
        if (!byte.TryParse(parts[0], out var pct) || pct > 100) return;
        bool ac = parts.Contains("ac"), saver = parts.Contains("saver");
        Fake = new Status
        {
            ACLineStatus = (byte)(ac ? 1 : 0), BatteryFlag = (byte)(ac && pct < 100 ? 8 : 0), BatteryLifePercent = pct,
            SystemStatusFlag = (byte)(saver ? 1 : 0), BatteryLifeTime = ac ? -1 : pct * 150, BatteryFullLifeTime = -1,
        };
    }
#endif
}
