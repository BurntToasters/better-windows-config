using System.Runtime.InteropServices;
using Bwc.Regedit.Controllers;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// Checks the selection and the process token for the commands that must refuse them. A remote
/// computer node is read-only in this version, so every command that would change the registry
/// checks <see cref="IsRemoteSelected"/> first. Elevation is read, never requested: the Registry
/// Editor does not self-elevate, and a restore that needs elevation is refused when it is not held.
/// </summary>
internal static partial class RegeditSelectionGuards
{
    /// <summary>True when the tree selection is a remote computer or one of its keys.</summary>
    public static bool IsRemoteSelected(RegeditContext context) =>
        context.Shell.TreeContent.SelectedNode?.RegistryPath is { Length: > 0 } displayPath &&
        displayPath.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>True when this process runs with an elevated token. It never prompts for elevation.</summary>
    public static bool IsElevated() => ProcessToken.IsElevated();

    private static partial class ProcessToken
    {
        private const uint TokenQuery = 0x0008;
        private const int TokenElevationClass = 20;

        internal static unsafe bool IsElevated()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out nint token))
            {
                return false;
            }

            try
            {
                uint elevated = 0;
                if (!GetTokenInformation(token, TokenElevationClass, &elevated, sizeof(uint), out _))
                {
                    return false;
                }

                return elevated != 0;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }

        [LibraryImport("kernel32.dll")]
        private static partial nint GetCurrentProcess();

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool GetTokenInformation(
            nint token,
            int informationClass,
            uint* information,
            int informationLength,
            out int returnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
