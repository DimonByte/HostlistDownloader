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
using HostlistDownloader.Modules.HostListDownloaderInternals;
using HostlistDownloader.Modules.WindowsSystem;
using System.IO.Compression;
using System.Net.Http.Headers;

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
        Cancelled
    }

    internal class DownloadController
    {
        private static readonly HttpClient httpClient = new();
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        private const int MaxRetries = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

        static DownloadController()
        {
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HostlistDownloader", "1.0"));
            httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            httpClient.Timeout = DefaultTimeout;
        }

        public static async Task<DownloadOutcome> DownloadFileAsync(string url, string localPath, bool forceMode, int fileID, CancellationToken cancellationToken = default)
        {
            string WorkingOnName = Path.GetFileName(url);
            TraceLogger.Log($"{fileID} - {WorkingOnName} | Checking {url}...", Enums.StatusSeverityType.Debug);
            if (string.IsNullOrWhiteSpace(url))
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | URL is null or empty", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | Invalid URL scheme or format: {url}", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | URL must start with http:// or https://: {url}", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            //If url is http but configmanager's AllowInsecureSources is false, return permanent failure.
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !ConfigManager.Instance.AllowInsecureSources)
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | Insecure HTTP sources has been blocked by configuration, please enable AllowInsecureSources to download from HTTP: {url}", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            if (string.IsNullOrWhiteSpace(localPath))
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | Local path is null or empty", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            string normalizedLocalPath = Path.GetFullPath(localPath);
            if (normalizedLocalPath.Contains(".."))
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | Path traversal detected in local path: {localPath}", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            WorkingOnName = Path.GetFileName(normalizedLocalPath);
            string metadataPath1 = normalizedLocalPath + ".etag";
            if (File.Exists(metadataPath1))
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | ETag exists, checking online version...", Enums.StatusSeverityType.Debug);
                try
                {
                    using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                    using HttpResponseMessage headResponse = await httpClient.SendAsync(headRequest, cancellationToken).ConfigureAwait(false);

                    if (headResponse.IsSuccessStatusCode)
                    {
                        string? eTag = headResponse.Headers.ETag?.Tag;
                        string? storedETag = await File.ReadAllTextAsync(metadataPath1, cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(eTag) && !string.IsNullOrEmpty(storedETag) && eTag == storedETag && !forceMode)
                        {
                            if (!File.Exists(normalizedLocalPath)) //Check if host file doesn't exist, but etag does.
                            {
                                TraceLogger.Log($"{fileID} - {WorkingOnName} | ETag exists but the host file is missing. proceeding with download.");
                            }
                            else
                            {
                                TraceLogger.Log($"{fileID} - {WorkingOnName} | ETag matches - file is already up to date. Skipping download.", Enums.StatusSeverityType.Debug);
                                return DownloadOutcome.SkippedUpToDate;
                            }
                        }
                        else
                        {
                            TraceLogger.Log($"{fileID} - {WorkingOnName} | ETag differs or missing, will proceed with download.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"{fileID} - {WorkingOnName} | Error checking online ETag, will proceed with download: {ex.Message}", Enums.StatusSeverityType.Warning);
                }
            }
            else
            {
                TraceLogger.Log($"{fileID} - {WorkingOnName} | No ETag found, will proceed with download.",Enums.StatusSeverityType.Debug);
            }

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    TraceLogger.Log($"{fileID} - {WorkingOnName} | Downloading to {normalizedLocalPath} (Attempt {attempt}/{MaxRetries})...");
                    string? directory = Path.GetDirectoryName(normalizedLocalPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        TraceLogger.Log($"{fileID} - {WorkingOnName} | Directory created: {directory}");
                    }

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMinutes(5));
                    using HttpResponseMessage response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        TraceLogger.Log($"{fileID} - {WorkingOnName} | HTTP response received with status code: {response.StatusCode}", Enums.StatusSeverityType.Debug);
                        long? contentLength = response.Content.Headers.ContentLength;
                        byte[] contentBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                        bool isGzipped = response.Content.Headers.ContentEncoding?.Any(e => e.Contains("gzip")) ?? false;
                        if (File.Exists(normalizedLocalPath) && !File.GetAttributes(normalizedLocalPath).HasFlag(FileAttributes.Directory))
                        {
                            if (File.GetAttributes(normalizedLocalPath).HasFlag(FileAttributes.ReparsePoint))
                            {
                                TraceLogger.Log($"{fileID} - {WorkingOnName} | Target path is a symbolic link or reparse point. Aborting.", Enums.StatusSeverityType.Error);
                                return DownloadOutcome.PermanentFailure;
                            }
                        }
                        using var fileStream = new FileStream(normalizedLocalPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                        if (isGzipped)
                        {
                            TraceLogger.Log($"{fileID} - {WorkingOnName} | Decompressing GZip...", Enums.StatusSeverityType.Debug);
                            using var compressedStream = new MemoryStream(contentBytes);
                            using var decompressedStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                            if (contentLength.HasValue)
                            {
                                TraceLogger.Log($"{fileID} - {WorkingOnName} | Decompressing {contentLength.Value} bytes of GZip data...", Enums.StatusSeverityType.Debug);
                            }
                            //Check if decompressedSize is too large. Crash if it is. This is a safety check to prevent decompression bombs.
                            if (decompressedStream.CanSeek && decompressedStream.Length > ConfigManager.Instance.MaxListSizeInMB * 1024 * 1024)
                            {
                                TraceLogger.Log($"{fileID} - {WorkingOnName} | Decompressed size exceeds limit of {ConfigManager.Instance.MaxListSizeInMB} MB. Aborting download to prevent decompression bombs.", Enums.StatusSeverityType.Error);
                                return DownloadOutcome.PermanentFailure;
                            }
                            await decompressedStream.CopyToAsync(fileStream, cts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            TraceLogger.Log($"{fileID} - {WorkingOnName} | Content is not gzipped, writing directly to file...", Enums.StatusSeverityType.Debug);
                            //Check if contentLength is too large. Crash if it is. This is a safety check to prevent writing huge files.
                            if (contentLength.HasValue && contentLength.Value > ConfigManager.Instance.MaxListSizeInMB * 1024 * 1024)
                            {
                                TraceLogger.Log($"{fileID} - {WorkingOnName} | Content length exceeds limit of {ConfigManager.Instance.MaxListSizeInMB} MB. Aborting download to prevent writing huge files.", Enums.StatusSeverityType.Error);
                                return DownloadOutcome.PermanentFailure;
                            }
                            if (contentLength.HasValue)
                            {
                                TraceLogger.Log($"{fileID} - {WorkingOnName} | Writing {contentLength.Value:N0} bytes to file...", Enums.StatusSeverityType.Debug);
                            }

                            await fileStream.WriteAsync(contentBytes.AsMemory(0, contentBytes.Length), cts.Token).ConfigureAwait(false);
                        }
                        if (response.Headers.ETag != null && !string.IsNullOrEmpty(response.Headers.ETag.Tag))
                        {
                            string metadataPath = normalizedLocalPath + ".etag";
                            await File.WriteAllTextAsync(metadataPath, response.Headers.ETag.Tag, cancellationToken).ConfigureAwait(false);
                            TraceLogger.Log($"{fileID} - {WorkingOnName} | ETag stored with file: {response.Headers.ETag.Tag}",Enums.StatusSeverityType.Debug);
                        }
                        else
                        {
                            TraceLogger.Log($"{fileID} - {WorkingOnName} | No ETag received from server, skipping ETag storage. This will cause HostListDownloader to re-download this file every sync.", Enums.StatusSeverityType.Warning);
                        }
                        HostListManager.HasDownloadedUpdates = true;
                        TraceLogger.Log($"{fileID} - {WorkingOnName} | Download completed successfully.");
                        return DownloadOutcome.Success;
                    }
                    else
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            TraceLogger.Log($"{fileID} - {WorkingOnName} | Download failed with status code: {response.StatusCode} (File not found, not retrying)", Enums.StatusSeverityType.Error);
                            return DownloadOutcome.PermanentFailure;
                        }
                        TraceLogger.Log($"{fileID} - {WorkingOnName} | Download attempt {attempt} failed with status code: {response.StatusCode}", Enums.StatusSeverityType.Warning);
                        if (attempt < MaxRetries)
                        {
                            TraceLogger.Log($"{fileID} - {WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log($"{fileID} - {WorkingOnName} | Download was cancelled by user", Enums.StatusSeverityType.Warning);
                    return DownloadOutcome.Cancelled;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log($"{fileID} - {WorkingOnName} | Download timed out on attempt {attempt}", Enums.StatusSeverityType.Error);
                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"{fileID} - {WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException hre) when (attempt < MaxRetries)
                {
                    TraceLogger.Log($"{fileID} - {WorkingOnName} | Network error on attempt {attempt}: {hre.Message}", Enums.StatusSeverityType.Warning);

                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"{fileID} - {WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"{fileID} - {WorkingOnName} | Error downloading file on attempt {attempt}: {ex.Message}", Enums.StatusSeverityType.Error);
                    TraceLogger.Log($"{fileID} - {WorkingOnName} | Exception details: {ex}", Enums.StatusSeverityType.Error);

                    // If this isn't the last attempt, wait before retrying
                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"{fileID} - {WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            TraceLogger.Log($"{fileID} - {WorkingOnName} | Download failed after {MaxRetries} attempts", Enums.StatusSeverityType.Error);
            return DownloadOutcome.TransientFailure;
        }
    }
}