using System.Runtime.InteropServices;
using System.Threading;

namespace RestoreWin10;

/// <summary>
/// Low-level keyboard hook. A Windows-key tap (no other key pressed meanwhile) raises
/// WinKeyTapped and is neutralised so the built-in Start menu does not also open.
/// Win+X, Win+E etc. pass through untouched. Only the Windows keys' up/down state is kept; no other key is recorded.
/// <para>
/// The hook lives on a thread of its own that does nothing else. Windows silently removes a low-level hook that once
/// answers too slowly (Windows 7 and later; the program is not told), and on the UI thread any busy moment (Start
/// opening for the first time, bars rebuilt after a screen change) could cost the Windows key for the rest of the
/// session. So the callback only notes the tap and hands it on without waiting (WinKeyTapped is raised on the hook
/// thread: subscribers must not block it), and the hook is put back every minute in case Windows dropped it anyway.
/// </para>
/// </summary>
public sealed class KeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int WM_TIMER = 0x113, WM_QUIT = 0x12;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const uint LLKHF_INJECTED = 0x10;
    const uint RehookMs = 60_000;

    delegate nint HookProc(int code, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y, priv; }

    [DllImport("user32", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc fn, nint mod, uint tid);
    [DllImport("user32")] static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32")] static extern nint CallNextHookEx(nint h, int code, nint w, nint l);
    [DllImport("user32")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32")] static extern int GetMessage(out MSG m, nint hwnd, uint min, uint max);
    [DllImport("user32")] static extern nint SetTimer(nint hwnd, nint id, uint ms, nint fn);
    [DllImport("user32")] static extern bool PostThreadMessage(uint tid, uint msg, nint w, nint l);
    [DllImport("kernel32")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32")] static extern nint GetModuleHandle(string? name);

    /// <summary>A Windows-key tap. Raised on the hook's thread: hand the work to the UI thread without waiting.</summary>
    public event Action? WinKeyTapped;
    nint _hook;
    HookProc? _proc; // keep alive
    bool _winDown, _alone, _failed;
    uint _threadId;
    Thread? _thread;

    /// <summary>Windows accepted the hook (for the self-test).</summary>
    internal bool Installed => _hook != 0 && _thread?.IsAlive == true;

    public void Install()
    {
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            Hook();
            ready.Set();
            SetTimer(0, 0, RehookMs, 0);
            // The hook is called from inside GetMessage; the thread's own timer puts it back now and then.
            while (GetMessage(out var m, 0, 0, 0) > 0)
                if (m.message == WM_TIMER) { Unhook(); Hook(); }
            Unhook();
        }) { IsBackground = true, Name = "Windows key hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
    }

    void Hook()
    {
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, GetModuleHandle(null), 0);
        if (_hook == 0 && !_failed) CrashLog.Note($"The Windows key could not be watched (SetWindowsHookEx error {Marshal.GetLastWin32Error()}); retrying every minute.");
        else if (_hook != 0 && _failed) CrashLog.Note("The Windows key is watched again.");
        _failed = _hook == 0;
    }

    void Unhook() { if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; } }

    nint Callback(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                bool down = w == WM_KEYDOWN || w == WM_SYSKEYDOWN;
                bool up = w == WM_KEYUP || w == WM_SYSKEYUP;
                bool isWin = k.vkCode == VK_LWIN || k.vkCode == VK_RWIN;
                if (isWin && down) { if (!_winDown) _alone = true; _winDown = true; }
                else if (!isWin && down && _winDown) _alone = false;
                else if (isWin && up)
                {
                    bool tap = _winDown && _alone;
                    _winDown = false; _alone = false;
                    if (tap)
                    {
                        // Inject an unassigned key so Explorer sees "Win + other" and skips its Start menu.
                        var inp = new INPUT[2];
                        inp[0].type = inp[1].type = 1;
                        inp[0].ki.vk = inp[1].ki.vk = 0xE8;
                        inp[1].ki.flags = 2;
                        SendInput(2, inp, Marshal.SizeOf<INPUT>());
                        WinKeyTapped?.Invoke();
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    public void Dispose()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, 0, 0);   // the thread unhooks on its way out
        _thread.Join(2000);
        _thread = null;
    }
}
