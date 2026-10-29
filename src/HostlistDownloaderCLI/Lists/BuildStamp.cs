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
using HostlistDownloader.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HostlistDownloader.Lists;

/// <summary>
/// Fingerprint of everything that affects the generated output: program version, format and output options,
/// user entries, neverBlock, and the set of cached source files (name, size, modification time).
/// When nothing downloaded and the fingerprint is unchanged, the outputs are already current and are left alone.
/// When it changed (for example formattype or a user rule was edited), the outputs are rebuilt from the cache
/// without needing a download.
/// </summary>
internal static class BuildStamp
{
    internal static string Compute(AppConfig config, IReadOnlyList<SourceFile> sources)
    {
        var sb = new StringBuilder();
        sb.Append("v=").AppendLine(AppInfo.ShortVersion);
        sb.Append("format=").AppendLine(config.Format.CanonicalName());
        OutputSettings o = config.Output;
        sb.Append(CultureInfo.InvariantCulture, $"output={o.Sort}|{config.NewLine.Length}|{o.Header}|{config.HostsBlockIp}|{o.Compress}|{o.ValidateDomains}|{o.RejectPublicSuffixes}").AppendLine();
        AppendList(sb, "userBlock", config.UserBlocklist);
        AppendList(sb, "userAllow", config.UserWhitelist);
        AppendList(sb, "neverBlock", config.NeverBlock);

        foreach (SourceFile source in sources.OrderBy(s => s.Kind).ThenBy(s => s.Path, StringComparer.Ordinal))
        {
            var info = new FileInfo(source.Path);
            sb.Append(CultureInfo.InvariantCulture, $"src={source.Kind}|{info.Name}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}").AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    internal static string? Read()
    {
        try
        {
            return File.Exists(Paths.BuildStampFile) ? File.ReadAllText(Paths.BuildStampFile).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Write(string stamp)
    {
        try
        {
            AtomicFile.WriteAllText(Paths.BuildStampFile, stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Worst case the next run rebuilds unnecessarily.
        }
    }

    internal static void Clear() => AtomicFile.TryDelete(Paths.BuildStampFile);

    private static void AppendList(StringBuilder sb, string name, IReadOnlyList<string> values)
    {
        sb.Append(name).Append('=');
        foreach (string value in values)
        {
            sb.Append(value).Append('\u001F');
        }
        sb.AppendLine();
    }
}
