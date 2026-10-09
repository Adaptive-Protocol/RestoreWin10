using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RestoreWin10;

/// <summary>What is pinned to the Windows taskbar, read the way Explorer stores it.</summary>
public static class WindowsPins
{
    /// <summary>One pinned program: the executable its windows run as, the shortcut to launch it with, its name.</summary>
    public record Pin(string Exe, string Shortcut, string Name);

    /// <summary>
    /// Explorer keeps taskbar pins as shortcuts in a per-user folder and their left-to-right order in a binary
    /// registry value that embeds each shortcut's file name; ranking the files by where their name first appears
    /// reproduces the order. Pins of Store apps are not shortcut files and cannot be carried over.
    /// </summary>
    public static List<Pin> Read()
    {
        var result = new List<Pin>();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        if (!Directory.Exists(dir)) return result;

        byte[] order = Array.Empty<byte>();
        try { order = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband", "Favorites", null) as byte[] ?? order; }
        catch { }
        int Rank(string file)
        {
            int i = IndexOf(order, Encoding.Unicode.GetBytes(Path.GetFileName(file)));
            if (i < 0) i = IndexOf(order, Encoding.Unicode.GetBytes(Path.GetFileNameWithoutExtension(file)));
            return i < 0 ? int.MaxValue : i;
        }

        var links = Directory.GetFiles(dir, "*.lnk").OrderBy(Rank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var t = new Thread(() =>
        {
            foreach (var lnk in links)
            {
                try
                {
                    if (Shortcut.Read(lnk) is not { } sc) continue;
                    var icon = sc.IconFile.Length > 0 ? $"{sc.IconFile},{sc.IconIndex}" : "";
                    var exe = ResolveExe(Path.GetFileNameWithoutExtension(lnk), sc.Target, sc.Arguments, icon);
                    if (exe != null && seen.Add(exe)) result.Add(new Pin(exe, lnk, Path.GetFileNameWithoutExtension(lnk)));
                }
                catch { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);   // the shell's shortcut object is COM
        t.Start(); t.Join();
        return result;
    }

    /// <summary>The executable a pinned shortcut's windows will actually run as, or null if it cannot be worked out.</summary>
    static string? ResolveExe(string name, string target, string args, string icon)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        target = Environment.ExpandEnvironmentVariables(target);

        // Shell-folder pins have no target; the two Windows ships are known.
        if (target.Length == 0)
        {
            if (name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, "explorer.exe");
            if (name.Equals("Control Panel", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, "System32", "control.exe");
            return null;
        }

        // Squirrel-style launchers ("Update.exe --processStart App.exe"): the windows belong to App.exe, somewhere below.
        var m = Regex.Match(args, @"--processStart\s+""?([^""\s]+\.exe)", RegexOptions.IgnoreCase);
        if (m.Success && Directory.Exists(Path.GetDirectoryName(target) ?? ""))
        {
            try
            {
                var real = Directory.EnumerateFiles(Path.GetDirectoryName(target)!, m.Groups[1].Value, SearchOption.AllDirectories)
                                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                if (real != null) return real;
            }
            catch { }
        }

        // MSI "advertised" shortcuts point at an icon stub under Windows\Installer; the icon usually names the real program.
        if (target.StartsWith(Path.Combine(windows, "Installer"), StringComparison.OrdinalIgnoreCase))
        {
            int c = icon.LastIndexOf(',');
            var iconFile = Environment.ExpandEnvironmentVariables(c > 0 ? icon[..c] : icon);
            if (iconFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconFile) &&
                !iconFile.StartsWith(Path.Combine(windows, "Installer"), StringComparison.OrdinalIgnoreCase)) return iconFile;
        }

        return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target) ? target : null;
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        if (needle.Length == 0 || hay.Length < needle.Length) return -1;
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
