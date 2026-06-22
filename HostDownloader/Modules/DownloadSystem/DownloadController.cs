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

        static DownloadController()
        {
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HostlistDownloader", "1.0"));
            httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            httpClient.Timeout = DefaultTimeout;
        }

        public static async Task<bool> DownloadFileAsync(string url, string localPath, CancellationToken cancellationToken = default)
        {
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
            try
            {
                TraceLogger.Log($"Downloading from {url} to {localPath}...");
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
                    byte[] contentBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    bool isGzipped = response.Content.Headers.ContentEncoding?.Any(e => e.Contains("gzip")) ?? false;
                    using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                    if (isGzipped)
                    {
                        TraceLogger.Log("Decompressing GZip...");
                        using var compressedStream = new MemoryStream(contentBytes);
                        using var decompressedStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                        await decompressedStream.CopyToAsync(fileStream, cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        TraceLogger.Log("Content is not gzipped, writing directly to file...");
                        await fileStream.WriteAsync(contentBytes.AsMemory(0, contentBytes.Length), cts.Token).ConfigureAwait(false);
                    }

                    TraceLogger.Log("Download completed successfully.");
                    return true;
                }
                else
                {
                    TraceLogger.Log($"Download failed with status code: {response.StatusCode}", Enums.StatusSeverityType.Error);
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TraceLogger.Log("Download was cancelled by user", Enums.StatusSeverityType.Warning);
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TraceLogger.Log("Download timed out", Enums.StatusSeverityType.Error);
                return false;
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"Error downloading file: {ex.Message}", Enums.StatusSeverityType.Error);
                TraceLogger.Log($"Exception details: {ex}", Enums.StatusSeverityType.Error);
                return false;
            }
        }
    }
}