using System.Runtime.InteropServices;

namespace Anchor;

/// <summary>
/// An invisible top-level window that hears what Windows broadcasts: display changes, sleep and wake,
/// lock and unlock, and the screen turning off and on.
/// (It has to be top-level, not message-only, to receive broadcasts like WM_DISPLAYCHANGE.)
/// </summary>
sealed class SystemEvents : NativeWindow, IDisposable
{
    const int WM_DISPLAYCHANGE = 0x007E, WM_POWERBROADCAST = 0x0218, WM_WTSSESSION_CHANGE = 0x02B1;
    const int PBT_APMSUSPEND = 0x4, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12, PBT_POWERSETTINGCHANGE = 0x8013;
    const int WTS_CONSOLE_CONNECT = 0x1, WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
    static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [StructLayout(LayoutKind.Sequential)]
    struct POWERBROADCAST_SETTING { public Guid PowerSetting; public int DataLength; public int Data; }

    [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(nint hwnd, int flags);
    [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(nint hwnd);
    [DllImport("user32.dll")] static extern nint RegisterPowerSettingNotification(nint hwnd, ref Guid guid, int flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(nint handle);

    readonly nint _powerReg;

    public event Action<string>? DisplayChanged;
    public event Action? Sleeping, Woke, Locked, Unlocked, ScreenOff, ScreenOn;

    public SystemEvents()
    {
        CreateHandle(new CreateParams { Caption = "Anchor events", Style = 0, ExStyle = 0x80 /* tool window: no taskbar button */ });
        WTSRegisterSessionNotification(Handle, 0 /* this session */);
        var g = GUID_CONSOLE_DISPLAY_STATE;
        _powerReg = RegisterPowerSettingNotification(Handle, ref g, 0 /* window handle */);
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            switch (m.Msg)
            {
                case WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke($"display changed to {(int)(m.LParam & 0xFFFF)}x{(int)((m.LParam >> 16) & 0xFFFF)}");
                    break;

                case WM_POWERBROADCAST:
                    switch ((int)m.WParam)
                    {
                        case PBT_APMSUSPEND: Sleeping?.Invoke(); break;
                        case PBT_APMRESUMESUSPEND:
                        case PBT_APMRESUMEAUTOMATIC: Woke?.Invoke(); break;
                        case PBT_POWERSETTINGCHANGE:
                            var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                            if (s.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                            {
                                if (s.Data == 0) ScreenOff?.Invoke();
                                else if (s.Data == 1) ScreenOn?.Invoke(); // 2 = dimmed: ignore
                            }
                            break;
                    }
                    m.Result = 1;
                    return;

                case WM_WTSSESSION_CHANGE:
                    switch ((int)m.WParam)
                    {
                        case WTS_SESSION_LOCK: Locked?.Invoke(); break;
                        case WTS_SESSION_UNLOCK: Unlocked?.Invoke(); break;
                        case WTS_CONSOLE_CONNECT: DisplayChanged?.Invoke("console reconnected"); break;
                    }
                    break;
            }
        }
        catch (Exception ex) { Anchor.Core.Log.Write("Event handling failed: " + ex.Message); }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            WTSUnRegisterSessionNotification(Handle);
            if (_powerReg != 0) UnregisterPowerSettingNotification(_powerReg);
            DestroyHandle();
        }
    }
}
