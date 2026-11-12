using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Harness;

/// <summary>
/// Captures what the screen shows for a window of the app, only when that picture is verified to be the app's.
/// PrintWindow was tried first and does not paint WinUI 3 content reliably on this host, so the capture is a
/// screen copy of the window rectangle. A screen copy shows whatever covers the window, so each capture first
/// samples a grid of points in the rectangle and requires every point to belong to the app process (its popups and
/// dialogs included). A point covered by a foreign process gets a <c>harness.foreign-foreground</c> note and one
/// re-assert of the app window, then one more check. If it is still covered, the capture is not judged: it throws
/// <see cref="NotJudgedException"/>, which makes the test inconclusive instead of passing or failing a pixel check.
/// Nothing here raises, topmosts or activates a window except that single re-assert.
/// A window that is still painting is not sampled either: each verified copy is repeated every 200 ms until five
/// consecutive copies (a quiet period of about one second) are identical apart from a blinking caret and the copy
/// is painted (not more than 40
/// percent pure black). A capture that has not settled after 10 seconds is never judged: it throws
/// <see cref="NotJudgedException"/> and records the note <c>harness.capture.unsettled</c>. Every judged or saved
/// picture goes through this class.
/// </summary>
internal static class WindowCapture
{
    private const int GridPoints = 5;
    private const int GridInset = 8;
    private const uint GaRoot = 2;
    private const int SettleCopies = 5;
    private const double UnpaintedBlackShare = 0.40;
    private const string DumpEnvironmentVariable = "BWC_E2E_DUMP_JUDGED";
    internal const string UnsettledNoteId = "harness.capture.unsettled";
    private static readonly TimeSpan SettleWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);
    private static int _dumpSequence;

    /// <summary>
    /// The window could not be seen unobscured by the app, so no pixel check can be judged. The test is
    /// inconclusive, which is neither a pass nor a failure. The note names the foreign process that covered it.
    /// </summary>
    internal sealed class NotJudgedException : AssertInconclusiveException
    {
        internal NotJudgedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>The whole window rectangle (GetWindowRect bounds), verified unobscured by the app.</summary>
    internal static Bitmap CaptureWindow(IntPtr hwnd)
    {
        Rectangle area = WindowArea(hwnd);
        return CaptureArea(area, ProcessIdOfWindow(hwnd), hwnd);
    }

    /// <summary>
    /// A region of the window, given by its top left in screen coordinates. Used for the client area, so pixel
    /// positions match <c>ClientToScreen</c>. The region is verified like <see cref="CaptureWindow"/>.
    /// </summary>
    internal static Bitmap CaptureRegion(IntPtr hwnd, Point screenOrigin, int width, int height) =>
        CaptureArea(new Rectangle(screenOrigin.X, screenOrigin.Y, width, height), ProcessIdOfWindow(hwnd), hwnd);

    /// <summary>
    /// A screen rectangle that must be shown by windows of <paramref name="processId"/> only (the app's popups and
    /// dialogs included). <paramref name="reassertTarget"/> is the app window re-asserted once when a foreign process
    /// covers the area.
    /// </summary>
    internal static Bitmap CaptureArea(Rectangle area, uint processId, IntPtr reassertTarget)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            throw new InvalidOperationException($"The capture area {area} has no size.");
        }

        // The expected owner is never taken from the area itself: a foreign window at the center would then be
        // accepted as the app. With no app process to verify against, the capture is not judged.
        if (processId == 0)
        {
            throw new NotJudgedException($"No app process is known for the capture of {area}, so it is not judged.");
        }

        uint expected = processId;
        if (!Unobscured(area, expected, out uint foreignPid, out string foreignName))
        {
            ForegroundGuard.ReassertForCapture(reassertTarget, foreignPid, foreignName, area);
            Thread.Sleep(SettleWait);
            if (!Unobscured(area, expected, out foreignPid, out foreignName))
            {
                throw Foreign(area, foreignName);
            }
        }

        // WinUI paints in stages: a black frame, then its background, then its text, with pauses between them. A copy
        // settles when SettleCopies consecutive copies, SettleInterval apart, are identical (a quiet period of about
        // one second) and the copy is painted. A copy that has not settled at the bound is never judged: the capture
        // is not judged, so the caller's pixel check is not run.
        DateTime deadline = DateTime.UtcNow + SettleTimeout;
        Bitmap? previous = null;
        byte[]? previousPixels = null;
        int identicalRun = 0;
        while (true)
        {
            Bitmap current = CopyVerified(area, expected);
            byte[] currentPixels = PixelBytes(current);
            identicalRun = (previousPixels is not null && SameApartFromCaret(previousPixels, currentPixels))
                ? identicalRun + 1
                : 1;
            double black = BlackShare(currentPixels);
            bool unpainted = black > UnpaintedBlackShare;
            if (identicalRun >= SettleCopies && !unpainted)
            {
                previous?.Dispose();
                DumpJudged(area, current);
                return current;
            }

            if (DateTime.UtcNow >= deadline)
            {
                previous?.Dispose();
                string text = UnsettledText(area, unpainted, black, identicalRun);
                E2EResults.Note(UnsettledNoteId, text);
                current.Dispose();
                throw new NotJudgedException(text);
            }

            previous?.Dispose();
            previous = current;
            previousPixels = currentPixels;
            Thread.Sleep(SettleInterval);
        }
    }

    /// <summary>
    /// One screen copy of the area, verified after the copy. A window can move or be covered during the copy, so
    /// every copy that is returned or compared has been verified on the same area.
    /// </summary>
    private static Bitmap CopyVerified(Rectangle area, uint expected)
    {
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
        }

        if (!Unobscured(area, expected, out _, out string foreignName))
        {
            bitmap.Dispose();
            throw Foreign(area, foreignName);
        }

        return bitmap;
    }

    /// <summary>The pixels of a 32 bit bitmap as BGRA bytes, one row after the other with no padding.</summary>
    private static byte[] PixelBytes(Bitmap bitmap)
    {
        int rowBytes = bitmap.Width * 4;
        var pixels = new byte[rowBytes * bitmap.Height];
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return pixels;
    }

    /// <summary>
    /// True when two copies differ in no more than a caret's worth of pixels. A focused edit box blinks its caret about
    /// every 530 ms, so strict equality would never settle a dialog with a focused edit box. The allowance is the larger
    /// of 0.1 percent of the area and 120 pixels (a 2 px caret at 200 percent scale is about 64 pixels).
    /// </summary>
    private static bool SameApartFromCaret(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        int total = a.Length / 4;
        int allowed = Math.Max(120, total / 1000);
        int differing = 0;
        for (int index = 0; index < a.Length; index += 4)
        {
            if (a[index] != b[index] || a[index + 1] != b[index + 1] || a[index + 2] != b[index + 2])
            {
                differing++;
                if (differing > allowed)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The fraction of pixels whose red, green and blue are all zero. Alpha is ignored.</summary>
    private static double BlackShare(byte[] pixels)
    {
        int total = pixels.Length / 4;
        if (total == 0)
        {
            return 0;
        }

        int black = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if ((pixels[index] | pixels[index + 1] | pixels[index + 2]) == 0)
            {
                black++;
            }
        }

        return (double)black / total;
    }

    private static string UnsettledText(Rectangle area, bool unpainted, double black, int identicalRun)
    {
        string state = unpainted
            ? $"still unpainted ({black:P0} pure black)"
            : $"still changing ({identicalRun} of {SettleCopies} identical copies in a row)";
        return $"capture of {area} did not settle within {SettleTimeout.TotalSeconds:0} s: {state}, so it is not judged.";
    }

    /// <summary>
    /// When the environment variable is 1, writes the copy a check is about to judge to artifacts/e2e/judged. It is a
    /// diagnostic only: the judged pixels do not change. Each file is numbered, so a run keeps every judged copy.
    /// </summary>
    private static void DumpJudged(Rectangle area, Bitmap bitmap)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(DumpEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        string dir = Path.Combine(GuiGate.Config.ArtifactsDir, "judged");
        Directory.CreateDirectory(dir);
        int sequence = Interlocked.Increment(ref _dumpSequence);
        bitmap.Save(
            Path.Combine(dir, $"{sequence:D4}-{area.Left}-{area.Top}-{area.Width}x{area.Height}.png"),
            ImageFormat.Png);
    }

    /// <summary>
    /// A screen rectangle shown by the app of the current session (<see cref="E2EResults.CurrentSession"/>), which is the
    /// most recently started app. Popups, flyouts and dialogs of that process count as the app. With no session the
    /// capture is not judged.
    /// </summary>
    internal static Bitmap CaptureAreaOfCurrentSession(Rectangle area)
    {
        AppSession? session = E2EResults.CurrentSession;
        if (session is null)
        {
            throw new NotJudgedException($"No app session is current for the capture of {area}, so it is not judged.");
        }

        return CaptureArea(area, unchecked((uint)session.ProcessId), session.MainHwnd);
    }

    /// <summary>
    /// Saves the whole window as a PNG and returns the path. Throws <see cref="NotJudgedException"/> when the window is
    /// covered, and when the file is not written.
    /// </summary>
    internal static string Save(IntPtr hwnd, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (Bitmap bitmap = CaptureWindow(hwnd))
        {
            bitmap.Save(path, ImageFormat.Png);
        }

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"Screenshot was not written to '{path}'.");
        }

        return path;
    }

    /// <summary>
    /// Saves an artifact picture. A capture that is not judged is recorded as a note and returns an empty string, so an
    /// artifact never fails a test. The PNG is the output, not a check.
    /// </summary>
    internal static string SaveArtifact(string noteId, IntPtr hwnd, string path)
    {
        try
        {
            return Save(hwnd, path);
        }
        catch (NotJudgedException ex)
        {
            E2EResults.Note(noteId, $"artifact not written: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>True when every grid point of the area is shown by <paramref name="processId"/>.
    /// Reports the first foreign owner.</summary>
    private static bool Unobscured(Rectangle area, uint processId, out uint foreignPid, out string foreignName)
    {
        foreignPid = 0;
        foreignName = string.Empty;
        int left = area.Left + GridInset;
        int top = area.Top + GridInset;
        int right = Math.Max(left, area.Right - GridInset - 1);
        int bottom = Math.Max(top, area.Bottom - GridInset - 1);
        for (int row = 0; row < GridPoints; row++)
        {
            int y = top + ((bottom - top) * row / (GridPoints - 1));
            for (int column = 0; column < GridPoints; column++)
            {
                int x = left + ((right - left) * column / (GridPoints - 1));
                IntPtr hit = WindowFromPoint(new Point(x, y));
                uint owner = hit == IntPtr.Zero ? 0 : OwnerOf(hit);
                if (hit == IntPtr.Zero || owner != processId)
                {
                    IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GaRoot);
                    foreignPid = owner;
                    foreignName = owner == 0
                        ? "no window"
                        : $"{ForegroundGuard.ProcessNameOf(owner)} (class '{Win32.ClassNameOf(root == IntPtr.Zero ? hit : root)}', " +
                            $"title '{Win32.TitleOf(root == IntPtr.Zero ? hit : root)}')";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The process of the top level window that holds <paramref name="hit"/>.
    /// Child controls therefore count as the app.</summary>
    private static uint OwnerOf(IntPtr hit)
    {
        IntPtr root = GetAncestor(hit, GaRoot);
        return Win32.ProcessIdOf(root == IntPtr.Zero ? hit : root);
    }

    private static uint ProcessIdOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
        {
            throw new InvalidOperationException("The window to capture does not exist.");
        }

        return Win32.ProcessIdOf(hwnd);
    }

    private static Rectangle WindowArea(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
        {
            throw new InvalidOperationException("The window to capture does not exist.");
        }

        if (!Win32.GetWindowRect(hwnd, out Win32.NativeRect rect))
        {
            throw new InvalidOperationException($"GetWindowRect failed for 0x{hwnd.ToInt64():X}.");
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"Window 0x{hwnd.ToInt64():X} ('{Win32.TitleOf(hwnd)}', class '{Win32.ClassNameOf(hwnd)}') has no size to capture.");
        }

        return new Rectangle(rect.Left, rect.Top, width, height);
    }

    private static NotJudgedException Foreign(Rectangle area, string foreignName)
    {
        E2EResults.Note(
            ForegroundGuard.ForeignNoteId,
            $"pixel capture of {area} not judged: the area is covered by '{foreignName}'.");
        return new NotJudgedException($"The capture of {area} is covered by '{foreignName}', so no pixel check is judged.");
    }

#pragma warning disable SYSLIB1054 // Two user32 calls; LibraryImport plumbing is not needed.
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
#pragma warning restore SYSLIB1054
}
