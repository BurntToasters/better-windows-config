using System.ComponentModel;

namespace Bwc.Registry;

/// <summary>
/// A failed registry operation with the Win32 code, operation name, and full path attached.
/// </summary>
public sealed class RegistryOperationException : Exception
{
    public RegistryOperationException(
        string operation,
        RegistryPath path,
        int win32Error,
        string? additionalDetail = null)
        : base(BuildMessage(operation, path, win32Error, additionalDetail))
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(path);

        Win32Error = win32Error;
        Operation = operation;
        Path = path;

        if (win32Error is >= 0 and <= 0xFFFF)
        {
            HResult = unchecked((int)(0x80070000u | (uint)win32Error));
        }
    }

    public int Win32Error { get; }

    public string Operation { get; }

    public RegistryPath Path { get; }

    private static string BuildMessage(
        string operation,
        RegistryPath path,
        int win32Error,
        string? additionalDetail)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(path);

        string systemMessage = new Win32Exception(win32Error).Message;
        string message =
            $"{operation} failed for {path.ToDisplayString()} with Win32 error {win32Error}: {systemMessage}";

        return string.IsNullOrEmpty(additionalDetail)
            ? message
            : $"{message} {additionalDetail}";
    }
}
