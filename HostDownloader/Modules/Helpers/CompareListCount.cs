namespace HostlistDownloader.Modules.Helpers
{
    internal class CompareListCount
    {
        //This will receive the combined list location, then a int of the line count of the list, then another function called compare will read the lines again but in a different int to compare.
        //E.g. CompareListCount.InitCompare("C:\\Users\\User\\Downloads\\combinedlist.txt")
        //CompareListCount.CompleteCount("C:\\Users\\User\\Downloads\\combinedlist.txt")
        //Int1: 15, int2: 20,
        //Output: "The combined list has had 5+/- entries added"
        static int lineCount = 0;
        static int finalCount = 0;

        public static void InitCompare(string filePath)
        {
            lineCount = File.ReadLines(filePath).Count();
            TraceLogger.Log($"Initial line count: {lineCount}");
        }
        public static void CompleteCount(string filePath, int initialCount)
        {
            finalCount = File.ReadLines(filePath).Count();
            int difference = finalCount - initialCount;
            TraceLogger.Log($"Final line count: {finalCount}");
            TraceLogger.Log($"Difference: {difference} entries added/removed");
        }
    }
}
