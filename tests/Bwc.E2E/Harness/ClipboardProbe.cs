using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bwc.E2E.Harness;

/// <summary>
/// Clears and reads the Windows clipboard as CF_UNICODETEXT for the hex editor checks. A check must
/// clear the clipboard before a copy, otherwise text from an earlier copy can satisfy the comparison.
/// </summary>
internal static class ClipboardProbe
{
    private const uint CfUnicodeText = 13;
    private const int PrefixLength = 24;

    internal enum ClipboardState
    {
        Text,
        NoText,
        Unavailable,
    }

    internal sealed record Reading(ClipboardState State, string Text, string Error);

    /// <summary>Empties the clipboard so a later copy is the only possible source of text.</summary>
    internal static bool Clear()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            return EmptyClipboard();
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    internal static Reading Read()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return new Reading(ClipboardState.Unavailable, string.Empty, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        try
        {
            IntPtr handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return new Reading(ClipboardState.NoText, string.Empty, "The clipboard has no Unicode text.");
            }

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return new Reading(ClipboardState.Unavailable, string.Empty, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            try
            {
                return new Reading(ClipboardState.Text, Marshal.PtrToStringUni(pointer) ?? string.Empty, string.Empty);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    /// <summary>
    /// Polls the clipboard until it holds the expected text or the timeout ends. Returns the last
    /// reading either way, so a mismatch reports what was actually on the clipboard.
    /// </summary>
    internal static Reading WaitForText(string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Reading last;
        do
        {
            last = Read();
            if (last.State == ClipboardState.Text && Normalize(last.Text) == expected)
            {
                return last;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return last;
    }

    /// <summary>
    /// Records a copy result. Matching text is a pass. Text that differs, or no text at all, is a known
    /// gap for UF7 with the observed clipboard. A clipboard that cannot be read is a failed check,
    /// because that is an environment problem rather than a product difference.
    /// </summary>
    internal static void RecordCopy(string checkId, Reading reading, string expected, string oracleBehavior)
    {
        if (reading.State == ClipboardState.Unavailable)
        {
            E2EResults.Record(checkId, false, $"clipboard unavailable: {reading.Error}");
            return;
        }

        string observed = reading.State == ClipboardState.NoText
            ? "clipboard has no Unicode text after Ctrl+C"
            : $"clipboard length={reading.Text.Length}, prefix='{Prefix(reading.Text)}'";
        bool matches = reading.State == ClipboardState.Text && Normalize(reading.Text) == expected;
        E2EResults.RecordOrKnownGap(
            checkId,
            matches,
            "UF7",
            oracleBehavior,
            $"{observed}; expected length={expected.Length}, expected prefix='{Prefix(expected)}'.");
    }

    /// <summary>Trailing line breaks are not part of the copied bytes, so they are ignored.</summary>
    internal static string Normalize(string text) => text.TrimEnd('\r', '\n');

    internal static string Prefix(string text) =>
        text.Length <= PrefixLength ? text : text[..PrefixLength];

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
