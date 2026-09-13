namespace HostlistDownloader.Modules.WindowsSystem.IO
{
    internal class Paths
    {
        internal static readonly string HostfilesLocation = "hostfiles";
        internal static readonly string BlockListFolderLocation = "hostfiles/blocklist";
        internal static readonly string WhiteListFolderLocation = "hostfiles/whitelist";
        internal static readonly string CombinedListFolderLocation = "hostfiles/combined";
        internal static readonly string CombinedBlockListFileLocation = "hostfiles/combined/HLDcombined-blocklist.txt";
        internal static readonly string CombinedWhiteListFileLocation = "hostfiles/combined/HLDcombined-whitelist.txt";
        internal static readonly string CombinedListFileLocation = "hostfiles/combined/HLDcombined-list.txt";
        internal static readonly string CombinedBlockListFileLocationTemp = "hostfiles/combined/HLDcombined-blocklist-TEMP.txt";
        internal static readonly string CombinedWhiteListFileLocationTemp = "hostfiles/combined/HLDcombined-whitelist-TEMP.txt";
        internal static readonly string CombinedListFileLocationTemp = "hostfiles/combined/HLDcombined-list-TEMP.txt";
        internal static readonly string LogsLocation = "logs";
        internal static readonly string UpdateStatsLocation = "logs/updatestats.txt";
        internal static readonly string SettingJsonFileLocation = "settings.json";
    }
}
