using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// Real keyboard and mouse input for the probe (SendInput), used only where UI Automation patterns are not available: opening a list row
// with Enter, Tab order, Escape on a menu, and a mouse click as the last fallback. The target window is made foreground first.
internal static class ProbeInput
{
    internal const ushort VkTab = 0x09;
    internal const ushort VkReturn = 0x0D;
    internal const ushort VkEscape = 0x1B;
    internal const ushort VkMenu = 0x12;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public MouseInput mi;

        [FieldOffset(8)]
        public KeybdInput ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, InputRecord[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    internal static void Foreground(IntPtr hwnd)
    {
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
        Thread.Sleep(200);
    }

    internal static void Key(ushort vk)
    {
        Send(KeyRecord(vk, up: false), KeyRecord(vk, up: true));
        Thread.Sleep(150);
    }

    // Alt held while one key is pressed (menu access keys).
    internal static void AltKey(ushort vk)
    {
        Send(KeyRecord(VkMenu, up: false), KeyRecord(vk, up: false), KeyRecord(vk, up: true), KeyRecord(VkMenu, up: true));
        Thread.Sleep(250);
    }

    internal static void Click(int x, int y, bool doubleClick)
    {
        SetCursorPos(x, y);
        Thread.Sleep(80);
        for (int i = 0; i < (doubleClick ? 2 : 1); i++)
        {
            Send(MouseRecord(MouseLeftDown), MouseRecord(MouseLeftUp));
            Thread.Sleep(60);
        }

        Thread.Sleep(150);
    }

    private static InputRecord KeyRecord(ushort vk, bool up) => new()
    {
        type = InputKeyboard,
        ki = new KeybdInput { wVk = vk, dwFlags = up ? KeyUp : 0 },
    };

    private static InputRecord MouseRecord(uint flags) => new()
    {
        type = InputMouse,
        mi = new MouseInput { dwFlags = flags },
    };

    private static void Send(params InputRecord[] records)
    {
        if (SendInput((uint)records.Length, records, Marshal.SizeOf<InputRecord>()) != records.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed (is the desktop locked or another window blocking input?)");
        }
    }
}
