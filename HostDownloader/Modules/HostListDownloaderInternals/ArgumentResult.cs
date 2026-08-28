namespace HostlistDownloader.Modules.HostListDownloaderInternals
{
    public record ArgumentResult(
        bool IsQuiet,
        bool IsFresh,
        string? SearchDomain,
        bool ShouldPurgeLogs,
        List<string> RemainingArgs,
        bool ShowHelp,
        string? AddBlocklistUrl,
        string? RemoveBlocklistUrl,
        string? AddWhitelistUrl,
        string? RemoveWhitelistUrl,
        string? AddUserBlockDomain,
        string? RemoveUserBlockDomain,
        string? AddUserAllowDomain,
        string? RemoveUserAllowDomain,
        bool DebugMode,
        bool CheckDuplicate,
        string? GetSourceName,
        string? AnalyseDuplicateSource,
        bool MergeMode,
        bool UpdateCheck
    );
}