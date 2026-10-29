//MIT License

//Copyright (c) 2026 Dimon

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.

using HostlistDownloader.Config;
using HostlistDownloader.Core;
using HostlistDownloader.Diagnostics;
using HostlistDownloader.IO;
using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HostlistDownloader.Network;

/// <summary>
/// Outcome of downloading one source, including retries. Permanent and transient failures are separated so a 404
/// is not retried forever, while a timeout or 503 may succeed on the next run.
/// </summary>
internal enum DownloadOutcome
{
    Success,
    SuccessWithoutValidator,
    SkippedUpToDate,
    TransientFailure,
    PermanentFailure,
    Cancelled,
    BlockedByConfig,
    ContentRejected,
    SecurityViolation,
    InvalidUrl,
    Disabled
}

internal static class DownloadOutcomeExtensions
{
    internal static bool IsSuccess(this DownloadOutcome outcome) =>
        outcome is DownloadOutcome.Success or DownloadOutcome.SuccessWithoutValidator;

    internal static bool IsProblem(this DownloadOutcome outcome) =>
        outcome is DownloadOutcome.TransientFailure or DownloadOutcome.PermanentFailure or DownloadOutcome.BlockedByConfig
            or DownloadOutcome.ContentRejected or DownloadOutcome.SecurityViolation or DownloadOutcome.InvalidUrl;

    internal static string Describe(this DownloadOutcome outcome) => outcome switch
    {
        DownloadOutcome.Success => "Downloaded",
        DownloadOutcome.SuccessWithoutValidator => "Downloaded (no ETag/Last-Modified)",
        DownloadOutcome.SkippedUpToDate => "Already up to date",
        DownloadOutcome.TransientFailure => "Failed after retries",
        DownloadOutcome.PermanentFailure => "Permanently unreachable",
        DownloadOutcome.Cancelled => "Cancelled",
        DownloadOutcome.BlockedByConfig => "Blocked by settings",
        DownloadOutcome.ContentRejected => "Content rejected",
        DownloadOutcome.SecurityViolation => "Blocked (internal address)",
        DownloadOutcome.InvalidUrl => "Invalid URL",
        DownloadOutcome.Disabled => "Disabled",
        _ => outcome.ToString()
    };
}

internal sealed record DownloadRequest(SourceEntry Source, Uri Uri, string TargetPath, bool Force);

internal sealed record DownloadResult(DownloadOutcome Outcome, string Message, long Bytes = 0);

/// <summary>Downloads one list to disk with streaming size limits, conditional requests, retries and atomic replacement.</summary>
internal sealed class ListDownloader : IDisposable
{
    private const int BufferSize = 81_920;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly AppConfig _config;
    private readonly HttpClient _client;

    private readonly record struct Attempt(DownloadResult? Final, string? TransientReason, TimeSpan? RetryAfter)
    {
        internal static Attempt Done(DownloadResult result) => new(result, null, null);
        internal static Attempt Retry(string reason, TimeSpan? retryAfter = null) => new(null, reason, retryAfter);
    }

    internal ListDownloader(AppConfig config)
    {
        _config = config;
        _client = new HttpClient(SsrfProtectedHttpHandler.Create(config.Network, config.ProxyUri), disposeHandler: true)
        {
            // Timeouts are applied per phase below (response headers, then inactivity while reading the body),
            // because a single overall timeout fails large lists on slow links.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream", 0.9));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(config.Network.UserAgent) ? AppInfo.DefaultUserAgent : config.Network.UserAgent);
    }

    public void Dispose() => _client.Dispose();

    internal async Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        Uri uri = request.Uri;
        if (UrlValidator.IsInsecure(uri) && !_config.AllowInsecureSources)
        {
            return new DownloadResult(DownloadOutcome.BlockedByConfig, "Insecure http:// source blocked. Set \"allowInsecureSources\" to true in settings.json to allow it.");
        }

        string target = Path.GetFullPath(request.TargetPath);
        string allowedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Paths.HostfilesFolder)) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadResult(DownloadOutcome.PermanentFailure, $"Target path {target} is outside {allowedRoot}.");
        }
        if (Directory.Exists(target))
        {
            return new DownloadResult(DownloadOutcome.PermanentFailure, $"Target path {target} is a directory.");
        }

        if (_config.ProxyUri is not null)
        {
            try
            {
                await SsrfProtectedHttpHandler.EnsurePublicHostAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (SsrfBlockedException ex)
            {
                return new DownloadResult(DownloadOutcome.SecurityViolation, ex.Message);
            }
        }

        bool haveCachedCopy = File.Exists(target);
        SourceMetadata? metadata = request.Force || !haveCachedCopy ? null : SourceMetadata.Load(target);
        long maxBytes = Math.Max(1, request.Source.MaxSizeMB ?? _config.MaxListSizeInMB) * 1024L * 1024L;
        int maxAttempts = _config.Network.MaxRetries;

        string lastReason = "unknown error";
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Attempt result = await AttemptAsync(request, target, metadata, maxBytes, cancellationToken).ConfigureAwait(false);
            if (result.Final is not null)
            {
                return result.Final;
            }

            lastReason = result.TransientReason ?? lastReason;
            if (attempt == maxAttempts)
            {
                break;
            }

            TimeSpan delay = result.RetryAfter ?? Backoff(attempt);
            if (delay > MaxRetryAfter)
            {
                return new DownloadResult(DownloadOutcome.TransientFailure, $"{lastReason} The server asked to wait {delay.TotalSeconds:N0}s, so it will be retried on the next run.");
            }

            TraceLogger.Log($"{Describe(request)}: attempt {attempt}/{maxAttempts} failed ({lastReason}). Retrying in {delay.TotalSeconds:N1}s.", LogSeverity.Warn);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return new DownloadResult(DownloadOutcome.TransientFailure, $"Failed after {maxAttempts} attempt(s): {lastReason}");
    }

    private async Task<Attempt> AttemptAsync(DownloadRequest request, string target, SourceMetadata? metadata, long maxBytes, CancellationToken cancellationToken)
    {
        string partialPath = target + ".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.Network.ResponseTimeoutSeconds));

        using var message = new HttpRequestMessage(HttpMethod.Get, request.Uri);
        if (metadata is not null)
        {
            // Conditional GET: one request instead of HEAD + GET, and Last-Modified covers servers without ETags.
            if (!string.IsNullOrEmpty(metadata.ETag) && EntityTagHeaderValue.TryParse(metadata.ETag, out EntityTagHeaderValue? etag))
            {
                message.Headers.IfNoneMatch.Add(etag);
            }
            if (metadata.LastModified is { } lastModified)
            {
                message.Headers.IfModifiedSince = lastModified;
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Attempt.Retry($"no response within {_config.Network.ResponseTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex) when (FindSsrfBlock(ex) is { } blocked)
        {
            return Attempt.Done(new DownloadResult(DownloadOutcome.SecurityViolation, blocked.Message));
        }
        catch (HttpRequestException ex)
        {
            return Attempt.Retry(DescribeException(ex));
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Attempt.Done(new DownloadResult(DownloadOutcome.SkippedUpToDate, "Not modified since the last download."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return ClassifyStatus(response);
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!DownloadContentValidator.IsAllowedContentType(mediaType))
            {
                return Attempt.Done(new DownloadResult(DownloadOutcome.ContentRejected,
                    $"Server returned Content-Type '{mediaType}', expected a plain text list."));
            }

            long? declared = response.Content.Headers.ContentLength;
            bool compressed = response.Content.Headers.ContentEncoding.Count > 0;
            if (declared > maxBytes && !compressed)
            {
                return Attempt.Done(TooLarge(declared.Value, maxBytes));
            }

            // Header timeout no longer applies; from here on, the timeout is "no data for N seconds".
            TimeSpan inactivity = TimeSpan.FromSeconds(_config.Network.InactivityTimeoutSeconds);
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            bool committed = false;
            try
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                byte[] sniff = new byte[DownloadContentValidator.SniffSize];
                int sniffLength = 0;
                bool sniffed = false;
                try
                {
                    await using var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                    await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    while (true)
                    {
                        timeout.CancelAfter(inactivity);
                        int read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), timeout.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        // Counted after decompression, so a small gzip bomb is stopped at the limit instead of being inflated in memory.
                        total += read;
                        if (total > maxBytes)
                        {
                            return Attempt.Done(TooLarge(total, maxBytes));
                        }

                        if (!sniffed)
                        {
                            int take = Math.Min(read, sniff.Length - sniffLength);
                            Buffer.BlockCopy(buffer, 0, sniff, sniffLength, take);
                            sniffLength += take;
                            if (sniffLength == sniff.Length)
                            {
                                sniffed = true;
                                if (!DownloadContentValidator.LooksLikeText(sniff, truncated: true, out string? reason))
                                {
                                    return Attempt.Done(new DownloadResult(DownloadOutcome.ContentRejected, $"Rejected: {reason}."));
                                }
                            }
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }

                    if (!sniffed && !DownloadContentValidator.LooksLikeText(sniff.AsSpan(0, sniffLength), truncated: false, out string? shortReason))
                    {
                        return Attempt.Done(new DownloadResult(DownloadOutcome.ContentRejected, $"Rejected: {shortReason}."));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (total == 0)
                {
                    // An empty 200 is a classic CDN glitch; worth retrying, and the cached copy is kept meanwhile.
                    return Attempt.Retry("the server returned an empty body.");
                }

                string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(request.Source.Sha256)
                    && !string.Equals(sha256, request.Source.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Attempt.Done(new DownloadResult(DownloadOutcome.ContentRejected,
                        $"SHA-256 mismatch: expected {request.Source.Sha256.Trim()}, got {sha256}."));
                }

                ContentCheck check = DownloadContentValidator.CheckLooksLikeList(partialPath);
                if (!check.Accepted)
                {
                    return Attempt.Done(new DownloadResult(DownloadOutcome.ContentRejected, $"Rejected: {check.Reason}."));
                }

                string? sanityProblem = CheckSizeAgainstCachedCopy(target, total, request.Force);
                if (sanityProblem is not null)
                {
                    return Attempt.Done(new DownloadResult(DownloadOutcome.ContentRejected, sanityProblem));
                }

                File.Move(partialPath, target, overwrite: true);
                committed = true;

                var newMetadata = new SourceMetadata
                {
                    Url = request.Source.Url,
                    ETag = response.Headers.ETag?.ToString(),
                    LastModified = response.Content.Headers.LastModified,
                    DownloadedUtc = DateTimeOffset.UtcNow,
                    Bytes = total,
                    Sha256 = sha256
                };
                try
                {
                    // Always rewritten: a download without an ETag must not leave a stale one behind that could
                    // later make the file look "up to date" forever.
                    SourceMetadata.Save(target, newMetadata);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SourceMetadata.Delete(target);
                    TraceLogger.Log($"{Describe(request)}: could not save download metadata ({ex.Message}); the next run will download it again.", LogSeverity.Warn);
                }

                return Attempt.Done(new DownloadResult(
                    newMetadata.HasValidator ? DownloadOutcome.Success : DownloadOutcome.SuccessWithoutValidator,
                    $"Downloaded {IOHelpers.FormatBytes(total)}.", total));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Attempt.Retry($"no data received for {inactivity.TotalSeconds:N0}s.");
            }
            catch (HttpRequestException ex)
            {
                return Attempt.Retry(DescribeException(ex));
            }
            catch (IOException ex)
            {
                return Attempt.Retry($"I/O error: {ex.Message}");
            }
            finally
            {
                if (!committed)
                {
                    AtomicFile.TryDelete(partialPath);
                }
            }
        }
    }

    private static Attempt ClassifyStatus(HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;
        string status = $"HTTP {code} {response.ReasonPhrase}".TrimEnd();

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
            case HttpStatusCode.Gone:
            case HttpStatusCode.UnavailableForLegalReasons:
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.MethodNotAllowed:
                return Attempt.Done(new DownloadResult(DownloadOutcome.PermanentFailure, $"{status}. Fix or remove this source in settings.json."));

            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.ServiceUnavailable:
                return Attempt.Retry($"{status}.", GetRetryAfter(response));

            case HttpStatusCode.RequestTimeout:
                return Attempt.Retry($"{status}.");
        }

        return code >= 500 || code == 425 // 425 Too Early
            ? Attempt.Retry($"{status}.")
            : Attempt.Done(new DownloadResult(DownloadOutcome.PermanentFailure, $"{status}."));
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (retryAfter?.Date is { } date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    /// <summary>Exponential backoff with jitter: about 2s, 4s, 8s... capped at 30s.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        double seconds = Math.Min(MaxBackoff.TotalSeconds, 2 * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
    }

    /// <summary>
    /// Rejects a download that shrank or grew far more than expected compared with the cached copy, which catches
    /// truncated, broken or hijacked sources. Skipped for --fresh runs and small lists.
    /// </summary>
    private string? CheckSizeAgainstCachedCopy(string target, long newBytes, bool force)
    {
        SanitySettings sanity = _config.Sanity;
        if (force || !sanity.Enabled || !File.Exists(target))
        {
            return null;
        }

        long oldBytes = new FileInfo(target).Length;
        if (oldBytes < sanity.MinimumBytes)
        {
            return null;
        }

        if (newBytes < oldBytes * (100.0 - sanity.MaxShrinkPercent) / 100.0)
        {
            return $"Rejected: the list shrank from {IOHelpers.FormatBytes(oldBytes)} to {IOHelpers.FormatBytes(newBytes)} (more than {sanity.MaxShrinkPercent}%). " +
                   "The cached copy is kept. Run with --fresh to accept it anyway.";
        }
        if (newBytes > oldBytes * (100.0 + sanity.MaxGrowPercent) / 100.0)
        {
            return $"Rejected: the list grew from {IOHelpers.FormatBytes(oldBytes)} to {IOHelpers.FormatBytes(newBytes)} (more than {sanity.MaxGrowPercent}%). " +
                   "The cached copy is kept. Run with --fresh to accept it anyway.";
        }
        return null;
    }

    private static DownloadResult TooLarge(long bytes, long maxBytes) =>
        new(DownloadOutcome.ContentRejected,
            $"Rejected: larger than the {IOHelpers.FormatBytes(maxBytes)} limit (at least {IOHelpers.FormatBytes(bytes)}). Raise maxListSizeInMB or the source's maxSizeMB if this is expected.");

    private static SsrfBlockedException? FindSsrfBlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SsrfBlockedException blocked)
            {
                return blocked;
            }
        }
        return null;
    }

    private static string DescribeException(Exception exception)
    {
        string message = exception.Message;
        if (exception.InnerException is { } inner && !message.Contains(inner.Message, StringComparison.Ordinal))
        {
            message += $" ({inner.Message})";
        }
        return message;
    }

    internal static string Describe(DownloadRequest request) =>
        string.IsNullOrWhiteSpace(request.Source.Name) ? request.Uri.Host + request.Uri.AbsolutePath : request.Source.Name!;
}
