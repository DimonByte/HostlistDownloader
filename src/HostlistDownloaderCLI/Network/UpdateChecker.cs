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
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostlistDownloader.Network;

internal sealed record GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>"sha256:&lt;hex&gt;" when GitHub has computed it.</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

internal sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public GitHubAsset[]? Assets { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal partial class GitHubJsonContext : JsonSerializerContext
{
}

internal static class UpdateChecker
{
    private const long MaxExecutableBytes = 300L * 1024 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static string LatestReleaseApi => $"https://api.github.com/repos/{AppInfo.Repository}/releases/latest";

    /// <summary>
    /// Background check run after an update: only if "checkForUpdates" is enabled and the last check is older than
    /// "updateCheckIntervalHours". Failures are logged at debug level and never affect the exit code.
    /// </summary>
    internal static async Task CheckIfDueAsync(AppConfig config, CancellationToken cancellationToken)
    {
        if (!config.CheckForUpdates)
        {
            return;
        }

        DateTimeOffset? lastCheck = ReadLastCheck();
        if (lastCheck is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(config.UpdateCheckIntervalHours))
        {
            TraceLogger.Log($"Skipping update check; last checked {last:u}.", LogSeverity.Debug);
            return;
        }

        try
        {
            GitHubRelease? release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            WriteLastCheck();
            if (release is not null && IsNewer(release, out Version? latest))
            {
                TraceLogger.Log($"[UPDATE AVAILABLE] HostlistDownloader {latest} is available (you have {AppInfo.ShortVersion}). " +
                                $"Run with --update to install it, or download it from https://github.com/{AppInfo.Repository}/releases.", LogSeverity.Notice);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            TraceLogger.Log($"Update check failed: {ex.Message}", LogSeverity.Debug);
        }
    }

    /// <summary>--update: downloads the latest release and replaces the executable (Windows only). Returns an exit code.</summary>
    internal static async Task<int> RunSelfUpdateAsync(CancellationToken cancellationToken)
    {
        GitHubRelease? release;
        try
        {
            release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            TraceLogger.Log($"Could not check for updates: {ex.Message}", LogSeverity.Error);
            return ExitCodes.NetworkConnectionFailed;
        }

        if (release is null || !IsNewer(release, out Version? latest))
        {
            TraceLogger.Log($"HostlistDownloader {AppInfo.ShortVersion} is the latest version.", LogSeverity.Notice);
            return ExitCodes.Success;
        }

        if (!OperatingSystem.IsWindows())
        {
            TraceLogger.Log($"Version {latest} is available. Automatic replacement is only supported on Windows; download it from {release.HtmlUrl ?? $"https://github.com/{AppInfo.Repository}/releases"}.", LogSeverity.Notice);
            return ExitCodes.Success;
        }

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            TraceLogger.Log("Cannot update: not running as a published executable.", LogSeverity.Error);
            return ExitCodes.GeneralError;
        }

        GitHubAsset? asset = SelectAsset(release);
        if (asset?.DownloadUrl is null)
        {
            TraceLogger.Log($"Release {release.TagName} has no Windows executable for {RuntimeInformation.RuntimeIdentifier}. Download it manually from {release.HtmlUrl}.", LogSeverity.Error);
            return ExitCodes.UpdateProcessError;
        }
        if (asset.Size > MaxExecutableBytes)
        {
            TraceLogger.Log($"Release asset {asset.Name} is unexpectedly large ({IOHelpers.FormatBytes(asset.Size)}). Aborting.", LogSeverity.Error);
            return ExitCodes.UpdateProcessError;
        }

        string exeDirectory = Path.GetDirectoryName(exePath)!;
        string stagedPath = exePath + ".update";
        try
        {
            TraceLogger.Log($"Downloading {asset.Name} ({latest})...", LogSeverity.Notice);
            using HttpClient client = CreateGitHubClient(TimeSpan.FromMinutes(10));
            using HttpResponseMessage response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] buffer = new byte[81_920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxExecutableBytes)
                    {
                        throw new InvalidDataException("Download exceeded the maximum executable size.");
                    }
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string expected = digest["sha256:".Length..].Trim().ToLowerInvariant();
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    AtomicFile.TryDelete(stagedPath);
                    TraceLogger.Log($"Downloaded update failed verification (SHA-256 {actual}, expected {expected}). Nothing was changed.", LogSeverity.Error);
                    return ExitCodes.UpdateProcessError;
                }
                TraceLogger.Log("Update verified against the SHA-256 digest published by GitHub.", LogSeverity.Debug);
            }
            else
            {
                TraceLogger.Log($"GitHub did not publish a digest for {asset.Name}; SHA-256 of the download is {actual}.", LogSeverity.Warn);
            }
        }
        catch (UnauthorizedAccessException)
        {
            AtomicFile.TryDelete(stagedPath);
            TraceLogger.Log($"Cannot write to {exeDirectory}. Run the update from an elevated prompt, or update manually.", LogSeverity.Error);
            return ExitCodes.UpdateProcessError;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AtomicFile.TryDelete(stagedPath);
            TraceLogger.Log($"Update download failed: {ex.Message}", LogSeverity.Error);
            return ExitCodes.UpdateProcessError;
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"hld-update-{Environment.ProcessId}.ps1");
        string script = BuildReplaceScript(Environment.ProcessId, stagedPath, exePath, scriptPath);
        File.WriteAllText(scriptPath, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath },
            UseShellExecute = false,
            CreateNoWindow = true
        });

        TraceLogger.Log($"Update to {latest} staged. HostlistDownloader will now exit so the executable can be replaced; this takes a few seconds.", LogSeverity.Notice);
        return ExitCodes.UpdateInProgress;
    }

    /// <summary>
    /// PowerShell that waits for THIS process (by its PID, passed in) to exit, then swaps the executable.
    /// Paths go into single-quoted strings with embedded quotes doubled, so unusual folder names cannot break out.
    /// </summary>
    internal static string BuildReplaceScript(int processId, string stagedPath, string exePath, string scriptPath)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $targetPid = {{processId.ToString(CultureInfo.InvariantCulture)}}
            $source = {{Quote(stagedPath)}}
            $destination = {{Quote(exePath)}}
            $deadline = (Get-Date).AddMinutes(2)
            while ((Get-Process -Id $targetPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
                Start-Sleep -Milliseconds 500
            }
            Start-Sleep -Milliseconds 500
            if (Test-Path -LiteralPath $source) {
                Move-Item -LiteralPath $source -Destination $destination -Force
            }
            Remove-Item -LiteralPath {{Quote(scriptPath)}} -Force -ErrorAction SilentlyContinue
            """;
    }

    internal static bool IsNewer(GitHubRelease release, out Version? latest)
    {
        latest = null;
        string? tag = release.TagName?.Trim();
        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }
        if (tag.StartsWith('v') || tag.StartsWith('V'))
        {
            tag = tag[1..];
        }
        int dash = tag.IndexOf('-');
        if (dash > 0)
        {
            tag = tag[..dash]; // 3.1.0-beta -> 3.1.0
        }
        if (!Version.TryParse(tag, out Version? parsed))
        {
            return false;
        }

        latest = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        var current = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(0, AppInfo.Version.Build));
        return latest > current;
    }

    private static GitHubAsset? SelectAsset(GitHubRelease release)
    {
        GitHubAsset[] executables = [.. (release.Assets ?? []).Where(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)];
        string rid = RuntimeInformation.RuntimeIdentifier; // e.g. win-x64
        return executables.FirstOrDefault(a => a.Name!.EndsWith($"-{rid}.exe", StringComparison.OrdinalIgnoreCase))
            ?? executables.FirstOrDefault(a => a.Name!.Equals("HostlistDownloader.exe", StringComparison.OrdinalIgnoreCase))
            ?? (executables.Length == 1 ? executables[0] : null);
    }

    private static async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = CreateGitHubClient(CheckTimeout);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using HttpResponseMessage response = await client.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // no releases yet
        }
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GitHubRelease);
    }

    private static HttpClient CreateGitHubClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.DefaultUserAgent);
        return client;
    }

    private static DateTimeOffset? ReadLastCheck()
    {
        try
        {
            if (File.Exists(Paths.UpdateCheckStampFile)
                && DateTimeOffset.TryParse(File.ReadAllText(Paths.UpdateCheckStampFile).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value))
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static void WriteLastCheck()
    {
        try
        {
            AtomicFile.WriteAllText(Paths.UpdateCheckStampFile, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
