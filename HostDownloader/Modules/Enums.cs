namespace HostlistDownloader.Modules
{
    public class Enums
    {
        public enum StatusSeverityType
        {
            Information = 0,
            Warning = 1,
            Error = 2,
            Debug = 3,
            Fatal = 4,
            Important = 5 //Used to log even if debug mode is disabled. This is for important information that should be logged regardless of debug mode.
        }
    }
}
