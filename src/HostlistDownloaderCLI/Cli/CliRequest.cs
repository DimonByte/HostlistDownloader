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

namespace HostlistDownloader.Cli;

internal enum CommandKind
{
    Update,
    Merge,
    Search,
    Stats,
    Diff,
    Revert,
    SelfUpdate,
    Validate,
    DuplicateScan,
    DuplicateAnalyse,
    GetSource,
    EditConfig,
    Disable,
    Enable,
    Help,
    Version
}

/// <summary>Options that apply to every command.</summary>
internal sealed record GlobalOptions(
    bool Quiet = false,
    bool Debug = false,
    bool NoBanner = false,
    string? DataDirectory = null,
    bool NoUpdateCheck = false,
    bool PurgeLogs = false);

/// <summary>Options for the default update command.</summary>
internal sealed record RunOptions(bool Fresh = false);

/// <summary>The parsed command line: exactly one command plus its options.</summary>
internal sealed record CliRequest(
    CommandKind Command,
    GlobalOptions Global,
    RunOptions Run,
    string? Argument,
    ValidationMode ValidationMode,
    IReadOnlyList<ConfigEdit> Edits,
    IReadOnlyList<string> DeprecatedArguments);
