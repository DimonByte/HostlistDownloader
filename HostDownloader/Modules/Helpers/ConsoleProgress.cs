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

namespace HostlistDownloader.Modules.Helpers
{
    public static class ConsoleProgress
    {
        public static void ShowDownloadProgress(long downloadedBytes, long totalBytes, string fileName)
        {
            if (totalBytes > 0)
            {
                int percentage = (int)((downloadedBytes * 100) / totalBytes);
                int progressWidth = 50;
                int progressBlocks = (percentage * progressWidth) / 100;

                string progressBar = new string('█', progressBlocks) + new string('░', progressWidth - progressBlocks);

                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write($"[{progressBar}] {percentage}% - {fileName}\n");
            }
        }

        public static void ShowOperationProgress(int current, int total, string operationName)
        {
            if (total > 0)
            {
                double percentage = (double)current / total * 100;
                int progressWidth = 50;
                int progressBlocks = (int)(percentage * progressWidth / 100);

                string progressBar = new string('█', progressBlocks) + new string('░', progressWidth - progressBlocks);
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write($"[{progressBar}] {current}/{total} {operationName}\n");
            }
        }

        public static void ClearLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, currentLineCursor);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }
}
