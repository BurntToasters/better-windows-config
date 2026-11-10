using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Bwc.Core;

/// <summary>
/// A per-tool, per-user, per-integrity-level single-instance gate. The first instance owns a
/// mutex and serves activation messages over a named pipe; later instances only send.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int ConnectTimeoutMilliseconds = 5000;
    private const uint TokenQueryAccess = 0x0008;
    private const int TokenIntegrityLevelClass = 25;

    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly int _ownerThreadId;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _serverGate = new();
    private readonly Task? _acceptTask;

    private NamedPipeServerStream? _activeServer;
    private int _disposed;

    private SingleInstance(string instanceName, string pipeName, Mutex mutex, bool ownsMutex)
    {
        InstanceName = instanceName;
        _pipeName = pipeName;
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _ownerThreadId = Environment.CurrentManagedThreadId;

        if (ownsMutex)
        {
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
        }
    }

    public event Action<string>? MessageReceived;

    public bool IsFirstInstance => _ownsMutex;

    public string InstanceName { get; }

    public static SingleInstance Create(string toolId)
    {
        AppPaths.ValidateToolId(toolId);

        string suffix = BuildIdentitySuffix();
        string instanceName = @"Local\Bwc." + toolId + "." + suffix;
        string pipeName = "Bwc." + toolId + "." + suffix;

        var mutex = new Mutex(initiallyOwned: true, instanceName, out bool createdNew);
        try
        {
            return new SingleInstance(instanceName, pipeName, mutex, createdNew);
        }
        catch
        {
            if (createdNew)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The mutex was already released or is not owned by this thread.
                }
            }

            mutex.Dispose();
            throw;
        }
    }

    public bool SendMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > MaxMessageBytes)
        {
            throw new ArgumentException(
                "Messages may not exceed " + MaxMessageBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes.",
                nameof(message));
        }

        byte[] payload = Encoding.UTF8.GetBytes(message);
        if (payload.Length > MaxMessageBytes)
        {
            throw new ArgumentException(
                "Messages may not exceed " + MaxMessageBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes.",
                nameof(message));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        byte[] packet = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
        payload.CopyTo(packet, 4);

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect(ConnectTimeoutMilliseconds);
            client.Write(packet, 0, packet.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
            or ObjectDisposedException or InvalidOperationException or System.Security.SecurityException)
        {
            Trace.WriteLine("SingleInstance could not send a message: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("SingleInstance cancellation failed: " + ex.Message);
        }

        NamedPipeServerStream? active;
        lock (_serverGate)
        {
            active = _activeServer;
            _activeServer = null;
        }

        if (active is not null)
        {
            try
            {
                active.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SingleInstance server dispose failed: " + ex.Message);
            }
        }

        if (_acceptTask is not null)
        {
            try
            {
                _acceptTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SingleInstance accept loop did not stop cleanly: " + ex.Message);
            }
        }

        try
        {
            if (_ownsMutex && _ownerThreadId == Environment.CurrentManagedThreadId)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    Trace.WriteLine("SingleInstance mutex release skipped: " + ex.Message);
                }
            }

            _mutex.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("SingleInstance mutex dispose failed: " + ex.Message);
        }

        try
        {
            _cancellation.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("SingleInstance cancellation dispose failed: " + ex.Message);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                lock (_serverGate)
                {
                    _activeServer = server;
                }

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                string? message = await ReadMessageAsync(server, token).ConfigureAwait(false);
                server.Disconnect();

                if (message is not null)
                {
                    RaiseMessage(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SingleInstance pipe server error: " + ex.Message);
            }
            finally
            {
                lock (_serverGate)
                {
                    if (ReferenceEquals(_activeServer, server))
                    {
                        _activeServer = null;
                    }
                }

                server?.Dispose();
            }
        }
    }

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        if (!await TryReadExactlyAsync(stream, header, token).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new ArgumentException(
                "Pipe message length " + length.ToString(CultureInfo.InvariantCulture) + " is outside the allowed range.");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        byte[] payload = new byte[length];
        if (!await TryReadExactlyAsync(stream, payload, token).ConfigureAwait(false))
        {
            return null;
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private void RaiseMessage(string message)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                MessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SingleInstance message handler failed: " + ex);
            }
        });
    }

    private static string BuildIdentitySuffix()
    {
        string sid = GetUserSid();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        string sidHash = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        return sidHash + "." + GetIntegrityLevel();
    }

    private static string GetUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? identity.Name;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("SingleInstance could not read the user SID: " + ex.Message);
            return Environment.UserName;
        }
    }

    private static string GetIntegrityLevel()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQueryAccess, out token))
            {
                return "unknown";
            }

            _ = GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return "unknown";
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, length, out _))
            {
                return "unknown";
            }

            // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field is the SID.
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return "unknown";
            }

            byte subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0)
            {
                return "unknown";
            }

            int offset = 8 + ((subAuthorityCount - 1) * 4);
            int rid = Marshal.ReadInt32(sid, offset);
            return rid switch
            {
                >= 0x4000 => "system",
                >= 0x3000 => "high",
                >= 0x2000 => "medium",
                >= 0x1000 => "low",
                _ => "unknown",
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine("SingleInstance could not read the integrity level: " + ex.Message);
            return "unknown";
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                _ = CloseHandle(token);
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
