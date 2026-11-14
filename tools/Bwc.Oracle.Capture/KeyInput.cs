using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bwc.Oracle.Capture;

// Key presses for the keys capture. A posted WM_KEYDOWN does not set the modifier state that TranslateAccelerator
// reads, so the modifier bits are set with SetKeyboardState on this thread's input queue, attached to regedit's GUI
// thread for the length of one press. The state is cleared and the queues are detached in a finally. No SendInput,
// keybd_event or foreground call, so the user's keyboard and focus are not taken over.
internal static class KeyInput
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    internal readonly record struct Mods(bool Shift = false, bool Ctrl = false, bool Alt = false)
    {
        internal bool Any => Shift || Ctrl || Alt;

        internal IReadOnlyList<string> Names()
        {
            var names = new List<string>();
            if (Ctrl)
            {
                names.Add("Ctrl");
            }

            if (Alt)
            {
                names.Add("Alt");
            }

            if (Shift)
            {
                names.Add("Shift");
            }

            return names;
        }

        // The accelerator flag bits this combination must match (FSHIFT, FCONTROL, FALT).
        internal int AccelFlags => (Shift ? ResourceDump.AccelShift : 0) | (Ctrl ? ResourceDump.AccelControl : 0) | (Alt ? ResourceDump.AccelAlt : 0);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

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
    private struct HardwareInput
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public MouseInput mi;

        [FieldOffset(8)]
        public KeybdInput ki;

        [FieldOffset(8)]
        public HardwareInput hi;
    }

    // Types text into the Connect picker's name box with SendInput, one Unicode character at a time. The picker is made the
    // foreground window first. Before each character the picker must be the foreground window and the name box must hold the
    // keyboard focus of regedit's GUI thread. If either check fails, typing stops and the reason is returned.
    // Used only for the Connect picker (docs/failure-modes/oracle.md, section 5, exception).
    internal static bool TypeIntoPicker(IntPtr main, IntPtr picker, IntPtr edit, string text, out string reason)
    {
        uint thread = Win32.GetWindowThreadProcessId(main, out _);
        uint me = GetCurrentThreadId();
        if (!AttachThreadInput(me, thread, true))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AttachThreadInput failed for the regedit GUI thread.");
        }

        try
        {
            SetFocus(edit);
            SetForegroundWindow(picker);
            Thread.Sleep(300);
            foreach (char ch in text)
            {
                if (GetForegroundWindow() != picker)
                {
                    reason = $"focus lost: the picker is not the foreground window before character '{ch}'";
                    return false;
                }

                if (!HasKeyboardFocus(thread, edit))
                {
                    reason = $"focus lost: the name box does not hold the keyboard focus before character '{ch}'";
                    return false;
                }

                SendUnicode(ch, up: false);
                Thread.Sleep(40);
                SendUnicode(ch, up: true);
                Thread.Sleep(80);
            }

            Thread.Sleep(300);
            reason = string.Empty;
            return true;
        }
        finally
        {
            AttachThreadInput(me, thread, false);
        }
    }

    private static bool HasKeyboardFocus(uint thread, IntPtr control)
    {
        var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return Win32Ext.GetGUIThreadInfo(thread, ref info) && info.hwndFocus == control;
    }

    private static void SendUnicode(char ch, bool up)
    {
        var input = new INPUT[1];
        input[0].type = InputKeyboard;
        input[0].ki = new KeybdInput
        {
            wVk = 0,
            wScan = ch,
            dwFlags = KeyeventfUnicode | (up ? KeyeventfKeyup : 0),
            time = 0,
            dwExtraInfo = IntPtr.Zero,
        };
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed.");
        }
    }

    // Presses one key on target (the focused control) and releases it. The modifier state is visible to the key's
    // handler.
    internal static void Press(IntPtr main, IntPtr target, int vk, Mods mods)
    {
        uint thread = Win32.GetWindowThreadProcessId(main, out _);
        uint me = GetCurrentThreadId();
        bool attached = false;
        if (mods.Any)
        {
            if (!AttachThreadInput(me, thread, true))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AttachThreadInput failed for the regedit GUI thread.");
            }

            attached = true;
        }

        try
        {
            if (attached)
            {
                var state = new byte[256];
                if (mods.Shift)
                {
                    state[VkShift] = 0x80;
                }

                if (mods.Ctrl)
                {
                    state[VkControl] = 0x80;
                }

                if (mods.Alt)
                {
                    state[VkMenu] = 0x80;
                }

                if (!SetKeyboardState(state))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetKeyboardState failed.");
                }
            }

            // Alt keys are WM_SYSKEYDOWN with the context-code bit (29) set. Repeat count 1, scan code in bits 16 to
            // 23.
            uint scan = MapVirtualKeyW((uint)vk, 0);
            long down = 1L | ((long)scan << 16) | (mods.Alt ? 1L << 29 : 0L);
            long up = down | (1L << 30) | (1L << 31);
            Win32.PostMessageW(target, mods.Alt ? WmSysKeyDown : WmKeyDown, new IntPtr(vk), new IntPtr(down));
            Thread.Sleep(250);
            Win32.PostMessageW(target, mods.Alt ? WmSysKeyUp : WmKeyUp, new IntPtr(vk), new IntPtr(up));
            Thread.Sleep(150);
        }
        finally
        {
            if (attached)
            {
                SetKeyboardState(new byte[256]);
                AttachThreadInput(me, thread, false);
            }
        }
    }
}
