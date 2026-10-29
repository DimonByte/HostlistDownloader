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


using HostlistDownloader.Core;
using HostlistDownloader.Diagnostics;
using HostlistDownloader.Lists;

namespace HostlistDownloader.IO;

/// <summary>
/// --disable / --enable. Disabling moves each combined list to "name.disabled" and leaves an empty file in its
/// place, so anything watching the file (a DNS filter) stops blocking. While disabled, updates write to the
/// .disabled copies; enabling moves them back.
/// </summary>
internal static class ListSwitch
{
    internal static int Disable()
    {
        if (Paths.ListsDisabled)
        {
            TraceLogger.Log("The combined lists are already disabled. Use --enable to restore them.", LogSeverity.Notice);
            return ExitCodes.Success;
        }

        // Marker first: from here on, builds go to the .disabled copies, never over the empty placeholders.
        AtomicFile.WriteAllText(Paths.DisabledMarkerFile, DateTimeOffset.UtcNow.ToString("o"));
        foreach (string final in OutputTargets.Published.All)
        {
            if (File.Exists(final))
            {
                File.Move(final, final + Paths.DisabledSuffix, overwrite: true);
            }
            AtomicFile.WriteAllText(final, string.Empty);
        }

        TraceLogger.Log("Combined lists disabled: they are now empty files. Run with --enable to restore them.", LogSeverity.Notice);
        return ExitCodes.Success;
    }

    internal static int Enable()
    {
        if (!Paths.ListsDisabled)
        {
            TraceLogger.Log("The combined lists are not disabled.", LogSeverity.Notice);
            return ExitCodes.Success;
        }

        int missing = 0;
        foreach (string final in OutputTargets.Published.All)
        {
            string stash = final + Paths.DisabledSuffix;
            if (File.Exists(stash))
            {
                File.Move(stash, final, overwrite: true);
            }
            else
            {
                missing++;
                TraceLogger.Log($"No saved copy of {Path.GetFileName(final)} was found; it stays empty until the next update.", LogSeverity.Warn);
            }
        }

        File.Delete(Paths.DisabledMarkerFile);
        if (missing > 0)
        {
            BuildStamp.Clear(); // make the next update rebuild the missing files
        }
        TraceLogger.Log("Combined lists enabled.", LogSeverity.Notice);
        return ExitCodes.Success;
    }
}
