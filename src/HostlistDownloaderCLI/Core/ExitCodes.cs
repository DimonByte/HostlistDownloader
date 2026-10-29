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

namespace HostlistDownloader.Core;

/// <summary>
/// Process exit codes. The numeric values are part of the public contract (scheduled tasks and scripts check them),
/// so existing values must never be renumbered.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;

    // General errors
    public const int GeneralError = 1;
    public const int NetworkConnectionFailed = 2;
    public const int FirstRunSetupCompleted = 3; // settings.json was just created; nothing to do until it is configured.
    public const int AlreadyRunning = 4; // Another HostlistDownloader process is using the same data folder.

    // File system errors
    public const int DirectoryCreationFailed = 10;

    // Configuration errors
    public const int FileMissing = 20;
    public const int ConfigurationCorrupted = 21;
    public const int InvalidConfigEntry = 22; // settings.json exists and parses, but a value in it cannot be used.

    // Command line errors
    public const int MissingOrInvalidParameters = 30; // A command was given without its required value.
    public const int InvalidArguments = 31; // Unknown argument, or conflicting commands.

    // Update process errors
    public const int UpdateProcessError = 40; // Hostlist update failed outright.
    public const int PartialUpdateWithIssues = 41; // Hostlists updated, but at least one source had a problem.
    public const int IntegrityCheckFailure = 42; // Generated output failed validation and was not published.
    public const int TaskThreadTimeout = 43;

    // Environment errors
    public const int UpdateInProgress = 51; // Self-update downloaded; process exits so the helper script can swap the executable.

    public const int Cancelled = 130; // Ctrl+C (same value shells use for SIGINT).

    public static string GetDescription(int exitCode) => exitCode switch
    {
        Success => "Success",
        GeneralError => "General error occurred",
        NetworkConnectionFailed => "Network connection failed",
        FirstRunSetupCompleted => "First run setup completed, settings.json needs configuring",
        AlreadyRunning => "Another HostlistDownloader process is already running",
        DirectoryCreationFailed => "Failed to create directory",
        FileMissing => "Critical file missing",
        ConfigurationCorrupted => "Configuration file corruption detected",
        InvalidConfigEntry => "Invalid configuration entry detected",
        MissingOrInvalidParameters => "Missing or invalid command value",
        InvalidArguments => "Unknown or conflicting command line arguments",
        UpdateProcessError => "Error during update process",
        PartialUpdateWithIssues => "Update completed with issues",
        IntegrityCheckFailure => "Data validation check failed",
        TaskThreadTimeout => "A multi-threaded task has reached a timeout threshold",
        UpdateInProgress => "Application update is in progress",
        Cancelled => "Cancelled by user",
        _ => "Unknown error occurred"
    };
}
