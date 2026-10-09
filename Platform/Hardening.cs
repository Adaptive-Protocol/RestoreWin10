using System.Runtime.InteropServices;

namespace RestoreWin10;

/// <summary>Process-wide safety defaults, applied first thing in Main (threat model F-01, F-04).</summary>
static class Hardening
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
    const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;
    const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;   // the application's folder and System32

    /// <param name="trustOwnFolder">
    /// True for the installed shell, whose folder only administrators can change. False for setup, which may run from a
    /// folder anyone signed in as this account can write to.
    /// </param>
    public static void Apply(bool trustOwnFolder)
    {
        // DLLs loaded by name never come from the current folder or the PATH.
        SetDefaultDllDirectories(trustOwnFolder ? LOAD_LIBRARY_SEARCH_DEFAULT_DIRS : LOAD_LIBRARY_SEARCH_SYSTEM32);
        // A current folder the user cannot write to, so anything that looks there first finds nothing planted.
        Environment.CurrentDirectory = Environment.SystemDirectory;
    }
}
