namespace RestoreWin10;

/// <summary>
/// The entry point. Installing, uninstalling and recovering the Windows taskbar (Setup.cs) happen here, before WPF or any
/// window exists; every other start runs the shell (App).
/// </summary>
static class Program
{
    static readonly string[] SetupSwitches = { "--install", "--uninstall", "--install-elevated", "--uninstall-elevated", "--restore-taskbar" };

    /// <summary>
    /// A downloaded RestoreWin10.exe opened without arguments (a double-click) opens the install window. Not the installed
    /// copy (opened plainly, that starts RestoreWin10), and not a development build (those never install; they run in
    /// preview). Preview of any copy: <c>--preview</c>.
    /// </summary>
    internal static bool IsInstallerLaunch(bool dev, string[] args, bool installedCopy) => !dev && args.Length == 0 && !installedCopy;

    [STAThread]
    static int Main(string[] args)
    {
        bool wizard = IsInstallerLaunch(Identity.Dev, args, Setup.IsInstalledCopy);
        bool setup = wizard || args.Any(a => SetupSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));
        // An installer may be started from a folder this account can write to (Downloads, the build folder), so it takes
        // DLLs from System32 only. The installed shell also trusts its own folder in Program Files.
        Hardening.Apply(trustOwnFolder: !setup);
        if (wizard) return Setup.Wizard();
        if (setup) return Setup.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
