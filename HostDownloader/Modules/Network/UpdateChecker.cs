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
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.Network
{
    internal class UpdateChecker
    {
        public static void IsUpdateAvailable()
        {
            TraceLogger.Log("Checking for updates...", Enums.StatusSeverityType.Debug);
            //Get version number of current program
            string? CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            if (string.IsNullOrEmpty(CurrentVersion))
            {
                TraceLogger.Log("Unable to determine current version.", Enums.StatusSeverityType.Error);
                return;
            }
            string LatestReleaseTag = GetLatestReleaseTag("DimonByte/HostlistDownloader");

            if (string.IsNullOrEmpty(LatestReleaseTag))
            {
                TraceLogger.Log("Unable to determine latest release tag.", Enums.StatusSeverityType.Error);
                return;
            }

            // Remove leading 'v' if present
            if (LatestReleaseTag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                LatestReleaseTag = LatestReleaseTag[1..];
            }

            TraceLogger.Log($"Current version: {CurrentVersion}, Latest release tag: {LatestReleaseTag}", Enums.StatusSeverityType.Debug);

            if (Version.TryParse(CurrentVersion, out Version? currentVer) && Version.TryParse(LatestReleaseTag, out Version? latestVer))
            {
                if (latestVer > currentVer)
                {
                    TraceLogger.Log($"[UPDATE AVAILABLE] A HostlistDownloader update is available! {latestVer} > {currentVer}", Enums.StatusSeverityType.Information);
                    TraceLogger.Log($"Please visit https://github.com/DimonByte/HostlistDownloader to download the latest version.", Enums.StatusSeverityType.Information);
                    return;
                }
                else
                {
                    TraceLogger.Log($"[NO UPDATE] No update available: {latestVer} <= {currentVer}", Enums.StatusSeverityType.Debug);
                    return;
                }
            }
            else
            {
                TraceLogger.Log($"Failed to parse version numbers. Current: {CurrentVersion}, Latest: {LatestReleaseTag}", Enums.StatusSeverityType.Error);
            }
            return;
        }
        private static string GetLatestReleaseTag(string ownerRepo)
        {
            try
            {
                TraceLogger.Log($"Checking latest release for {ownerRepo}...", Enums.StatusSeverityType.Debug);
                using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false });
                http.DefaultRequestHeaders.UserAgent.ParseAdd("HostlistDownloader-Updater/1.0");

                var url = $"https://github.com/{ownerRepo}/releases/latest";
                HttpResponseMessage resp = http.GetAsync(url).GetAwaiter().GetResult();
                TraceLogger.Log($"HTTP response for {url}: {(int)resp.StatusCode} {resp.StatusCode}", Enums.StatusSeverityType.Debug);
                // Check for redirect location (preferred, reliable)
                if (resp.StatusCode == HttpStatusCode.Found ||
                    resp.StatusCode == HttpStatusCode.Redirect ||
                    resp.StatusCode == HttpStatusCode.MovedPermanently ||
                    resp.StatusCode == HttpStatusCode.SeeOther ||
                    resp.StatusCode == HttpStatusCode.TemporaryRedirect ||
                    resp.StatusCode == HttpStatusCode.PermanentRedirect)
                {
                    Uri? loc = resp.Headers.Location;
                    if (loc != null)
                    {
                        TraceLogger.Log($"Redirect location: {loc}", Enums.StatusSeverityType.Debug);
                        // The redirect typically ends with "/releases/tag/vX.Y.Z" -> we take the last segment
                        var segments = loc.Segments.Select(s => s.Trim('/')).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                        if (segments.Length > 0)
                        {
                            var tag = segments.Last();
                            TraceLogger.Log($"Extracted tag from redirect: {tag}", Enums.StatusSeverityType.Debug);
                            return tag;
                        }
                    }
                }
                TraceLogger.Log($"No redirect found for {url}, status code: {(int)resp.StatusCode} {resp.StatusCode}. Attempting HTML parsing fallback.", Enums.StatusSeverityType.Debug);
                // Fallback: some servers may not redirect; parse the HTML for /releases/tag/<tag>
                var html = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Match m = Regex.Match(html, @"/releases/tag/([^""'\s<>]+)");
                if (m.Success && m.Groups.Count > 1)
                {
                    TraceLogger.Log($"Extracted tag from HTML: {m.Groups[1].Value}", Enums.StatusSeverityType.Debug);
                    return m.Groups[1].Value.Trim('/');
                }
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"GetLatestReleaseTag error for {ownerRepo}: {ex}", Enums.StatusSeverityType.Error);
            }
            TraceLogger.Log($"Failed to get latest release tag for {ownerRepo}.", Enums.StatusSeverityType.Error);
            return "";
        }
    }
}
