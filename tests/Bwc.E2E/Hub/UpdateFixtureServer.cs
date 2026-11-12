using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bwc.E2E.Hub;

/// <summary>
/// A loopback HTTP/1.1 server for the update fixture. It serves one manifest at a time, so a test
/// can offer a newer or an equal version between checks, and it can delay the manifest to hold the
/// progress window open. It listens only on 127.0.0.1 and runs inside the test process.
/// </summary>
internal sealed class UpdateFixtureServer : IDisposable
{
    private const string ManifestPath = "/manifest.json";
    private const string SignaturePath = "/manifest.json.minisig";
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
    private readonly Task _accepting;
    private volatile int _manifestDelayMilliseconds;
    private volatile TaskCompletionSource? _manifestHold;

    public UpdateFixtureServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => "http://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The URL the Hub is pointed at. The signature URL is this URL with .minisig appended.</summary>
    public string ManifestUrl => BaseUrl + ManifestPath;

    /// <summary>How long the manifest response waits. Zero serves at once.</summary>
    public int ManifestDelayMilliseconds
    {
        get => _manifestDelayMilliseconds;
        set => _manifestDelayMilliseconds = value;
    }

    /// <summary>
    /// Holds every manifest response until <see cref="ReleaseManifest"/> is called, or until the hold limit
    /// (60 seconds) passes, so a test controls when the check can finish. The hold is for the progress window,
    /// which closes when the check ends.
    /// </summary>
    public void HoldManifest()
    {
        _manifestHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets the held manifest responses go out now, and later ones too, until the next hold.</summary>
    public void ReleaseManifest()
    {
        _manifestHold?.TrySetResult();
    }

    /// <summary>Serves a manifest and its signature for the next checks, and the installer files.</summary>
    public void Offer(byte[] manifest, byte[] signature, byte[] installer)
    {
        _routes[ManifestPath] = manifest;
        _routes[SignaturePath] = signature;
        _routes["/installer-win-x64.exe"] = installer;
        _routes["/installer-win-arm64.exe"] = installer;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // The listener is already stopped.
        }

        try
        {
            _accepting.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends with a cancellation when the listener stops.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                string? requestLine = await ReadLineAsync(stream);
                string? line;
                do
                {
                    line = await ReadLineAsync(stream);
                }
                while (!string.IsNullOrEmpty(line));

                string target = requestLine?.Split(' ') is [_, var path, ..] ? path : string.Empty;
                int query = target.IndexOf('?', StringComparison.Ordinal);
                if (query >= 0)
                {
                    target = target[..query];
                }

                if (target == ManifestPath && ManifestDelayMilliseconds > 0)
                {
                    await Task.Delay(ManifestDelayMilliseconds, _stop.Token);
                }

                if (target == ManifestPath)
                {
                    await WaitForReleaseAsync();
                }

                if (_routes.TryGetValue(target, out byte[]? body))
                {
                    await WriteResponseAsync(stream, "200 OK", body);
                }
                else
                {
                    await WriteResponseAsync(stream, "404 Not Found", Encoding.ASCII.GetBytes("not found"));
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client closed the connection, for example when a check is canceled.
            }
        }
    }

    /// <summary>Waits for the test to release a held manifest, until the hold limit or the server stopping.</summary>
    private async Task WaitForReleaseAsync()
    {
        TaskCompletionSource? hold = _manifestHold;
        if (hold is null)
        {
            return;
        }

        _ = await Task.WhenAny(hold.Task, Task.Delay(HoldLimit, _stop.Token));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, byte[] body)
    {
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (one[0] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            bytes.Add(one[0]);
            if (bytes.Count > 8192)
            {
                return null;
            }
        }
    }
}
