using System.Runtime.InteropServices;

namespace Bwc.E2E.List;

/// <summary>
/// Raw keyboard input for the list tests. FlaUI's Keyboard.Pressing and TypeSimultaneously do not hold a
/// modifier while the next key is handled in this environment: the list saw Down before Shift. keybd_event
/// posts the same key messages the keyboard would, with the modifier already down when the arrow arrives.
/// Callers make the window foreground first (ControlTestSupport.EnsureForeground).
/// </summary>
internal static class RawKeyboard
{
    internal const ushort Shift = 0x10;
    internal const ushort Control = 0x11;
    internal const ushort Space = 0x20;
    internal const ushort Down = 0x28;
    internal const ushort A = 0x41;

    private const uint KeyEventfExtendedKey = 0x0001;
    private const uint KeyEventfKeyUp = 0x0002;
    private const uint MapVirtualKeyToScanCode = 0;
    private const int GapMilliseconds = 30;

    /// <summary>Holds a modifier down, runs the body, and releases the modifier even when the body throws.</summary>
    internal static void WithHeld(ushort modifier, Action body)
    {
        KeyEvent(modifier, keyUp: false);
        try
        {
            Thread.Sleep(GapMilliseconds);
            body();
        }
        finally
        {
            Thread.Sleep(GapMilliseconds);
            KeyEvent(modifier, keyUp: true);
        }
    }

    /// <summary>A key-down followed by a key-up for one key, with the standard gap between the two.</summary>
    internal static void Tap(ushort key)
    {
        KeyEvent(key, keyUp: false);
        Thread.Sleep(GapMilliseconds);
        KeyEvent(key, keyUp: true);
    }

    private static void KeyEvent(ushort key, bool keyUp)
    {
        byte scan = unchecked((byte)MapVirtualKey(key, MapVirtualKeyToScanCode));
        uint flags = (IsExtended(key) ? KeyEventfExtendedKey : 0) | (keyUp ? KeyEventfKeyUp : 0);
        keybd_event(unchecked((byte)key), scan, flags, UIntPtr.Zero);
    }

    /// <summary>Arrow, Home, End, Page, Insert and Delete keys carry the extended-key flag.</summary>
    private static bool IsExtended(ushort key) => key is >= 0x21 and <= 0x2E;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
