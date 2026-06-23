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

using System.IO.Compression;
using System.Net.Http.Headers;

namespace HostlistDownloader.Modules.DownloadSystem
{
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

        public static async Task<bool> DownloadFileAsync(string url, string localPath, bool forceMode, CancellationToken cancellationToken = default)
        {
            TraceLogger.Log($"Checking {url}...");
            if (string.IsNullOrEmpty(url))
            {
                TraceLogger.Log("URL is null or empty", Enums.StatusSeverityType.Error);
                return false;
            }
            if (string.IsNullOrEmpty(localPath))
            {
                TraceLogger.Log("Local path is null or empty", Enums.StatusSeverityType.Error);
                return false;
            }
            string metadataPath1 = localPath + ".etag";
            if (File.Exists(metadataPath1))
            {
                TraceLogger.Log("ETag exists, checking online version...");
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
                                TraceLogger.Log("ETag exists but the host file is missing. proceeding with download.");
                            }
                            else
                            {
                                TraceLogger.Log("ETag matches - file is already up to date. Skipping download.");
                                return true;
                            }
                        }
                        else
                        {
                            TraceLogger.Log("ETag differs or missing, will proceed with download.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Error checking online ETag, will proceed with download: {ex.Message}", Enums.StatusSeverityType.Warning);
                }
            }

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    TraceLogger.Log($"Downloading from {url} to {localPath} (Attempt {attempt}/{MaxRetries})...");
                    string? directory = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        TraceLogger.Log($"Directory created: {directory}");
                    }

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMinutes(5));
                    using HttpResponseMessage response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        TraceLogger.Log($"HTTP response received with status code: {response.StatusCode}");
                        long? contentLength = response.Content.Headers.ContentLength;
                        byte[] contentBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                        bool isGzipped = response.Content.Headers.ContentEncoding?.Any(e => e.Contains("gzip")) ?? false;
                        using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                        if (isGzipped)
                        {
                            TraceLogger.Log("Decompressing GZip...");
                            using var compressedStream = new MemoryStream(contentBytes);
                            using var decompressedStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                            if (contentLength.HasValue)
                            {
                                TraceLogger.Log($"Decompressing {contentLength.Value} bytes of GZip data...");
                            }
                            await decompressedStream.CopyToAsync(fileStream, cts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            TraceLogger.Log($"Content is not gzipped, writing directly to file...");
                            if (contentLength.HasValue)
                            {
                                TraceLogger.Log($"Writing {contentLength.Value:N0} bytes to file...");
                            }

                            await fileStream.WriteAsync(contentBytes.AsMemory(0, contentBytes.Length), cts.Token).ConfigureAwait(false);
                        }
                        if (response.Headers.ETag != null && !string.IsNullOrEmpty(response.Headers.ETag.Tag))
                        {
                            string metadataPath = localPath + ".etag";
                            await File.WriteAllTextAsync(metadataPath, response.Headers.ETag.Tag, cancellationToken).ConfigureAwait(false);
                            TraceLogger.Log($"ETag stored with file: {response.Headers.ETag.Tag}");
                        }
                        HostListManager.HasDownloadedUpdates = true;
                        TraceLogger.Log("Download completed successfully.");
                        return true;
                    }
                    else
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            TraceLogger.Log($"Download failed with status code: {response.StatusCode} (File not found, not retrying)", Enums.StatusSeverityType.Error);
                            return false;
                        }
                        TraceLogger.Log($"Download attempt {attempt} failed with status code: {response.StatusCode}", Enums.StatusSeverityType.Warning);
                        if (attempt < MaxRetries)
                        {
                            TraceLogger.Log($"Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log("Download was cancelled by user", Enums.StatusSeverityType.Warning);
                    return false;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TraceLogger.Log($"Download timed out on attempt {attempt}", Enums.StatusSeverityType.Error);
                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException hre) when (attempt < MaxRetries)
                {
                    TraceLogger.Log($"Network error on attempt {attempt}: {hre.Message}", Enums.StatusSeverityType.Warning);

                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    TraceLogger.Log($"Error downloading file on attempt {attempt}: {ex.Message}", Enums.StatusSeverityType.Error);
                    TraceLogger.Log($"Exception details: {ex}", Enums.StatusSeverityType.Error);

                    // If this isn't the last attempt, wait before retrying
                    if (attempt < MaxRetries)
                    {
                        TraceLogger.Log($"Waiting {RetryDelay.TotalSeconds} seconds before retry...");
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            TraceLogger.Log($"Download failed after {MaxRetries} attempts", Enums.StatusSeverityType.Error);
            return false;
        }
    }
}