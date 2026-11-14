using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Bwc.E2E.Installer;

/// <summary>
/// A loopback HTTP/1.1 server for the installer update. It serves the manifest and its signature. The installer is
/// served only after a 302 from /dl/ to /objects/ with a long GitHub-shaped query (escaped %2F and %3D), the shape
/// that broke 0.1.0. It counts each redirect and checks that the query comes back byte for byte.
/// </summary>
internal sealed class RedirectFeedServer : IDisposable
{
    private const string ManifestPath = "/manifest.json";
    private const string SignaturePath = "/manifest.json.minisig";
    private const string DownloadPrefix = "/dl/";
    private const string ObjectPrefix = "/objects/3f9c1ab24e/";
    private const string Credential = "AKIAEXAMPLETESTONLY/20261113/us-east-1/s3/aws4_request";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly byte[] _installer;
    private readonly string _fileName;
    private readonly string _expectedQuery;
    private volatile byte[]? _manifest;
    private volatile byte[]? _signature;
    private int _redirects;
    private int _downloads;
    private int _mismatches;

    public RedirectFeedServer(byte[] installer, string fileName)
    {
        _installer = installer;
        _fileName = fileName;
        _expectedQuery = BuildQuery(fileName);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The URL the Hub is pointed at for the manifest. The signature is at this URL with .minisig appended.</summary>
    public string ManifestUrl => BaseUrl + ManifestPath;

    /// <summary>The installer URL the manifest names. It answers with a redirect, not the installer.</summary>
    public string InstallerUrl => BaseUrl + DownloadPrefix + _fileName;

    /// <summary>How many times the redirect was sent.</summary>
    public int Redirects => Volatile.Read(ref _redirects);

    /// <summary>How many installer requests arrived with exactly the query the redirect sent.</summary>
    public int Downloads => Volatile.Read(ref _downloads);

    /// <summary>How many installer requests arrived with a different query.</summary>
    public int Mismatches => Volatile.Read(ref _mismatches);

    /// <summary>True when the installer was requested after a redirect and every request kept the query.</summary>
    public bool QueryIntact => Downloads > 0 && Mismatches == 0;

    /// <summary>Serves the manifest and its signature for the next checks.</summary>
    public void Offer(byte[] manifest, byte[] signature)
    {
        _manifest = manifest;
        _signature = signature;
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

    /// <summary>
    /// The long query the redirect carries. The credential and the file name are escaped as a presigned URL would
    /// escape them, so the query holds %2F and %3D, and the token ends with base64 padding.
    /// </summary>
    private static string BuildQuery(string fileName)
    {
        string signature = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(35));
        return "X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + "&X-Amz-Credential=" + Uri.EscapeDataString(Credential)
            + "&X-Amz-Date=20261113T000000Z&X-Amz-Expires=300"
            + "&X-Amz-Signature=" + signature
            + "&X-Amz-SignedHeaders=host"
            + "&response-content-disposition=" + Uri.EscapeDataString("attachment; filename=" + fileName)
            + "&response-content-type=application%2Foctet-stream"
            + "&token=" + Uri.EscapeDataString(token);
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

                string target = requestLine?.Split(' ') is [_, var raw, ..] ? raw : string.Empty;
                int question = target.IndexOf('?', StringComparison.Ordinal);
                string path = question >= 0 ? target[..question] : target;
                string query = question >= 0 ? target[(question + 1)..] : string.Empty;
                await RespondAsync(stream, path, query);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client closed the connection, for example when the Hub stops mid-download.
            }
        }
    }

    private async Task RespondAsync(NetworkStream stream, string path, string query)
    {
        byte[]? manifest = _manifest;
        byte[]? signature = _signature;
        if (path == ManifestPath && manifest is not null)
        {
            await WriteAsync(stream, "200 OK", manifest, location: null);
        }
        else if (path == SignaturePath && signature is not null)
        {
            await WriteAsync(stream, "200 OK", signature, location: null);
        }
        else if (path == DownloadPrefix + _fileName)
        {
            Interlocked.Increment(ref _redirects);
            await WriteAsync(stream, "302 Found", [], BaseUrl + ObjectPrefix + _fileName + "?" + _expectedQuery);
        }
        else if (path == ObjectPrefix + _fileName)
        {
            if (query == _expectedQuery)
            {
                Interlocked.Increment(ref _downloads);
                await WriteAsync(stream, "200 OK", _installer, location: null);
            }
            else
            {
                Interlocked.Increment(ref _mismatches);
                await WriteAsync(stream, "403 Forbidden", Encoding.ASCII.GetBytes("query changed"), location: null);
            }
        }
        else
        {
            await WriteAsync(stream, "404 Not Found", Encoding.ASCII.GetBytes("not found"), location: null);
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string status, byte[] body, string? location)
    {
        string header =
            "HTTP/1.1 " + status + "\r\n" +
            (location is null ? string.Empty : "Location: " + location + "\r\n") +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
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
