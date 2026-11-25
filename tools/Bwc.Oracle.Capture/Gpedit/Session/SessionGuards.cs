using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// Checks that must pass before a session touches anything. Nothing here elevates or prompts.
internal static class SessionGuards
{
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);

    internal static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal const string ElevationMessage =
        "refused: this process is not elevated. Classic gpedit and the GroupPolicy backup need administrator rights. " +
        "Start the tool from an administrator terminal on the VM desktop. The tool never elevates itself and never shows a UAC prompt.";

    // Null when an interactive desktop is available. Otherwise the reason.
    internal static string? InteractiveProblem()
    {
        if (!Environment.UserInteractive)
        {
            return "refused: this process is not interactive (no desktop window station).";
        }

        using Process me = Process.GetCurrentProcess();
        if (me.SessionId == 0)
        {
            return "refused: this process runs in session 0, which has no interactive desktop.";
        }

        foreach (string name in new[] { "SSH_CONNECTION", "SSH_CLIENT", "SSH_TTY" })
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                return $"refused: {name} is set, so this looks like an SSH session. Run the tool on the VM desktop.";
            }
        }

        var buffer = new StringBuilder(128);
        if (GetUserObjectInformationW(GetProcessWindowStation(), UoiName, buffer, buffer.Capacity, out _)
            && !string.Equals(buffer.ToString(), "WinSta0", StringComparison.OrdinalIgnoreCase))
        {
            return $"refused: the window station is '{buffer}', not WinSta0 (the interactive desktop).";
        }

        return null;
    }
}
