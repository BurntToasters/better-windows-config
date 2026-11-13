using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.Update;

/// <summary>What the operating system says about one process's image.</summary>
public enum ImageStatus
{
    /// <summary>No such process, or it has exited.</summary>
    Exited,

    /// <summary>The process is alive and its image path was read.</summary>
    Readable,

    /// <summary>The process is alive but its image path cannot be read (access denied or another failure).</summary>
    Unreadable,
}

/// <summary>
/// The one rule for "is this process running from that folder". The Hub's pre-check, the helper, and
/// the installer's --close-running command all use it. It fails closed: a live process whose image
/// cannot be read counts as running. The image is read with QueryFullProcessImageName and
/// PROCESS_QUERY_LIMITED_INFORMATION. The folder match needs the full folder with a trailing separator,
/// compared without regard to case, so "C:\BWC2" is not "C:\BWC".
///
/// This file is compiled into the Hub too (a source link), so it has no other dependency in this project.
/// </summary>
public static class RunningImageRule
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const int ErrorInvalidParameter = 87;
    private const int MaxImagePath = 32768;

    /// <summary>The decision, from an image status that has already been read.</summary>
    public static bool Decide(ImageStatus status, string? imagePath, string installDir)
    {
        ArgumentNullException.ThrowIfNull(installDir);
        return status switch
        {
            ImageStatus.Exited => false,
            ImageStatus.Unreadable => true,
            _ => imagePath is not null && IsUnderDirectory(imagePath, installDir),
        };
    }

    /// <summary>True when <paramref name="imagePath"/> is inside <paramref name="directory"/> (at any depth).</summary>
    public static bool IsUnderDirectory(string imagePath, string directory)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(directory);

        string image;
        string folder;
        try
        {
            image = Path.GetFullPath(imagePath);
            folder = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that cannot be normalized may still be inside the folder, so it counts as inside.
            return true;
        }

        if (!folder.EndsWith(Path.DirectorySeparatorChar))
        {
            folder += Path.DirectorySeparatorChar;
        }

        return image.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the status and, when readable, the full image path of a process.</summary>
    public static ImageStatus ReadImage(int pid, out string? imagePath)
    {
        imagePath = null;
        if (pid <= 0)
        {
            return ImageStatus.Exited;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? ImageStatus.Exited : ImageStatus.Unreadable;
        }

        try
        {
            if (WaitForSingleObject(handle, 0) == WaitObject0)
            {
                return ImageStatus.Exited;
            }

            var buffer = new StringBuilder(MaxImagePath);
            var size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return ImageStatus.Unreadable;
            }

            imagePath = buffer.ToString(0, (int)size);
            return ImageStatus.Readable;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>
    /// True when the process is alive and its image is inside <paramref name="installDir"/>, or when
    /// its image cannot be read.
    /// </summary>
    public static bool IsRunningFrom(int pid, string installDir)
    {
        var status = ReadImage(pid, out var imagePath);
        return Decide(status, imagePath, installDir);
    }

#pragma warning disable SYSLIB1054 // DllImport keeps these signatures plain; the StringBuilder and ref uint are marshalled directly.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);
#pragma warning restore SYSLIB1054
}
