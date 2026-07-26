namespace HostlistDownloader.Modules.DownloadSystem
{
    internal class NetworkChecker
    {
        public static bool IsNetworkAvailable()
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var pingResult = ping.Send("1.1.1.1", 1000);
                return pingResult != null && pingResult.Status == System.Net.NetworkInformation.IPStatus.Success;
            }
            catch
            {
                try
                {
                    var hostEntry = System.Net.Dns.GetHostEntry("duck.com");
                    return hostEntry != null && hostEntry.AddressList.Length > 0;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}