using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bwc.Update.Tests;

/// <summary>
/// How a route answers. Defaults send the whole body and close cleanly.
/// StopAfter cuts the body short; Pause then holds the socket open before closing (a stall).
/// TrickleDelay sends one body byte at a time with a pause between bytes (a slow download).
/// </summary>
internal sealed record Route(
    int Status = 200,
    byte[]? Body = null,
    long? DeclaredLength = null,
    int? StopAfter = null,
    TimeSpan? Pause = null,
    string? Location = null,
    TimeSpan? TrickleDelay = null);

/// <summary>Minimal HTTP/1.1 responder on 127.0.0.1 for scripted failures. Needs no URL ACL.</summary>
internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public LoopbackServer()
    {
        _listener.Start();
        _accepting = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

    public void Map(string path, Route route) => _routes[path] = route;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var path = await ReadRequestTargetAsync(stream);
                var route = _routes.TryGetValue(path, out var found)
                    ? found
                    : new Route(404, Encoding.ASCII.GetBytes("not found"));
                await WriteResponseAsync(stream, route);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client gave up. Nothing to report from the server side.
            }
        }
    }

    private static async Task<string> ReadRequestTargetAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length));
            if (read == 0)
            {
                throw new IOException("client closed before the request ended");
            }

            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var requestLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
                return requestLine.Split(' ')[1];
            }

            if (length == buffer.Length)
            {
                throw new IOException("request headers too large");
            }
        }
    }

    private async Task WriteResponseAsync(NetworkStream stream, Route route)
    {
        var body = route.Body ?? [];
        var declared = route.DeclaredLength ?? (route.Status == 302 ? 0 : body.Length);
        var reason = route.Status switch
        {
            200 => "OK",
            302 => "Found",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "Status",
        };
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {route.Status} {reason}\r\n")
            .Append("Connection: close\r\n")
            .Append($"Content-Length: {declared}\r\n");
        if (route.Location is not null)
        {
            head.Append($"Location: {route.Location}\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token);
        if (route.Status == 302)
        {
            return;
        }

        if (route.TrickleDelay is { } delay)
        {
            foreach (var value in body)
            {
                await stream.WriteAsync(new[] { value }, _stop.Token);
                await stream.FlushAsync(_stop.Token);
                await Task.Delay(delay, _stop.Token);
            }

            return;
        }

        var count = route.StopAfter is { } stop ? Math.Min(stop, body.Length) : body.Length;
        await stream.WriteAsync(body.AsMemory(0, count), _stop.Token);
        await stream.FlushAsync(_stop.Token);
        if (route.Pause is { } pause)
        {
            await Task.Delay(pause, _stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
            // Expected when the listener stops.
        }

        _stop.Dispose();
    }
}
