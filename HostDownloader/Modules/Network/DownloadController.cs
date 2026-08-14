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

        public static async Task<DownloadOutcome> DownloadFileAsync(string url, string localPath, bool forceMode, CancellationToken cancellationToken = default)
        {
            string WorkingOnName = Path.GetFileName(url);
            TraceLogger.Log($"{WorkingOnName} | Checking {url}...");
            if (string.IsNullOrEmpty(url))
            {
                TraceLogger.Log($"{WorkingOnName} | URL is null or empty", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            if (string.IsNullOrEmpty(localPath))
            {
                TraceLogger.Log($"{WorkingOnName} | Local path is null or empty", Enums.StatusSeverityType.Error);
                return DownloadOutcome.PermanentFailure;
            }
            string metadataPath1 = localPath + ".etag";
            if (File.Exists(metadataPath1))
            {
                TraceLogger.Log($"{WorkingOnName} | ETag exists, checking online version...");
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
                            if (!File.Exists(localPath)) //Check if host file doesn't exist, but etag does.
                            {
                                TraceLogger.Log($"{WorkingOnName} | ETag exists but the host file is missing. proceeding with download.");
                            }
                            else
                            {
                                TraceLogger.Log($"{WorkingOnName} | ETag matches - file is already up to date. Skipping download.");
                                return DownloadOutcome.SkippedUpToDate;
                            }
                        }
                        else
                        {
                            TraceLogger.Log($"{WorkingOnName} | ETag differs or missing, will proceed with download.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"{WorkingOnName} | Error checking online ETag, will proceed with download: {ex.Message}", Enums.StatusSeverityType.Warning);
                }
            }

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    TraceLogger.Log($"{WorkingOnName} | Downloading to {localPath} (Attempt {attempt}/{MaxRetries})...");
                    string? directory = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        TraceLogger.Log($"{WorkingOnName} | Directory created: {directory}");
                    }

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMinutes(5));
                    using HttpResponseMessage response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        TraceLogger.Log($"{WorkingOnName} | HTTP response received with status code: {response.StatusCode}");
                        long? contentLength = response.Content.Headers.ContentLength;
                        byte[] contentBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                        bool isGzipped = response.Content.Headers.ContentEncoding?.Any(e => e.Contains("gzip")) ?? false;
                        using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                        if (isGzipped)
                        {
                            TraceLogger.Log($"{WorkingOnName} | Decompressing GZip...");
                            using var compressedStream = new MemoryStream(contentBytes);
                            using var decompressedStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                            if (contentLength.HasValue)
                            {
                                TraceLogger.Log($"{WorkingOnName} | Decompressing {contentLength.Value} bytes of GZip data...");
                            }
                            await decompressedStream.CopyToAsync(fileStream, cts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            TraceLogger.Log($"Content is not gzipped, writing directly to file...");
                            if (contentLength.HasValue)
                            {
                                TraceLogger.Log($"{WorkingOnName} | Writing {contentLength.Value:N0} bytes to file...");
                            }

                            await fileStream.WriteAsync(contentBytes.AsMemory(0, contentBytes.Length), cts.Token).ConfigureAwait(false);
                        }
                        if (response.Headers.ETag != null && !string.IsNullOrEmpty(response.Headers.ETag.Tag))
                        {
                            string metadataPath = localPath + ".etag";
                            await File.WriteAllTextAsync(metadataPath, response.Headers.ETag.Tag, cancellationToken).ConfigureAwait(false);
                            TraceLogger.Log($"{WorkingOnName} | ETag stored with file: {response.Headers.ETag.Tag}");
                        }
                        HostListManager.HasDownloadedUpdates = true;
                        TraceLogger.Log($"{WorkingOnName} | Download completed successfully.");
                        return DownloadOutcome.Success;
                    }
                    else
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            TraceLogger.Log($"{WorkingOnName} | Download failed with status code: {response.StatusCode} (File not found, not retrying)", Enums.StatusSeverityType.Error);
                            return DownloadOutcome.PermanentFailure;
                        }
                        TraceLogger.Log($"{WorkingOnName} | Download attempt {attempt} failed with status code: {response.StatusCode}", Enums.StatusSeverityType.Warning);
                        if (attempt < MaxRetries)
                        {
                            TraceLogger.Log($"{WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log($"{WorkingOnName} | Download was cancelled by user", Enums.StatusSeverityType.Warning);
                    return DownloadOutcome.Cancelled;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log($"{WorkingOnName} | Download timed out on attempt {attempt}", Enums.StatusSeverityType.Error);
                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"{WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException hre) when (attempt < MaxRetries)
                {
                    TraceLogger.Log($"{WorkingOnName} | Network error on attempt {attempt}: {hre.Message}", Enums.StatusSeverityType.Warning);

                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"{WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"{WorkingOnName} | Error downloading file on attempt {attempt}: {ex.Message}", Enums.StatusSeverityType.Error);
                    TraceLogger.Log($"{WorkingOnName} | Exception details: {ex}", Enums.StatusSeverityType.Error);

                    // If this isn't the last attempt, wait before retrying
                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"{WorkingOnName} | Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            TraceLogger.Log($"{WorkingOnName} | Download failed after {MaxRetries} attempts", Enums.StatusSeverityType.Error);
            return DownloadOutcome.TransientFailure;
        }
    }
}