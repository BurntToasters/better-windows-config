using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.Win32;

namespace Bwc.E2E.Dialogs;

/// <summary>
/// Pixel contrast checks for the classic dialogs. UI Automation reads the text of a control, not what is
/// drawn, so a dialog whose text is painted in its own background color passes every text check. These
/// checks capture the client area of the dialog and read its pixels:
/// (a) the most common color of the whole client area is the palette dialog face, within
///     <see cref="FaceTolerance"/> on each channel;
/// (b) each visible static text control has at least <see cref="MinimumContrastPixels"/> pixels inside its
///     rectangle whose luminance differs from the text background by more than
///     <see cref="MinimumLuminanceDifference"/> of full scale. The text background is the most common color
///     inside that rectangle.
/// Invisible text fails (b). A dialog painted in the wrong color fails (a).
/// </summary>
internal static class DialogContrast
{
    internal const int MinimumContrastPixels = 12;
    internal const double MinimumLuminanceDifference = 0.40;
    internal const int FaceTolerance = 6;

    /// <summary>COLOR_3DFACE, the classic dialog face on the light palette.</summary>
    private const int Color3DFace = 15;

    /// <summary>The dialog face of the computed dark palette (BwcSystemColors.DarkFace).</summary>
    private static readonly Color DarkFace = Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B);

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>The dialog face the palette paints: the dark face, or the system 3D face on the light palette.</summary>
    internal static Color FaceFor(bool dark) => dark ? DarkFace : SystemFace();

    /// <summary>The face for a run with no theme override, which follows the Windows app mode.</summary>
    internal static Color FaceForSystem() => FaceFor(AppsUseDarkTheme());

    /// <summary>
    /// Captures the client area of <paramref name="hwnd"/> and records two checks: "{checkId}.background" for (a) and
    /// "{checkId}.text-contrast" for (b). <paramref name="window"/> is the UI Automation element of the same window.
    /// The caller brings the window to the foreground first, because the capture is a screen copy.
    /// </summary>
    internal static void Check(string checkId, AutomationElement window, IntPtr hwnd, Color expectedFace)
    {
        if (!GetClientRect(hwnd, out Win32.NativeRect client) || client.Right <= 0 || client.Bottom <= 0)
        {
            E2EResults.Record(checkId + ".background", false, "The dialog has no client area to capture.");
            E2EResults.Record(checkId + ".text-contrast", false, "The dialog has no client area to capture.");
            return;
        }

        // The main window is topmost, which puts a non-topmost dialog beneath it. The capture is a screen copy, so the
        // dialog is raised above the main window and activated first; otherwise the sample shows the main window.
        _ = Win32.SetTopmost(hwnd, true);
        _ = Win32.ForceForeground(hwnd, TimeSpan.FromSeconds(2));

        var origin = new Point(0, 0);
        if (!ClientToScreen(hwnd, ref origin))
        {
            E2EResults.Record(checkId + ".background", false, "ClientToScreen failed for the dialog.");
            E2EResults.Record(checkId + ".text-contrast", false, "ClientToScreen failed for the dialog.");
            return;
        }

        int width = client.Right;
        int height = client.Bottom;
        int[] pixels = Capture(origin, width, height);
        List<Rectangle> edits = EditRectangles(window, origin, width, height);
        CheckFace(checkId, pixels, width, height, expectedFace, edits);
        CheckEdits(checkId, pixels, width, edits, expectedFace);
        CheckText(checkId, window, pixels, width, height, origin);
    }

    /// <summary>
    /// The face is the most common color outside the edit boxes. An edit box is painted in the palette window
    /// color, so it is checked on its own in <see cref="CheckEdits"/>.
    /// </summary>
    private static void CheckFace(string checkId, int[] pixels, int width, int height, Color expectedFace, List<Rectangle> edits)
    {
        int dominant = MostCommonOutside(pixels, width, height, edits);
        Color actual = FromRgb(dominant);
        bool matches = Within(actual, expectedFace, FaceTolerance);
        E2EResults.Record(
            checkId + ".background",
            matches,
            $"Most common client color outside the edit boxes is {Hex(actual)}, expected the palette face {Hex(expectedFace)} within {FaceTolerance} per channel.");
    }

    /// <summary>Each edit box must be painted in the palette window color (COLOR_WINDOW on the light palette).</summary>
    private static void CheckEdits(string checkId, int[] pixels, int width, List<Rectangle> edits, Color expectedFace)
    {
        Color expectedWindow = expectedFace == DarkFace ? DarkWindow : SystemWindow();
        var problems = new List<string>();
        foreach (Rectangle edit in edits)
        {
            Rectangle inner = Rectangle.Inflate(edit, -3, -3);
            if (inner.Width <= 0 || inner.Height <= 0)
            {
                continue;
            }

            Color actual = FromRgb(MostCommon(pixels, width, inner));
            if (!Within(actual, expectedWindow, FaceTolerance))
            {
                problems.Add($"edit {edit.Left},{edit.Top},{edit.Width}x{edit.Height} is {Hex(actual)}");
            }
        }

        E2EResults.Record(
            checkId + ".edit-background",
            problems.Count == 0,
            $"{edits.Count} edit box(es), expected the palette window color {Hex(expectedWindow)}. " +
            (problems.Count == 0 ? "All edit boxes use it." : string.Join("; ", problems)));
    }

    /// <summary>The edit boxes of the window as client rectangles, clipped to the client area.</summary>
    private static List<Rectangle> EditRectangles(AutomationElement window, Point origin, int width, int height)
    {
        var result = new List<Rectangle>();
        foreach (AutomationElement edit in window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)))
        {
            System.Drawing.Rectangle screen = edit.BoundingRectangle;
            if (screen.Width <= 0 || screen.Height <= 0)
            {
                continue;
            }

            Rectangle local = Rectangle.Intersect(
                new Rectangle(screen.Left - origin.X, screen.Top - origin.Y, screen.Width, screen.Height),
                new Rectangle(0, 0, width, height));
            if (local.Width > 0 && local.Height > 0)
            {
                result.Add(local);
            }
        }

        return result;
    }

    private static int MostCommonOutside(int[] pixels, int width, int height, List<Rectangle> excluded)
    {
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (excluded.Any(rect => rect.Contains(x, y)))
                {
                    continue;
                }

                int value = pixels[(y * width) + x];
                counts[value] = counts.TryGetValue(value, out int seen) ? seen + 1 : 1;
            }
        }

        int best = 0;
        int bestCount = -1;
        foreach (KeyValuePair<int, int> pair in counts)
        {
            if (pair.Value > bestCount)
            {
                best = pair.Key;
                bestCount = pair.Value;
            }
        }

        return best;
    }

    private static void CheckText(
        string checkId,
        AutomationElement window,
        int[] pixels,
        int width,
        int height,
        Point origin)
    {
        List<Rectangle> texts = VisibleTextRectangles(window);
        if (texts.Count == 0)
        {
            E2EResults.Record(checkId + ".text-contrast", false, "No visible static text control was found in the dialog.");
            return;
        }

        var problems = new List<string>();
        int leastCount = int.MaxValue;
        foreach (Rectangle screen in texts)
        {
            Rectangle local = Rectangle.Intersect(
                new Rectangle(screen.Left - origin.X, screen.Top - origin.Y, screen.Width, screen.Height),
                new Rectangle(0, 0, width, height));
            if (local.Width <= 0 || local.Height <= 0)
            {
                problems.Add($"text rectangle {screen} lies outside the client area");
                leastCount = 0;
                continue;
            }

            int background = MostCommon(pixels, width, local);
            int count = CountStrongPixels(pixels, width, local, background);
            leastCount = Math.Min(leastCount, count);
            if (count < MinimumContrastPixels)
            {
                problems.Add($"text rectangle {local.Left},{local.Top},{local.Width}x{local.Height} has {count} strong pixels on {Hex(FromRgb(background))}");
            }
        }

        E2EResults.Record(
            checkId + ".text-contrast",
            problems.Count == 0,
            $"{texts.Count} static text controls, least strong-contrast pixel count {leastCount} (need at least {MinimumContrastPixels}, luminance difference over {MinimumLuminanceDifference:0.00}). " +
            (problems.Count == 0 ? "All text is readable." : string.Join("; ", problems)));
    }

    /// <summary>The visible static text rectangles of a window, in screen pixels. Hidden or empty controls skip.</summary>
    private static List<Rectangle> VisibleTextRectangles(AutomationElement window)
    {
        var rectangles = new List<Rectangle>();
        AutomationElement[] elements = window.FindAllDescendants(
            cf => cf.ByControlType(ControlType.Text).Or(cf.ByControlType(ControlType.Document)));
        foreach (AutomationElement element in elements)
        {
            try
            {
                if (element.Properties.IsOffscreen.ValueOrDefault || string.IsNullOrEmpty(element.Name))
                {
                    continue;
                }

                Rectangle bounds = element.BoundingRectangle;
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    rectangles.Add(bounds);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The control left the tree while it was read.
            }
        }

        return rectangles;
    }

    /// <summary>Copies the screen rectangle into an array of 0xRRGGBB values, row by row.</summary>
    private static int[] Capture(Point origin, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        Rectangle all = new(0, 0, width, height);
        BitmapData data = bitmap.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] raw = new byte[data.Stride * height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            var pixels = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * data.Stride) + (x * 4);
                    pixels[(y * width) + x] = (raw[offset + 2] << 16) | (raw[offset + 1] << 8) | raw[offset];
                }
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>The most common 0xRRGGBB value inside a rectangle of the pixel array.</summary>
    private static int MostCommon(int[] pixels, int width, Rectangle area)
    {
        var counts = new Dictionary<int, int>();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                int value = pixels[(y * width) + x];
                counts[value] = counts.TryGetValue(value, out int seen) ? seen + 1 : 1;
            }
        }

        int best = 0;
        int bestCount = -1;
        foreach (KeyValuePair<int, int> pair in counts)
        {
            if (pair.Value > bestCount)
            {
                best = pair.Key;
                bestCount = pair.Value;
            }
        }

        return best;
    }

    /// <summary>Counts the pixels in a rectangle whose luminance differs from the background by over the threshold.</summary>
    private static int CountStrongPixels(int[] pixels, int width, Rectangle area, int background)
    {
        double backgroundLuma = Luma(background);
        int count = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                double difference = Math.Abs(Luma(pixels[(y * width) + x]) - backgroundLuma) / 255.0;
                if (difference > MinimumLuminanceDifference)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Perceived luminance on a 0 to 255 scale.</summary>
    private static double Luma(int rgb)
    {
        int red = (rgb >> 16) & 0xFF;
        int green = (rgb >> 8) & 0xFF;
        int blue = rgb & 0xFF;
        return (0.299 * red) + (0.587 * green) + (0.114 * blue);
    }

    private static Color SystemFace()
    {
        uint colorRef = GetSysColor(Color3DFace);
        return Color.FromArgb(0xFF, (int)(colorRef & 0xFF), (int)((colorRef >> 8) & 0xFF), (int)((colorRef >> 16) & 0xFF));
    }

    /// <summary>The palette window color of the dark palette (BwcSystemColors.DarkWindow).</summary>
    private static readonly Color DarkWindow = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);

    /// <summary>COLOR_WINDOW, the edit box color on the light palette.</summary>
    private const int ColorWindow = 5;

    private static Color SystemWindow()
    {
        uint colorRef = GetSysColor(ColorWindow);
        return Color.FromArgb(0xFF, (int)(colorRef & 0xFF), (int)((colorRef >> 8) & 0xFF), (int)((colorRef >> 16) & 0xFF));
    }

    private static bool AppsUseDarkTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static bool Within(Color actual, Color expected, int tolerance) =>
        Math.Abs(actual.R - expected.R) <= tolerance &&
        Math.Abs(actual.G - expected.G) <= tolerance &&
        Math.Abs(actual.B - expected.B) <= tolerance;

    private static Color FromRgb(int rgb) =>
        Color.FromArgb(0xFF, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

#pragma warning disable SYSLIB1054 // The helper uses plain DllImport like the rest of the harness.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32.NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
#pragma warning restore SYSLIB1054
}
