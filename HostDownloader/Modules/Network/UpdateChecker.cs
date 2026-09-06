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
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace HostlistDownloader.Modules.Network
{
    internal class UpdateChecker
    {
        private static readonly string OwnerRepo = "DimonByte/HostlistDownloader";
        public static async Task BeginUpdateReplacement()
        {
            TraceLogger.Log($"[UPDATE] Starting update replacement process...", Enums.StatusSeverityType.Information);

            try
            {
                string latestReleaseTag = GetLatestReleaseTag(OwnerRepo);

                if (string.IsNullOrEmpty(latestReleaseTag))
                {
                    TraceLogger.Log("Unable to determine latest release tag.", Enums.StatusSeverityType.Error);
                    return;
                }

                if (latestReleaseTag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    latestReleaseTag = latestReleaseTag[1..];
                }

                string downloadUrl = $"https://github.com/{OwnerRepo}/releases/download/v{latestReleaseTag}/HostlistDownloader.exe";
                string currentExecutablePath = Assembly.GetExecutingAssembly().Location;
                string tempUpdatePath = Path.Combine(Path.GetDirectoryName(currentExecutablePath), "HostlistDownloader_update.exe");

                TraceLogger.Log($"Downloading update from: {downloadUrl}", Enums.StatusSeverityType.Debug);

                using (HttpClient client = new())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("HostlistDownloader-Updater/1.0");
                    var response = await client.GetAsync(downloadUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        TraceLogger.Log($"Failed to download update: {(int)response.StatusCode} {response.ReasonPhrase}", Enums.StatusSeverityType.Error);
                        return;
                    }

                    using (var fileStream = new FileStream(tempUpdatePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }
                }

                TraceLogger.Log("Download complete. Running PowerShell script to replace executable...", Enums.StatusSeverityType.Information);

                string psScript = $@"
                # Wait 5 seconds then replace the executable
                Start-Sleep -Seconds 5

                # Get the current process ID
                $CurrentProcessId = [System.Diagnostics.Process]::GetCurrentProcess().Id

                # Wait for the current process to finish (if it's still running)
                do {{
                    $process = Get-Process -Id $CurrentProcessId -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 1
                }} while ($process -ne $null)

                # Replace the executable
                $sourcePath = '{tempUpdatePath.Replace("\\", "\\\\")}'
                $destinationPath = '{currentExecutablePath.Replace("\\", "\\\\")}'

                if (Test-Path $sourcePath) {{
                    if (Test-Path $destinationPath) {{
                        Remove-Item $destinationPath -Force
                    }}
                    Move-Item $sourcePath $destinationPath
                    Write-Output 'Update completed successfully'
                }} else {{
                    Write-Error 'Source file not found: $sourcePath'
                }}
                Start-Sleep -Seconds 5
                ";

                string psScriptPath = Path.Combine(Path.GetDirectoryName(currentExecutablePath), "update_script.ps1");
                File.WriteAllText(psScriptPath, psScript);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{psScriptPath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                });

                TraceLogger.Log("[UPDATE] PowerShell script started. Current process will now exit, please wait 5 seconds for update to complete...", Enums.StatusSeverityType.Information);
                Environment.Exit(ErrorCodes.UpdateInProgress);
            }
            catch (Exception ex)
            {
                TraceLogger.Log($"[UPDATE ERROR] Failed to perform update replacement: {ex}", Enums.StatusSeverityType.Error);
            }
        }

        public static bool IsUpdateAvailable()
        {
            TraceLogger.Log("Checking for HostlistDownloader updates...", Enums.StatusSeverityType.Debug);
            //Get version number of current program
            string? CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            if (string.IsNullOrEmpty(CurrentVersion))
            {
                TraceLogger.Log("Unable to determine current version.", Enums.StatusSeverityType.Error);
                return false;
            }
            string LatestReleaseTag = GetLatestReleaseTag(OwnerRepo);

            if (string.IsNullOrEmpty(LatestReleaseTag))
            {
                TraceLogger.Log("Unable to determine latest release tag.", Enums.StatusSeverityType.Error);
                return false;
            }

            if (LatestReleaseTag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                LatestReleaseTag = LatestReleaseTag[1..];
            }

            TraceLogger.Log($"Current version: {CurrentVersion}, Latest release tag: {LatestReleaseTag}", Enums.StatusSeverityType.Debug);

            if (Version.TryParse(CurrentVersion, out Version? currentVer) && Version.TryParse(LatestReleaseTag, out Version? latestVer))
            {
                if (latestVer > currentVer)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    TraceLogger.Log($"[UPDATE AVAILABLE] A HostlistDownloader update is available! {latestVer} > {currentVer}", Enums.StatusSeverityType.Important);
                    TraceLogger.Log($"Use /update command to update automatically or visit https://github.com/DimonByte/HostlistDownloader to download the latest version.", Enums.StatusSeverityType.Important);
                    return true;
                }
                else
                {
                    TraceLogger.Log($"[NO UPDATE] No update available: {latestVer} <= {currentVer}", Enums.StatusSeverityType.Debug);
                    return false;
                }
            }
            else
            {
                TraceLogger.Log($"Failed to parse version numbers. Current: {CurrentVersion}, Latest: {LatestReleaseTag}", Enums.StatusSeverityType.Error);
            }
            return false;
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
