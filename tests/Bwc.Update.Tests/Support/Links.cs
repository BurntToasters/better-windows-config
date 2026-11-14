using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.Update.Tests;

/// <summary>Junctions (mklink, no elevation needed) and 8.3 short names (GetShortPathName).</summary>
internal static partial class Links
{
    public static bool TryMakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start cmd");
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>The 8.3 form of an existing path. Equals the input when the volume has no short names for it.</summary>
    public static string ShortPath(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        return length == 0 ? path : buffer.ToString(0, (int)length);
    }

#pragma warning disable SYSLIB1054 // The output buffer is marshalled directly.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint length);
#pragma warning restore SYSLIB1054
}
