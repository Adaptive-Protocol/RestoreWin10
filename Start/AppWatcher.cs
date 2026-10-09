using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RestoreWin10;

/// <summary>
/// Notices programs being installed or removed while RestoreWin10 runs:
/// <list type="bullet">
/// <item>shortcuts and folders appearing or going in the two Start Menu "Programs" trees;</item>
/// <item>Store apps registered or unregistered for this account (their registry key gains or loses a subkey).</item>
/// </list>
/// Installers write many files at once, so changes are gathered for two seconds before <see cref="Changed"/> is raised,
/// on a thread-pool thread. No polling: Windows wakes it.
/// </summary>
sealed class AppWatcher : IDisposable
{
    const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>(shortcuts changed, Store apps changed)</summary>
    public event Action<bool, bool>? Changed;

    readonly List<FileSystemWatcher> _files = new();
    readonly Timer _settle;
    readonly object _lock = new();
    bool _shortcuts, _store, _disposed;

    RegistryKey? _packages;
    AutoResetEvent? _packagesChanged;
    RegisteredWaitHandle? _packagesWait;

    public AppWatcher(IEnumerable<string> programRoots)
    {
        _settle = new Timer(_ => Fire());
        foreach (var root in programRoots.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 32 * 1024,   // a big installer can add dozens of shortcuts at once
                };
                w.Created += (_, e) => OnFile(e.FullPath);
                w.Deleted += (_, e) => OnFile(e.FullPath);
                w.Renamed += (_, e) => { OnFile(e.FullPath); OnFile(e.OldFullPath); };
                w.Error += (_, _) => Pulse(shortcuts: true, store: false);   // too many at once: rescan everything
                w.EnableRaisingEvents = true;
                _files.Add(w);
            }
            catch (Exception ex) { CrashLog.Write("watching " + root, ex); }
        }
        WatchPackages();
    }

    /// <summary>Shortcuts (.lnk) and folders matter; other files in the Start Menu (desktop.ini, .url) do not.</summary>
    void OnFile(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !Path.HasExtension(path))
            Pulse(shortcuts: true, store: false);
    }

    void WatchPackages()
    {
        try
        {
            _packages = Registry.CurrentUser.OpenSubKey(PackagesKey);
            if (_packages == null) return;
            _packagesChanged = new AutoResetEvent(false);
            if (!Arm()) return;
            _packagesWait = ThreadPool.RegisterWaitForSingleObject(_packagesChanged, (_, _) =>
            {
                Pulse(shortcuts: false, store: true);
                Arm();   // a notification fires once; ask for the next one
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) { CrashLog.Write("watching Store apps", ex); }
    }

    bool Arm()
    {
        lock (_lock)
        {
            if (_disposed || _packages == null || _packagesChanged == null) return false;
            return RegNotifyChangeKeyValue(_packages.Handle, false, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_THREAD_AGNOSTIC,
                                           _packagesChanged.SafeWaitHandle, true) == 0;
        }
    }

    void Pulse(bool shortcuts, bool store)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _shortcuts |= shortcuts;
            _store |= store;
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);   // restart the wait: fire two seconds after the last change
        }
    }

    void Fire()
    {
        bool shortcuts, store;
        lock (_lock)
        {
            if (_disposed) return;
            (shortcuts, store) = (_shortcuts, _store);
            _shortcuts = _store = false;
        }
        if (shortcuts || store) Changed?.Invoke(shortcuts, store);
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        foreach (var w in _files) w.Dispose();
        _settle.Dispose();
        _packagesWait?.Unregister(null);
        _packages?.Dispose();
        _packagesChanged?.Dispose();
    }

    const uint REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;
    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, uint filter, SafeWaitHandle evt, bool asynchronous);
}
