using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace RestoreWin10;

/// <summary>
/// RestoreWin10's own notification-area icon, registered with Shell_NotifyIcon directly so the process does not
/// have to load Windows Forms for one icon. Left click toggles the Start menu; right click shows a WPF menu.
/// </summary>
public sealed class AppTrayIcon : IDisposable
{
    const uint WM_TRAYICON = 0x0400 + 1, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B;
    const uint NIM_ADD = 0, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public int fIcon, xHotspot, yHotspot; public nint hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint h);
    [DllImport("user32.dll")] static extern nint CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, nint[]? large, out nint small, uint count);

    readonly HwndSource _sink;          // hidden window that receives the icon's mouse notifications
    readonly uint _taskbarCreated;
    readonly nint _hIcon;
    readonly ContextMenu _menu;
    readonly Action _onLeftClick;
    NOTIFYICONDATA _data;

    public AppTrayIcon(string tooltip, ContextMenu menu, Action onLeftClick)
    {
        _menu = menu;
        _onLeftClick = onLeftClick;
        _taskbarCreated = Win.RegisterWindowMessage("TaskbarCreated");
        _sink = new HwndSource(new HwndSourceParameters("RestoreWin10Tray")
        {
            Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP, not visible */, ExtendedWindowStyle = Win.WS_EX_TOOLWINDOW,
        });
        _sink.AddHook(WndProc);
        // The program's own icon (Windows 10 blue panes): visible on a light taskbar as well as a dark one.
        _hIcon = ExtractIconEx(Environment.ProcessPath ?? "", 0, null, out var small, 1) > 0 && small != 0
            ? small
            : StartGlyphIcon(Win.GetSystemMetrics(49 /* SM_CXSMICON */));
        _data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _sink.Handle, uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = WM_TRAYICON, hIcon = _hIcon,
            szTip = tooltip, szInfo = "", szInfoTitle = "",
        };
        Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            switch ((uint)lParam)
            {
                case WM_LBUTTONUP: _onLeftClick(); break;
                case WM_RBUTTONUP: case WM_CONTEXTMENU: ShowMenu(); break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated) Shell_NotifyIcon(NIM_ADD, ref _data);   // the tray was recreated: register again
        return 0;
    }

    void ShowMenu()
    {
        Win.SetForegroundWindow(_sink.Handle);   // otherwise the menu would not close when the user clicks elsewhere
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    /// <summary>Fallback when the program's icon cannot be read: four white squares - the Windows 10 Start glyph.</summary>
    static nint StartGlyphIcon(int size)
    {
        var px = new byte[size * size * 4];   // BGRA, top-down
        int gap = Math.Max(1, size / 8), sq = (size - gap) / 2 - 1, margin = (size - (2 * sq + gap)) / 2;
        void Square(int x0, int y0)
        {
            for (int y = y0; y < y0 + sq; y++)
                for (int x = x0; x < x0 + sq; x++) { int i = (y * size + x) * 4; px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 255; }
        }
        Square(margin, margin); Square(margin + sq + gap, margin);
        Square(margin, margin + sq + gap); Square(margin + sq + gap, margin + sq + gap);

        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var color = CreateDIBSection(0, ref bmi, 0 /* DIB_RGB_COLORS */, out var bits, 0, 0);
        Marshal.Copy(px, 0, bits, px.Length);
        var mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);   // all zero: the alpha channel decides
        var ii = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref ii);
        DeleteObject(color); DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        if (_hIcon != 0) DestroyIcon(_hIcon);
        _sink.Dispose();
    }
}
