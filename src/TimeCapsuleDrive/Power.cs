using System.Runtime.InteropServices;

namespace TimeCapsuleDrive;

/// <summary>
/// Tells the program when the computer is about to sleep and when it has woken up.
/// Uses the callback form of the Windows power notifications, which needs no window and therefore also works
/// in a background process started by the task scheduler.
/// </summary>
static class Power
{
    const int DeviceNotifyCallback = 2;
    const uint PbtApmSuspend = 4, PbtApmResumeAutomatic = 18;

    delegate uint Callback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Callback Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerRegisterSuspendResumeNotification(int flags, ref Params recipient, out IntPtr handle);

    static Callback? _keepAlive; // the delegate must outlive the registration
    static Params _params;
    static IntPtr _handle;

    public static void Watch(Action onSuspend, Action onResume, Action<string> log)
    {
        _keepAlive = (_, type, _) =>
        {
            try
            {
                if (type == PbtApmSuspend) { log("computer is going to sleep"); onSuspend(); }
                else if (type == PbtApmResumeAutomatic) { log("computer woke up"); onResume(); }
            }
            catch (Exception e) { log("power event: " + e.Message); }
            return 0;
        };
        _params = new Params { Callback = _keepAlive };
        uint err = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref _params, out _handle);
        if (err != 0) log("sleep notifications are not available (code " + err + ")");
    }
}
