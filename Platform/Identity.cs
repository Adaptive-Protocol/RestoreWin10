using System.IO;

namespace RestoreWin10;

/// <summary>
/// Which RestoreWin10 this is. Release builds are the real thing. Debug builds (DEV_BUILD) are development copies that
/// can run next to the installed RestoreWin10 without touching it: their own lock and signal names, their own settings
/// folder, preview mode only, no Windows-key hook, and no installing.
/// </summary>
static class Identity
{
    /// <summary>The name people see.</summary>
    public const string Product = "RestoreWin10";

#if DEV_BUILD
    public static readonly bool Dev = true;
    /// <summary>Prefix of the lock and signal names, and the settings folder's name.</summary>
    public const string Name = "RestoreWin10.Dev";
#else
    public static readonly bool Dev = false;
    public const string Name = "RestoreWin10";
#endif

    /// <summary>%APPDATA%\RestoreWin10: tiles, pins, settings and logs.</summary>
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string DataFile(string name) => Path.Combine(DataDir, name);
}
