using System.Net;

namespace Bwc.Update;

/// <summary>Which download a failure belongs to. It picks the reason code the caller sees.</summary>
internal enum DownloadStage
{
    Manifest,
    ManifestSignature,
    Installer,
}

/// <summary>
/// Downloads with size caps, a per-read stall limit, an overall limit, and manual redirects. Every
/// redirect target is checked by <see cref="UrlPolicy"/> before it is followed.
/// </summary>
internal sealed class UpdateDownloader : IDisposable
{
    private const int BufferSize = 80 * 1024;

    private readonly UpdateOptions _options;
    private readonly HttpClient _client;

    public UpdateDownloader(UpdateOptions options)
    {
        _options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Reads a small body into memory. More than <paramref name="maxBytes"/> is refused as it arrives.</summary>
    public async Task<Outcome<byte[]>> ReadAllAsync(Uri url, DownloadStage stage, long maxBytes, CancellationToken ct)
    {
        var overall = stage == DownloadStage.Installer ? _options.InstallerDownloadTimeout : _options.ManifestTimeout;
        using var deadline = new Deadline(ct, overall, _options.StallTimeout);
        var opened = await OpenAsync(url, stage, deadline);
        if (!opened.Ok)
        {
            return Outcome<byte[]>.Fail(opened.Failure, opened.Detail);
        }

        using var response = opened.Value!;
        using var collected = new MemoryStream();
        Stream body;
        try
        {
            body = await response.Content.ReadAsStreamAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return deadline.Classify<byte[]>();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return Outcome<byte[]>.Fail(UpdateFailure.DownloadFailed, "connection ended before the body arrived");
        }

        await using (body)
        {
            var chunk = new byte[BufferSize];
            while (true)
            {
                var read = await ReadChunkAsync(body, chunk, deadline, UpdateFailure.DownloadFailed);
                if (!read.Ok)
                {
                    return Outcome<byte[]>.Fail(read.Failure, read.Detail);
                }

                if (read.Value == 0)
                {
                    break;
                }

                if (collected.Length + read.Value > maxBytes)
                {
                    return Outcome<byte[]>.Fail(OverflowFailure(stage), "download is larger than the limit");
                }

                collected.Write(chunk, 0, read.Value);
            }
        }

        return Outcome<byte[]>.Success(collected.ToArray());
    }

    /// <summary>
    /// Streams an installer to a new file. The file must not exist yet. It is written with no
    /// sharing, so no other process can open it while it is written. The caller deletes the file on failure.
    /// <paramref name="progress"/> gets the bytes written so far after each chunk.
    /// </summary>
    public async Task<Outcome<long>> DownloadToFileAsync(
        Uri url,
        long expectedSize,
        string path,
        CancellationToken ct,
        IProgress<DownloadProgress>? progress = null)
    {
        using var deadline = new Deadline(ct, _options.InstallerDownloadTimeout, _options.StallTimeout);
        var opened = await OpenAsync(url, DownloadStage.Installer, deadline);
        if (!opened.Ok)
        {
            return Outcome<long>.Fail(opened.Failure, opened.Detail);
        }

        using var response = opened.Value!;
        Stream body;
        try
        {
            body = await response.Content.ReadAsStreamAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return deadline.Classify<long>();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return Outcome<long>.Fail(UpdateFailure.InstallerDownloadInterrupted, "connection ended before the body arrived");
        }

        await using (body)
        {
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Outcome<long>.Fail(UpdateFailure.Io, $"cannot create the installer file: {ex.GetType().Name}");
            }

            await using (file)
            {
                long total = 0;
                var chunk = new byte[BufferSize];
                while (true)
                {
                    var read = await ReadChunkAsync(body, chunk, deadline, UpdateFailure.InstallerDownloadInterrupted);
                    if (!read.Ok)
                    {
                        return Outcome<long>.Fail(read.Failure, read.Detail);
                    }

                    if (read.Value == 0)
                    {
                        break;
                    }

                    total += read.Value;
                    if (total > expectedSize)
                    {
                        return Outcome<long>.Fail(UpdateFailure.InstallerTooLarge, "installer is larger than the manifest size");
                    }

                    try
                    {
                        await file.WriteAsync(chunk.AsMemory(0, read.Value), deadline.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return deadline.Classify<long>();
                    }
                    catch (IOException ex)
                    {
                        return Outcome<long>.Fail(UpdateFailure.Io, $"cannot write the installer: {ex.GetType().Name}");
                    }

                    progress?.Report(new DownloadProgress(total, expectedSize));
                }

                if (total != expectedSize)
                {
                    return Outcome<long>.Fail(
                        UpdateFailure.InstallerSizeMismatch,
                        $"installer is {total} bytes, the manifest says {expectedSize}");
                }

                try
                {
                    await file.FlushAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    return deadline.Classify<long>();
                }
                catch (IOException ex)
                {
                    return Outcome<long>.Fail(UpdateFailure.Io, $"cannot flush the installer: {ex.GetType().Name}");
                }

                return Outcome<long>.Success(total);
            }
        }
    }

    // Follows up to RedirectLimit redirects. Each hop is checked before it is requested.
    private async Task<Outcome<HttpResponseMessage>> OpenAsync(Uri url, DownloadStage stage, Deadline deadline)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            try
            {
                deadline.ArmStall();
                response = await _client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, current),
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return deadline.Classify<HttpResponseMessage>();
            }
            catch (HttpRequestException ex)
            {
                return Outcome<HttpResponseMessage>.Fail(
                    UpdateFailure.DownloadFailed,
                    $"could not connect: {ex.HttpRequestError}");
            }

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location?.OriginalString;
                response.Dispose();
                if (hop >= UrlPolicy.RedirectLimit)
                {
                    return Outcome<HttpResponseMessage>.Fail(UpdateFailure.DownloadFailed, "too many redirects");
                }

                if (location is null)
                {
                    return Outcome<HttpResponseMessage>.Fail(UpdateFailure.DownloadFailed, "redirect has no location");
                }

                var next = UrlPolicy.ValidateRedirect(current, location, _options.RequireHttps);
                if (!next.Ok)
                {
                    return Outcome<HttpResponseMessage>.Fail(UpdateFailure.UrlRejected, "redirect: " + next.Detail);
                }

                current = next.Value!;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                response.Dispose();
                return Outcome<HttpResponseMessage>.Fail(HttpFailure(stage), $"server answered HTTP {code}");
            }

            return Outcome<HttpResponseMessage>.Success(response);
        }
    }

    private static async Task<Outcome<int>> ReadChunkAsync(Stream body, byte[] chunk, Deadline deadline, UpdateFailure interrupted)
    {
        try
        {
            deadline.ArmStall();
            var count = await body.ReadAsync(chunk.AsMemory(), deadline.Token);
            return Outcome<int>.Success(count);
        }
        catch (OperationCanceledException)
        {
            return deadline.Classify<int>();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return Outcome<int>.Fail(interrupted, "connection ended before the download finished");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static UpdateFailure HttpFailure(DownloadStage stage) => stage switch
    {
        DownloadStage.Manifest => UpdateFailure.ManifestDownload,
        DownloadStage.ManifestSignature => UpdateFailure.ManifestSignatureDownload,
        _ => UpdateFailure.InstallerDownload,
    };

    private static UpdateFailure OverflowFailure(DownloadStage stage) => stage switch
    {
        DownloadStage.Manifest => UpdateFailure.ManifestTooLarge,
        DownloadStage.ManifestSignature => UpdateFailure.ManifestSignatureInvalid,
        _ => UpdateFailure.InstallerTooLarge,
    };

    /// <summary>
    /// Two limits: the overall limit covers the whole download, and the stall limit restarts on every
    /// read. Cancellation by the caller is kept apart from both, so it is reported as Canceled.
    /// </summary>
    private sealed class Deadline : IDisposable
    {
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _overall;
        private readonly CancellationTokenSource _read;
        private readonly TimeSpan _stall;

        public Deadline(CancellationToken caller, TimeSpan overall, TimeSpan stall)
        {
            _caller = caller;
            _stall = stall;
            _overall = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _overall.CancelAfter(overall);
            _read = CancellationTokenSource.CreateLinkedTokenSource(_overall.Token);
        }

        public CancellationToken Token => _read.Token;

        public void ArmStall() => _read.CancelAfter(_stall);

        public Outcome<T> Classify<T>()
        {
            if (_caller.IsCancellationRequested)
            {
                return Outcome<T>.Fail(UpdateFailure.Canceled, "update check was canceled");
            }

            if (_overall.IsCancellationRequested)
            {
                return Outcome<T>.Fail(UpdateFailure.Timeout, "download exceeded its overall time limit");
            }

            return Outcome<T>.Fail(UpdateFailure.Timeout, "download stalled: no data arrived within the stall limit");
        }

        public void Dispose()
        {
            _read.Dispose();
            _overall.Dispose();
        }
    }
}
