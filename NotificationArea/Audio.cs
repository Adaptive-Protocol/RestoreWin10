using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RestoreWin10;

// ---------- property-store interop shared by audio, jump lists and shortcuts ----------

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public uint pid; }
[StructLayout(LayoutKind.Sequential)] public struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public nint p; nint p2; }

public static class PropStore
{
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, out IPropertyStore ps);

    public static PROPERTYKEY AppUserModelId = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    public static PROPERTYKEY DeviceFriendlyName = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    /// <summary>Reads a string property, or null.</summary>
    public static string? GetString(IPropertyStore store, PROPERTYKEY key)
    {
        try
        {
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
    }

    /// <summary>Reads a yes/no property (VT_BOOL); false when absent.</summary>
    public static bool GetBool(IPropertyStore store, PROPERTYKEY key)
    {
        try
        {
            if (store.GetValue(ref key, out var pv) != 0) return false;
            try { return pv.vt == 11 /* VT_BOOL */ && (short)(pv.p & 0xFFFF) != 0; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return false; }
    }

    /// <summary>The AppUserModelID a window was given (Store apps, browsers, Office...), or null.</summary>
    public static string? WindowAumid(nint hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var ps) != 0) return null;
            try { return GetString(ps, AppUserModelId); } finally { Marshal.ReleaseComObject(ps); }
        }
        catch { return null; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    /// <summary>The AppUserModelID stored in a shortcut, or null.</summary>
    public static string? ShortcutAumid(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            return GetString((IPropertyStore)link, AppUserModelId);
        }
        catch { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }
}

// ---------- master volume of the default playback device ----------

/// <summary>Core Audio access to the default playback device's master volume and mute.</summary>
public static class Audio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    // Windows' own interface for choosing the default device: undocumented, but what Windows 10's volume flyout, the
    // Sound control panel and tools such as SoundSwitch use, unchanged since Windows 7. Only SetDefaultEndpoint is
    // called; the slots before it only keep the method order. A missing or changed interface is caught and the user
    // is sent to Settings > Sound instead.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }

    /// <summary>A playback device Windows reports as active: its endpoint id and the name Windows shows.</summary>
    public record Device(string Id, string Name);

    const int eRender = 0, eConsole = 0, eMultimedia = 1, eCommunications = 2, DEVICE_STATE_ACTIVE = 1;

    /// <summary>The active playback devices, and which of them is the default; an empty list when Windows reports none.</summary>
    public static (List<Device> Devices, string? DefaultId) Devices()
    {
        var list = new List<Device>();
        string? current = null;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                if (en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var col) == 0)
                    try
                    {
                        col.GetCount(out uint n);
                        for (uint i = 0; i < Math.Min(n, 32u); i++)   // a sane cap: nobody has more
                        {
                            if (col.Item(i, out var dev) != 0) continue;
                            try
                            {
                                if (dev.GetId(out var id) != 0 || string.IsNullOrEmpty(id)) continue;
                                string name = "";
                                if (dev.OpenPropertyStore(0, out var ps) == 0)
                                    try { name = PropStore.GetString(ps, PropStore.DeviceFriendlyName) ?? ""; } finally { Marshal.ReleaseComObject(ps); }
                                list.Add(new Device(id, name.Length > 0 ? name : "Speakers"));
                            }
                            finally { Marshal.ReleaseComObject(dev); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(col); }
                if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var def) == 0)
                    try { if (def.GetId(out var id) == 0) current = id; } finally { Marshal.ReleaseComObject(def); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }
        catch { }
        return (list, current);
    }

    /// <summary>
    /// Makes <paramref name="id"/> the default playback device for sound, media and calls, as Windows 10's flyout does.
    /// The id must be one of the active devices Windows lists right now (nothing else is ever passed on). False when
    /// it is not, or when Windows refused or the interface is unavailable.
    /// </summary>
    public static bool SetDefault(string id)
    {
        if (!Devices().Devices.Any(d => d.Id == id)) return false;
        object? pc = null;
        try
        {
            pc = new PolicyConfigClient();
            var policy = (IPolicyConfig)pc;
            bool ok = true;
            foreach (var role in new[] { eConsole, eMultimedia, eCommunications })
                ok &= policy.SetDefaultEndpoint(id, role) == 0;
            Release();   // the volume connection follows the new default at once
            return ok;
        }
        catch (Exception ex) { CrashLog.Note($"could not change the default playback device ({ex.GetType().Name})"); return false; }
        finally { if (pc != null) Marshal.ReleaseComObject(pc); }
    }

    /// <summary>Whether Windows' default-device interface can be created here (for the self-test; changes nothing).</summary>
    internal static bool CanSetDefault()
    {
        object? pc = null;
        try { pc = new PolicyConfigClient(); return pc is IPolicyConfig; }
        catch { return false; }
        finally { if (pc != null) Marshal.ReleaseComObject(pc); }
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    public record Status(float Level, bool Mute, string Device);

    static IAudioEndpointVolume? _volume;
    static string _device = "";
    static DateTime _opened = DateTime.MinValue;

    /// <summary>(Re)connects to the current default device; the connection is renewed regularly so device changes are picked up.</summary>
    static IAudioEndpointVolume? Endpoint()
    {
        if (_volume != null && (DateTime.Now - _opened).TotalSeconds < 10) return _volume;
        Release();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev) != 0) return null;
                try
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, 0, out var o) != 0) return null;
                    _volume = (IAudioEndpointVolume)o;
                    _device = "";
                    if (dev.OpenPropertyStore(0, out var ps) == 0)
                    {
                        try { _device = PropStore.GetString(ps, PropStore.DeviceFriendlyName) ?? ""; } finally { Marshal.ReleaseComObject(ps); }
                    }
                    _opened = DateTime.Now;
                    return _volume;
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }
        catch { return null; }
    }

    static void Release()
    {
        if (_volume != null) { try { Marshal.ReleaseComObject(_volume); } catch { } _volume = null; }
    }

    /// <summary>Current level (0..1), mute state and device name; null when there is no playback device.</summary>
    public static Status? Get()
    {
        try
        {
            var v = Endpoint();
            if (v == null) return null;
            if (v.GetMasterVolumeLevelScalar(out var level) != 0 || v.GetMute(out var mute) != 0) { Release(); return null; }
            return new Status(level, mute, _device);
        }
        catch { Release(); return null; }
    }

    public static void SetLevel(float level)
    {
        try { var ctx = Guid.Empty; Endpoint()?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx); } catch { Release(); }
    }

    public static void SetMute(bool mute)
    {
        try { var ctx = Guid.Empty; Endpoint()?.SetMute(mute, ref ctx); } catch { Release(); }
    }

    /// <summary>The Segoe MDL2 glyph Windows 10 uses for this state: muted, or one of four levels.</summary>
    public static string Glyph(Status? s) =>
        s == null || s.Mute ? "" : s.Level <= 0.005f ? "" : s.Level <= 0.34f ? "" : s.Level <= 0.67f ? "" : "";
}
