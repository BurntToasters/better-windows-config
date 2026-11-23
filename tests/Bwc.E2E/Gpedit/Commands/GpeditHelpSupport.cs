using System.Diagnostics;
using System.Runtime.InteropServices;
using Bwc.E2E.AppServices;
using Bwc.E2E.Harness;
using Bwc.E2E.List;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// The native About box of the editor (Help > About, Help > snap-in About), found, read and closed the way the Registry
/// Editor test AppServices_AboutShowsProductVersionAndCopyright does it: the box is a window of class #32770 owned by the
/// main window, titled "About " plus the application name, found by handle; its static text is read with the window
/// API; it is closed with Enter once the foreground guard made it the foreground window, and with WM_CLOSE when Enter
/// could not be sent, so that no run leaves a box open (docs/failure-modes/console-options.md, section 11).
/// </summary>
internal static class AboutProbe
{
    internal const string NativeClass = "#32770";
    private const uint WmClose = 0x0010;
    private static readonly TimeSpan BoxTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The caption of the box that the application name <paramref name="applicationName"/> gives.</summary>
    internal static string TitleFor(string applicationName) => "About " + applicationName;

    /// <summary>Waits for the box owned by the main window of the run. Zero when it did not appear.</summary>
    internal static IntPtr Wait(GpeditRun run, string title) =>
        AppServicesSupport.WaitForOwnedWindow(run.Shell.Session.MainHwnd, NativeClass, title, BoxTimeout);

    /// <summary>True when a box with the title is open now.</summary>
    internal static bool IsOpen(GpeditRun run, string title) =>
        AppServicesSupport.FindOwnedWindow(run.Shell.Session.MainHwnd, NativeClass, title) != IntPtr.Zero;

    /// <summary>The static texts of the box joined with line feeds.</summary>
    internal static string TextOf(IntPtr dialog) => string.Join("\n", AppServicesSupport.ChildTexts(dialog));

    /// <summary>The product version of the editor executable without build metadata.</summary>
    internal static string ExpectedVersion(E2EConfig config)
    {
        string? product = FileVersionInfo.GetVersionInfo(config.GpeditExePath).ProductVersion;
        if (string.IsNullOrWhiteSpace(product))
        {
            return string.Empty;
        }

        int plus = product.IndexOf('+', StringComparison.Ordinal);
        return (plus < 0 ? product : product[..plus]).Trim();
    }

    /// <summary>
    /// Closes the box with Enter when it is the foreground window, else with WM_CLOSE, and waits until it is gone. The
    /// result says whether Enter was the way it closed.
    /// </summary>
    internal static (bool Closed, bool ByEnter) Close(IntPtr dialog, string checkId)
    {
        bool focused = AppServicesSupport.Activate(dialog, checkId);
        if (focused)
        {
            RawKeyboard.Tap(0x0D);
        }

        bool closed = focused && AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(dialog), BoxTimeout);
        if (closed)
        {
            return (true, true);
        }

        _ = PostMessageW(dialog, WmClose, IntPtr.Zero, IntPtr.Zero);
        return (AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(dialog), BoxTimeout), false);
    }

#pragma warning disable SYSLIB1054 // The helper is small and needs no LibraryImport plumbing.
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
#pragma warning restore SYSLIB1054
}
