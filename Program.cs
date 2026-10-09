namespace RestoreWin10;

/// <summary>
/// The entry point. Installing, uninstalling and recovering the Windows taskbar (Setup.cs) happen here, before WPF or any
/// window exists; every other start runs the shell (App).
/// </summary>
static class Program
{
    static readonly string[] SetupSwitches = { "--install", "--uninstall", "--install-elevated", "--uninstall-elevated", "--restore-taskbar" };

    [STAThread]
    static int Main(string[] args)
    {
        bool setup = args.Any(a => SetupSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));
        // An installer may be started from a folder this account can write to (Downloads, the build folder), so it takes
        // DLLs from System32 only. The installed shell also trusts its own folder in Program Files.
        Hardening.Apply(trustOwnFolder: !setup);
        if (setup) return Setup.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
