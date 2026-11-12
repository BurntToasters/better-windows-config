using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Bwc.Core;

/// <summary>Describes how a message send ended. Only <see cref="Sent"/> means delivery.</summary>
public enum SingleInstanceSendResult
{
    /// <summary>The server accepted and read the complete message.</summary>
    Sent,

    /// <summary>No server is listening, the pipe is busy, or the pipe broke.</summary>
    ServerUnavailable,

    /// <summary>The connect or write did not finish before the timeout.</summary>
    TimedOut,

    /// <summary>The pipe ACL rejected this process.</summary>
    AccessDenied,

    /// <summary>This instance object has already been disposed.</summary>
    Disposed,

    /// <summary>The pipe could not be opened for another reason.</summary>
    Failed,
}

/// <summary>
/// A per-tool, per-user, per-integrity-level single-instance gate. The first instance owns a
/// mutex and serves activation messages over a named pipe; later instances only send.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int ConnectTimeoutMilliseconds = 3000;
    private const int ServerReadTimeoutMilliseconds = 1500;
    private const int AcceptRetryInitialDelayMilliseconds = 50;
    private const int AcceptRetryMaximumDelayMilliseconds = 1000;
    private const int AbnormalClientDelayMilliseconds = 25;
    private const string LocalServerName = ".";
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
            ObserveFaults(_acceptTask);
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

    /// <summary>
    /// Sends one message to the first instance and reports a named result. The client only
    /// targets the local machine with the bare per-user pipe name. It never tries the global
    /// pipe namespace, a remote server, or an impersonation token.
    /// </summary>
    public SingleInstanceSendResult TrySendMessage(string message)
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
            return SingleInstanceSendResult.Disposed;
        }

        byte[] packet = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
        payload.CopyTo(packet, 4);

        try
        {
            using var client = new NamedPipeClientStream(
                LocalServerName,
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect(ConnectTimeoutMilliseconds);
            client.Write(packet, 0, packet.Length);
            client.Flush();
            return SingleInstanceSendResult.Sent;
        }
        catch (TimeoutException ex)
        {
            SafeTrace.WriteLine("SingleInstance send timed out: " + ex.Message);
            return SingleInstanceSendResult.TimedOut;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            SafeTrace.WriteLine("SingleInstance send was denied: " + ex.Message);
            return SingleInstanceSendResult.AccessDenied;
        }
        catch (ObjectDisposedException ex)
        {
            SafeTrace.WriteLine("SingleInstance send used a disposed pipe: " + ex.Message);
            return SingleInstanceSendResult.Disposed;
        }
        catch (IOException ex)
        {
            SafeTrace.WriteLine("SingleInstance server is unavailable: " + ex.Message);
            return SingleInstanceSendResult.ServerUnavailable;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            SafeTrace.WriteLine("SingleInstance could not send a message: " + ex.Message);
            return SingleInstanceSendResult.Failed;
        }
    }

    /// <summary>
    /// Sends one message and returns true only when the first instance received it. Prefer
    /// <see cref="TrySendMessage"/> when the caller needs to distinguish failure reasons.
    /// </summary>
    public bool SendMessage(string message)
    {
        return TrySendMessage(message) == SingleInstanceSendResult.Sent;
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
            SafeTrace.WriteLine("SingleInstance cancellation failed: " + ex.Message);
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
                SafeTrace.WriteLine("SingleInstance server dispose failed: " + ex.Message);
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
                SafeTrace.WriteLine("SingleInstance accept loop did not stop cleanly: " + ex.Message);
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
                    SafeTrace.WriteLine("SingleInstance mutex release skipped: " + ex.Message);
                }
            }

            _mutex.Dispose();
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("SingleInstance mutex dispose failed: " + ex.Message);
        }

        try
        {
            _cancellation.Dispose();
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("SingleInstance cancellation dispose failed: " + ex.Message);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        int retryDelay = AcceptRetryInitialDelayMilliseconds;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            bool connectionAccepted = false;
            bool messageCompleted = false;
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
                connectionAccepted = true;
                retryDelay = AcceptRetryInitialDelayMilliseconds;

                Task<string?> readTask = ReadMessageAsync(server, token);
                ObserveFaults(readTask);
                string? message = await readTask
                    .WaitAsync(TimeSpan.FromMilliseconds(ServerReadTimeoutMilliseconds), token)
                    .ConfigureAwait(false);

                if (message is not null)
                {
                    RaiseMessage(message);
                    messageCompleted = true;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException)
            {
                SafeTrace.WriteLine(
                    "SingleInstance disconnected a client that did not send a complete message within " +
                    ServerReadTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SafeTrace.WriteLine("SingleInstance pipe server error: " + ex.Message);
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

                if (server is not null)
                {
                    try
                    {
                        server.Dispose();
                    }
                    catch (Exception ex)
                    {
                        SafeTrace.WriteLine("SingleInstance server cleanup failed: " + ex.Message);
                    }
                }
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (connectionAccepted && messageCompleted)
            {
                continue;
            }

            if (connectionAccepted)
            {
                // A client connected but did not deliver a complete message. Do not let a fast
                // connect and disconnect stream drive an unbounded tight loop.
                if (!await DelayAsync(AbnormalClientDelayMilliseconds, token).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            // Creating the server failed, most often because another process owns the pipe name.
            // Back off so the loop cannot saturate a core while the name is unavailable.
            if (!await DelayAsync(retryDelay, token).ConfigureAwait(false))
            {
                break;
            }

            retryDelay = Math.Min(retryDelay * 2, AcceptRetryMaximumDelayMilliseconds);
        }
    }

    private static void ObserveFaults(Task task)
    {
        // A timed-out read is abandoned after the server is disposed. Observe its eventual
        // fault so TaskScheduler.UnobservedTaskException cannot turn cleanup into a report.
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<bool> DelayAsync(int milliseconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(milliseconds, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
                SafeTrace.WriteLine("SingleInstance message handler failed: " + ex);
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
            SafeTrace.WriteLine("SingleInstance could not read the user SID: " + ex.Message);
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
            SafeTrace.WriteLine("SingleInstance could not read the integrity level: " + ex.Message);
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
