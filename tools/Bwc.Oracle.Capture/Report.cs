using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// Shared output helpers: deterministic JSON (LF, no byte-order mark, no timestamps), the environment blocks
// every tracked output carries, and screenshots of a single window with PrintWindow.
internal static class Report
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void WriteJson(string path, JsonNode node)
    {
        string json = node.ToJsonString(Options).Replace("\r\n", "\n") + "\n";
        Util.WriteUtf8NoBom(path, json);
        Console.WriteLine($"wrote {path}");
    }

    // Tracked reasons must not change between runs, so the process and window list that a timeout message carries
    // (pids and handles) is cut off here. The full message still goes to stderr.
    internal static string StableReason(string message)
    {
        int cut = message.IndexOf(" Windows of pid ", StringComparison.Ordinal);
        return cut < 0 ? message : message[..cut];
    }

    internal static JsonObject OsInfo()
    {
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false)
            ?? throw new InvalidOperationException("Cannot read the Windows NT version key.");
        return new JsonObject
        {
            ["productName"] = key.GetValue("ProductName")?.ToString(),
            ["currentBuild"] = key.GetValue("CurrentBuild")?.ToString(),
            ["ubr"] = Convert.ToInt32(key.GetValue("UBR") ?? 0),
            ["displayVersion"] = key.GetValue("DisplayVersion")?.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    internal static JsonObject RegeditInfo() => new()
    {
        ["path"] = RegeditProcess.RegeditPath,
        ["fileVersion"] = System.Diagnostics.FileVersionInfo.GetVersionInfo(RegeditProcess.RegeditPath).FileVersion,
        ["compatLayer"] = "RunAsInvoker",
    };

    internal static JsonObject DpiInfo(uint system, uint window, bool allowDpi) => new()
    {
        ["system"] = system,
        ["window"] = window,
        ["requiredForParity"] = 96,
        ["overrideAllowed"] = allowDpi,
    };

    // Relative rectangle: position inside the reference origin, in device pixels.
    internal static JsonObject RelRect(NativeRect screen, int originX, int originY) => new()
    {
        ["x"] = screen.Left - originX,
        ["y"] = screen.Top - originY,
        ["width"] = screen.Width,
        ["height"] = screen.Height,
    };

    internal static void SaveWindow(IntPtr hwnd, string path)
    {
        if (!Win32.GetWindowRect(hwnd, out NativeRect r) || r.Width <= 0 || r.Height <= 0)
        {
            throw new InvalidOperationException($"GetWindowRect failed or empty for window 0x{hwnd:X}.");
        }

        using var bitmap = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                if (!Win32.PrintWindow(hwnd, hdc, Msg.PW_RENDERFULLCONTENT))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow(PW_RENDERFULLCONTENT) failed.");
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"wrote {path} ({r.Width}x{r.Height})");
    }
}
