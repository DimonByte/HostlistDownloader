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

using HostlistDownloader.Modules.Helpers;
using HostlistDownloader.Modules.HostlistManagement.Generation;
using HostlistDownloader.Modules.WindowsSystem;
using HostlistDownloader.Modules.WindowsSystem.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;

namespace HostlistDownloader.Modules.Network
{
    /// <summary>
    /// Outcome of a single download attempt sequence (including retries). Distinguishing Permanent
    /// from Transient failures lets callers avoid endlessly retrying/recovering a URL that returned
    /// 404 - that will never resolve itself, unlike a network blip or timeout.
    /// </summary>
    public enum DownloadOutcome
    {
        Success,
        SkippedUpToDate,
        TransientFailure,
        PermanentFailure,
        Cancelled,
        DownloadBlockedByConfig,
        NotStarted,
        ContentRejectedNonText
    }

    internal class DownloadController
    {
        private static readonly HttpClient httpClient = new();
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        private const int MaxRetries = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        private const int MaxUrlLength = 2048;
        private const int MaxLocalPathLength = 255;

        static DownloadController()
        {
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HostlistDownloader", "1.0"));
            httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            httpClient.Timeout = DefaultTimeout;
        }

        internal static async Task<DownloadOutcome> DownloadFileAsync(
            string url,
            string localPath,
            bool forceMode,
            int fileID,
            CancellationToken cancellationToken = default)
        {
            string workingOnName = SafeGetFileName(url, localPath);

            if (string.IsNullOrWhiteSpace(url))
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | URL is null or empty", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | Invalid URL scheme or format: {url}", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }

            if (url.Length > MaxUrlLength)
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | URL exceeds maximum length of {MaxUrlLength} characters: {url}", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }

            if (await URLSecurityValidator.IsInternalAddress(uri))
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | SSRF Protection Fault: URL resolves to a prohibited internal/private IP address: {uri.DnsSafeHost}. Aborting.", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !AppConfig.Instance.AllowInsecureSources)
            {
                TraceLogger.Log(
                    $"{fileID} - {workingOnName} | Insecure HTTP source blocked by configuration. " +
                    $"Enable AllowInsecureSources to download from HTTP: {url}",
                    Enums.StatusSeverityType.Error);
                return DownloadOutcome.DownloadBlockedByConfig;
            }

            if (string.IsNullOrWhiteSpace(localPath))
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | Local path is null or empty", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }

            if (localPath.Length > MaxLocalPathLength)
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | Local path exceeds maximum allowed length. Aborting.", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }

            string normalizedLocalPath = Path.GetFullPath(localPath);

            string allowedRoot = Path.GetFullPath(Paths.HostfilesLocation);
            if (!normalizedLocalPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                TraceLogger.Log(
                    $"{fileID} - {workingOnName} | Local path resolves outside the allowed download directory " +
                    $"({allowedRoot}): {normalizedLocalPath}",
                    Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }

            workingOnName = SafeGetFileName(url, normalizedLocalPath);

            if (IsSystemFile(normalizedLocalPath))
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | Target path is a known system file. Aborting.", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            if (normalizedLocalPath.Contains(".."))
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | Path traversal detected in local path: {localPath}", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            string etagPath = normalizedLocalPath + ".etag";

            if (File.Exists(etagPath))
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | ETag metadata exists, checking online version...", Enums.StatusSeverityType.Debug);
                try
                {
                    using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                    using HttpResponseMessage headResponse = await httpClient.SendAsync(headRequest, cancellationToken).ConfigureAwait(false);

                    if (headResponse.IsSuccessStatusCode)
                    {
                        string? remoteETag = headResponse.Headers.ETag?.Tag;
                        string storedETag = await File.ReadAllTextAsync(etagPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(remoteETag)
                            && !string.IsNullOrEmpty(storedETag)
                            && remoteETag == storedETag
                            && !forceMode)
                        {
                            if (!File.Exists(normalizedLocalPath))
                            {
                                // ETag is present but the actual host file was deleted.
                                TraceLogger.Log($"{fileID} - {workingOnName} | ETag exists but host file is missing. Proceeding with download.", Enums.StatusSeverityType.Warning);
                            }
                            else
                            {
                                TraceLogger.Log($"{fileID} - {workingOnName} | ETag matches – file is up to date. Skipping download.", Enums.StatusSeverityType.Debug);
                                return DownloadOutcome.SkippedUpToDate;
                            }
                        }
                        else
                        {
                            TraceLogger.Log($"{fileID} - {workingOnName} | ETag differs or is missing remotely. Proceeding with download.", Enums.StatusSeverityType.Debug);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log($"{fileID} - {workingOnName} | ETag check cancelled by user", Enums.StatusSeverityType.Warning);
                    return DownloadOutcome.Cancelled;
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"{fileID} - {workingOnName} | Error checking online ETag, proceeding with download: {ex.Message}", Enums.StatusSeverityType.Warning);
                }
            }
            else
            {
                TraceLogger.Log($"{fileID} - {workingOnName} | No ETag metadata found. Proceeding with download.", Enums.StatusSeverityType.Debug);
            }

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    TraceLogger.Log(
                        $"{fileID} - {workingOnName} | Downloading to {normalizedLocalPath} (attempt {attempt}/{MaxRetries})...",
                        Enums.StatusSeverityType.Debug);

                    string? directory = Path.GetDirectoryName(normalizedLocalPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        TraceLogger.Log($"{fileID} - {workingOnName} | Created directory: {directory}", Enums.StatusSeverityType.Debug);
                    }

                    // Reject reparse points / symlinks before opening the file.
                    if (File.Exists(normalizedLocalPath))
                    {
                        FileAttributes attrs = File.GetAttributes(normalizedLocalPath);
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            TraceLogger.Log($"{fileID} - {workingOnName} | Target path is a symbolic link or reparse point. Aborting.", Enums.StatusSeverityType.Error);
                            return DownloadOutcome.PermanentFailure;
                        }
                        if (attrs.HasFlag(FileAttributes.Directory))
                        {
                            TraceLogger.Log($"{fileID} - {workingOnName} | Target path is a directory. Aborting.", Enums.StatusSeverityType.Error);
                            return DownloadOutcome.PermanentFailure;
                        }
                    }

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    linkedCts.CancelAfter(TimeSpan.FromMinutes(5));

                    using HttpResponseMessage response = await httpClient.GetAsync(url, linkedCts.Token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            TraceLogger.Log($"{fileID} - {workingOnName} | 404 Not Found – permanent failure, not retrying.", Enums.StatusSeverityType.Error);
                            return DownloadOutcome.PermanentFailure;
                        }

                        // 401/403/405 are also unlikely to succeed on retry.
                        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                            or System.Net.HttpStatusCode.Forbidden
                            or System.Net.HttpStatusCode.MethodNotAllowed)
                        {
                            TraceLogger.Log($"{fileID} - {workingOnName} | {response.StatusCode} – server rejected request, not retrying.", Enums.StatusSeverityType.Error);
                            return DownloadOutcome.PermanentFailure;
                        }

                        TraceLogger.Log($"{fileID} - {workingOnName} | Attempt {attempt} failed with status {response.StatusCode}", Enums.StatusSeverityType.Warning);

                        if (attempt < MaxRetries)
                            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    string? contentType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.IsNullOrEmpty(contentType) && !URLSecurityValidator.IsAllowedContentType(contentType))
                    {
                        TraceLogger.Log(
                            $"{fileID} - {workingOnName} | Server returned disallowed Content-Type '{contentType}'. " +
                            $"Expected text/plain, text/csv, or application/octet-stream.",
                            Enums.StatusSeverityType.Error);
                        return DownloadOutcome.ContentRejectedNonText;
                    }

                    long maxBytes = AppConfig.Instance.MaxListSizeInMB * 1024L * 1024L;
                    long? declaredLength = response.Content.Headers.ContentLength;

                    if (declaredLength.HasValue && declaredLength.Value > maxBytes)
                    {
                        TraceLogger.Log(
                            $"{fileID} - {workingOnName} | Declared content length {declaredLength.Value:N0} bytes exceeds " +
                            $"{AppConfig.Instance.MaxListSizeInMB} MB limit. Aborting.",
                            Enums.StatusSeverityType.Error);
                        return DownloadOutcome.DownloadBlockedByConfig;
                    }

                    byte[] contentBytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token).ConfigureAwait(false);

                    if (contentBytes.Length > maxBytes)
                    {
                        TraceLogger.Log(
                            $"{fileID} - {workingOnName} | Actual content size {contentBytes.Length:N0} bytes exceeds " +
                            $"{AppConfig.Instance.MaxListSizeInMB} MB limit. Aborting.",
                            Enums.StatusSeverityType.Error);
                        return DownloadOutcome.DownloadBlockedByConfig;
                    }

                    if (contentBytes.Length == 0)
                    {
                        TraceLogger.Log($"{fileID} - {workingOnName} | Server returned an empty body. Skipping write.", Enums.StatusSeverityType.Warning);
                        return DownloadOutcome.PermanentFailure;
                    }

                    bool isGzipped = response.Content.Headers.ContentEncoding
                        ?.Any(e => string.Equals(e, "gzip", StringComparison.OrdinalIgnoreCase)) ?? false;

                    byte[] finalContent;

                    if (isGzipped)
                    {
                        TraceLogger.Log($"{fileID} - {workingOnName} | Decompressing GZip payload...", Enums.StatusSeverityType.Debug);
                        using var compressedStream = new MemoryStream(contentBytes, writable: false);
                        using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                        using var decompressedBuffer = new MemoryStream();
                        await gzipStream.CopyToAsync(decompressedBuffer, linkedCts.Token).ConfigureAwait(false);

                        if (decompressedBuffer.Length > maxBytes)
                        {
                            TraceLogger.Log(
                                $"{fileID} - {workingOnName} | Decompressed size {decompressedBuffer.Length:N0} bytes exceeds " +
                                $"{AppConfig.Instance.MaxListSizeInMB} MB limit. Aborting to prevent decompression bomb.",
                                Enums.StatusSeverityType.Error);
                            return DownloadOutcome.DownloadBlockedByConfig;
                        }

                        finalContent = decompressedBuffer.ToArray();
                    }
                    else
                    {
                        finalContent = contentBytes;
                    }

                    if (!URLSecurityValidator.IsPlainTextContent(finalContent))
                    {
                        TraceLogger.Log(
                            $"{fileID} - {workingOnName} | Downloaded content appears to be HTML or non-text data, " +
                            $"not a valid host list. Rejecting.",
                            Enums.StatusSeverityType.Error);
                        return DownloadOutcome.ContentRejectedNonText;
                    }

                    using var fileStream = new FileStream(
                        normalizedLocalPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.Asynchronous);

                    await fileStream.WriteAsync(finalContent.AsMemory(), linkedCts.Token).ConfigureAwait(false);
                    await fileStream.FlushAsync(linkedCts.Token).ConfigureAwait(false);

                    string? newETag = response.Headers.ETag?.Tag;
                    if (!string.IsNullOrEmpty(newETag))
                    {
                        await File.WriteAllTextAsync(etagPath, newETag, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                        TraceLogger.Log($"{fileID} - {workingOnName} | Stored ETag: {newETag}", Enums.StatusSeverityType.Debug);
                    }
                    else
                    {
                        TraceLogger.Log(
                            $"{fileID} - {workingOnName} | No ETag received from server. " +
                            $"This file will be re-downloaded on every sync.",
                            Enums.StatusSeverityType.Warning);
                    }

                    HostlistOrchestrator.HasDownloadedUpdates = true;
                    TraceLogger.Log($"{fileID} - {workingOnName} | Download completed successfully ({finalContent.Length:N0} bytes).");
                    return DownloadOutcome.Success;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log($"{fileID} - {workingOnName} | Download cancelled by user", Enums.StatusSeverityType.Warning);
                    return DownloadOutcome.Cancelled;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout on the per-attempt linked CTS.
                    TraceLogger.Log($"{fileID} - {workingOnName} | Attempt {attempt} timed out", Enums.StatusSeverityType.Error);
                    if (attempt < MaxRetries)
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException hre)
                {
                    TraceLogger.Log($"{fileID} - {workingOnName} | Network error on attempt {attempt}: {hre.Message}", Enums.StatusSeverityType.Warning);
                    if (attempt < MaxRetries)
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ioe)
                {
                    TraceLogger.Log($"{fileID} - {workingOnName} | I/O error on attempt {attempt}: {ioe.Message}", Enums.StatusSeverityType.Error);
                    if (attempt < MaxRetries)
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"{fileID} - {workingOnName} | Unexpected error on attempt {attempt}: {ex.Message}", Enums.StatusSeverityType.Error);
                    TraceLogger.Log($"{fileID} - {workingOnName} | Stack: {ex.StackTrace}", Enums.StatusSeverityType.Debug);
                    if (attempt < MaxRetries)
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            TraceLogger.Log($"{fileID} - {workingOnName} | Download failed after {MaxRetries} attempts", Enums.StatusSeverityType.Error);
            return DownloadOutcome.TransientFailure;
        }

        private static bool IsSystemFile(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.Contains(@"\windows\system32\")
                || lower.Contains(@"\windows\winnt")
                || lower.Contains(@"\windows\servicing"))
            {
                return true;
            }
            string fileName = Path.GetFileName(lower);
            return fileName is "hosts" or "hosts.bak" or "boot.ini" or "autoexec.bat"
                   or "registry" or "system.dat" or "pagefile.sys" or "swapfile.sys";
        }

        private static string SafeGetFileName(string url, string fallbackPath)
        {
            try
            {
                string name = Path.GetFileName(new Uri(url).LocalPath);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch
            {
                TraceLogger.Log($"Failed to extract file name from URL: {url}. Using fallback path.", Enums.StatusSeverityType.Warning);
            }
            return Path.GetFileName(fallbackPath);
        }
    }
}