using System.ComponentModel;
using System.Runtime.InteropServices;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Keyboard input the probe helpers do not offer: chords with held modifiers and Unicode text. Every send is preceded by the foreground
// check (a key press that would reach another window is not sent), see docs/failure-modes/oracle-gpedit-driver.md section 11.
internal static class DriverInput
{
    internal const ushort VkShift = 0x10;
    internal const ushort VkControl = 0x11;
    internal const ushort VkApps = 0x5D;
    internal const ushort VkHome = 0x24;
    internal const ushort VkDown = 0x28;
    internal const ushort VkRight = 0x27;
    internal const ushort VkDelete = 0x2E;
    internal const ushort VkF2 = 0x71;
    internal const ushort VkSpace = 0x20;
    internal const ushort VkA = 0x41;

    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;

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

    // Brings the window to the foreground and requires the foreground window to belong to the mmc process before any key is sent.
    internal static void RequireForeground(IntPtr window, int pid)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            ProbeInput.Foreground(window);
            IntPtr front = DriverNative.GetForegroundWindow();
            if (front != IntPtr.Zero && DriverNative.ProcessOf(front) == (uint)pid)
            {
                return;
            }

            Thread.Sleep(300);
        }

        IntPtr now = DriverNative.GetForegroundWindow();
        throw new DriverException(
            $"focus stolen: the foreground window is 0x{now:X} '{ProbeWindows.ClassOf(now)}' '{ProbeWindows.TitleOf(now)}', not a window of mmc.exe (pid {pid}). No key was sent.");
    }

    // Presses modifiers in order, the key, then releases in reverse.
    internal static void Chord(ushort[] modifiers, ushort key)
    {
        var records = new List<InputRecord>();
        foreach (ushort m in modifiers)
        {
            records.Add(KeyRecord(m, up: false));
        }

        records.Add(KeyRecord(key, up: false));
        records.Add(KeyRecord(key, up: true));
        for (int i = modifiers.Length - 1; i >= 0; i--)
        {
            records.Add(KeyRecord(modifiers[i], up: true));
        }

        Send(records.ToArray());
        Thread.Sleep(150);
    }

    internal static void Key(ushort key) => Chord(Array.Empty<ushort>(), key);

    // Types text as Unicode key events (no layout needed, ampersands and non-ASCII are safe).
    internal static void TypeText(string text)
    {
        foreach (char ch in text)
        {
            Send(UnicodeRecord(ch, up: false), UnicodeRecord(ch, up: true));
            Thread.Sleep(15);
        }

        Thread.Sleep(150);
    }

    // A character typed as a key (menu access keys): the virtual key of the current layout, with Shift when the layout needs it.
    internal static void CharKey(char ch)
    {
        (ushort Vk, bool Shift)? key = DriverNative.KeyFor(ch);
        if (key is null)
        {
            throw new DriverException($"no key for the character '{ch}' on this keyboard layout");
        }

        Chord(key.Value.Shift ? new[] { VkShift } : Array.Empty<ushort>(), key.Value.Vk);
    }

    private static InputRecord KeyRecord(ushort vk, bool up) => new()
    {
        type = InputKeyboard,
        ki = new KeybdInput { wVk = vk, dwFlags = up ? KeyUp : 0 },
    };

    private static InputRecord UnicodeRecord(char ch, bool up) => new()
    {
        type = InputKeyboard,
        ki = new KeybdInput { wScan = ch, dwFlags = KeyUnicode | (up ? KeyUp : 0) },
    };

    private static void Send(params InputRecord[] records)
    {
        if (SendInput((uint)records.Length, records, Marshal.SizeOf<InputRecord>()) != records.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed (is the desktop locked or another window blocking input?)");
        }
    }
}
