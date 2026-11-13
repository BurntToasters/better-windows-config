using System.ComponentModel;

namespace Bwc.Registry;

/// <summary>
/// Why a remote registry connection was rejected or failed.
/// </summary>
public enum RemoteRegistryFailureReason
{
    /// <summary>The requested name resolves to the computer that runs this process.</summary>
    OwnComputer,

    /// <summary>The first root connection failed, so no handle was left open.</summary>
    ConnectFailed,

    /// <summary>The second root connection failed after the first one succeeded.</summary>
    PartialConnection,
}

/// <summary>
/// A failed remote registry connection with the computer name, operation, Win32 code, and
/// failure reason attached.
/// </summary>
public sealed class RemoteRegistryException : Exception
{
    public RemoteRegistryException(
        string computerName,
        string operation,
        int win32Error,
        RemoteRegistryFailureReason reason)
        : base(BuildMessage(computerName, operation, win32Error, reason))
    {
        ArgumentException.ThrowIfNullOrEmpty(computerName);
        ArgumentException.ThrowIfNullOrEmpty(operation);

        ComputerName = computerName;
        Operation = operation;
        Win32Error = win32Error;
        Reason = reason;

        if (win32Error is >= 0 and <= 0xFFFF)
        {
            HResult = unchecked((int)(0x80070000u | (uint)win32Error));
        }
    }

    public string ComputerName { get; }

    public string Operation { get; }

    public int Win32Error { get; }

    public RemoteRegistryFailureReason Reason { get; }

    private static string BuildMessage(
        string computerName,
        string operation,
        int win32Error,
        RemoteRegistryFailureReason reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(computerName);
        ArgumentException.ThrowIfNullOrEmpty(operation);

        if (reason == RemoteRegistryFailureReason.OwnComputer)
        {
            string rejection =
                $"Remote registry connection to '{computerName}' was rejected during {operation}: you cannot connect to your own computer.";

            if (win32Error == 0)
            {
                return rejection;
            }

            string rejectionDetail = new Win32Exception(win32Error).Message;
            return $"{rejection} Win32 error {win32Error}: {rejectionDetail}";
        }

        string message = $"Remote registry connection to '{computerName}' failed during {operation}";

        if (win32Error == 0)
        {
            return message + ".";
        }

        string systemMessage = new Win32Exception(win32Error).Message;
        message = $"{message} with Win32 error {win32Error}: {systemMessage}";

        return reason == RemoteRegistryFailureReason.PartialConnection
            ? $"{message} The previous root connection was closed."
            : message;
    }
}
