using System.Runtime.InteropServices;

namespace TimeCapsuleDrive.Install;

/// <summary>
/// Tells Explorer (and other programs showing a list of drives) that a drive letter appeared or went away.
/// The background drive runs outside the user's desktop session, so its mount does not make already open
/// Explorer windows refresh; setup runs on the user's desktop and can announce it.
/// </summary>
static class ShellNotify
{
    const int ShcneDriveRemoved = 0x80, ShcneDriveAdd = 0x100;
    const uint ShcnfPathW = 0x0005, ShcnfFlush = 0x1000;
    const uint WmDeviceChange = 0x0219, SmtoAbortIfHung = 0x0002;
    const int DbtDeviceArrival = 0x8000, DbtDeviceRemoveComplete = 0x8004, DbtDevTypVolume = 2;
    static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    struct DevBroadcastVolume
    {
        public int Size, DeviceType, Reserved, UnitMask;
        public short Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, ref DevBroadcastVolume lParam, uint flags, uint timeoutMs, out IntPtr result);

    public static void DriveAdded(string drive) => Announce(drive, true);
    public static void DriveRemoved(string drive) => Announce(drive, false);

    static void Announce(string drive, bool added)
    {
        try
        {
            char letter = char.ToUpperInvariant(drive[0]);
            if (letter < 'A' || letter > 'Z') return;
            SHChangeNotify(added ? ShcneDriveAdd : ShcneDriveRemoved, ShcnfPathW | ShcnfFlush, letter + ":\\", IntPtr.Zero);
            var v = new DevBroadcastVolume
            {
                Size = Marshal.SizeOf<DevBroadcastVolume>(), DeviceType = DbtDevTypVolume, UnitMask = 1 << (letter - 'A'),
            };
            SendMessageTimeout(HwndBroadcast, WmDeviceChange, new IntPtr(added ? DbtDeviceArrival : DbtDeviceRemoveComplete), ref v,
                SmtoAbortIfHung, 1000, out _);
        }
        catch (Exception) { } // purely cosmetic: never let it break an installation
    }
}
