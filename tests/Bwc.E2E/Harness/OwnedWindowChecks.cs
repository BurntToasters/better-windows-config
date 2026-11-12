using System.Text;

namespace Bwc.E2E.Harness;

/// <summary>
/// Real checks made when an owned window (a classic dialog, a message box, a file dialog, ChooseFont, a Find
/// progress window) appears. The harness does nothing to the window: no raise, no topmost, no activation. The
/// checks read the state the product left behind.
/// <list type="bullet">
/// <item><c>dialog.zorder.&lt;label&gt;</c>: the window is above its owner. The owner's <c>GW_HWNDPREV</c>
/// chain is walked upward and must reach the window.</item>
/// <item><c>dialog.foreground.&lt;label&gt;</c>: within 1.5 seconds the window is the foreground window. When
/// another window of the app holds the foreground, the check fails and names it. When another process holds
/// it, the check is not judged and the foreign note records that instead.</item>
/// </list>
/// </summary>
internal static class OwnedWindowChecks
{
    private const uint GwOwner = 4;
    private const int MaxZWalk = 20000;
    private static readonly TimeSpan ForegroundWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>Records the z-order and foreground checks for a window that just appeared.</summary>
    internal static void Record(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.IsWindow(window))
        {
            return;
        }

        string label = LabelOf(window);
        IntPtr owner = GetOwner(window);
        string title = Win32.TitleOf(window);
        if (owner == IntPtr.Zero)
        {
            E2EResults.Note(
                "dialog.zorder." + label,
                $"window 0x{window.ToInt64():X} ('{title}') has no owner, so no owner z-order check applies.");
        }
        else
        {
            bool above = IsAbove(window, owner);
            E2EResults.Record(
                "dialog.zorder." + label,
                above,
                $"window 0x{window.ToInt64():X} ('{title}') above owner 0x{owner.ToInt64():X} in z order={above}.");
        }

        RecordForeground(window, label, title);
    }

    /// <summary>The owner window of a window, or zero.</summary>
    internal static IntPtr GetOwner(IntPtr window) => Win32.GetWindow(window, (int)GwOwner);

    /// <summary>True when <paramref name="window"/> is in the z order above <paramref name="owner"/>.</summary>
    internal static bool IsAbove(IntPtr window, IntPtr owner)
    {
        IntPtr current = owner;
        for (int step = 0; step < MaxZWalk && current != IntPtr.Zero; step++)
        {
            current = Win32.WindowAbove(current);
            if (current == window)
            {
                return true;
            }
        }

        return false;
    }

    private static void RecordForeground(IntPtr window, string label, string title)
    {
        DateTime deadline = DateTime.UtcNow + ForegroundWait;
        ForegroundState state = ForegroundGuard.Observe(window, out string description);
        while (state != ForegroundState.Target && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
            state = ForegroundGuard.Observe(window, out description);
        }

        string checkId = "dialog.foreground." + label;
        switch (state)
        {
            case ForegroundState.Target:
                E2EResults.Record(checkId, true, $"window 0x{window.ToInt64():X} ('{title}') is the foreground window.");
                break;
            case ForegroundState.AppOther:
                E2EResults.Record(
                    checkId,
                    false,
                    $"window 0x{window.ToInt64():X} ('{title}') was not foreground; {description} belongs to the app.");
                break;
            default:
                E2EResults.Note(
                    ForegroundGuard.ForeignNoteId,
                    $"{checkId}: window 0x{window.ToInt64():X} ('{title}') not judged; {description} is another process.");
                break;
        }
    }

    /// <summary>A short, check-id safe label: the title when there is one, else the class name.</summary>
    internal static string LabelOf(IntPtr window)
    {
        string text = Win32.TitleOf(window);
        if (text.Length == 0)
        {
            text = "window-" + window.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                _ = builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                _ = builder.Append('-');
            }
        }

        string label = builder.ToString().Trim('-');
        if (label.Length > 40)
        {
            label = label[..40].TrimEnd('-');
        }

        return label.Length == 0 ? "window" : label;
    }
}
