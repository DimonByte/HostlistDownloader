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

// ErrorCodes.cs - Centralized error code definitions

namespace HostlistDownloader.Modules
{
    public static class ErrorCodes
    {

        // General errors
        public const int GeneralError = 1;
        public const int NetworkConnectionFailed = 2;

        // File system errors
        public const int DirectoryCreationFailed = 10;

        // Configuration errors
        public const int ConfigurationFileMissing = 20;
        public const int ConfigurationCorrupted = 21;
        public const int InvalidConfigEntry = 22; //Configuration file is present, but the attempt to use configuration failed.

        // Update process errors
        public const int UpdateProcessError = 40; //Hostfiles update failed outright.
        public const int PartialUpdateWithIssues = 41; //Hostfiles updates partially but some might've timed out.
        // Internal failures
        public const int IntegrityCheckFailure = 42; //Thrown when an operation output is checked and the output differs from what we expect.
        public const int TaskThreadTimeout = 43;
        // Environment errors
        public const int WrongExecutionDirectory = 50;

        // Helper method to get error description
        public static string GetDescription(int errorCode)
        {
            return errorCode switch
            {
                GeneralError => "General error occurred",
                NetworkConnectionFailed => "Network connection failed",
                DirectoryCreationFailed => "Failed to create directory",
                ConfigurationFileMissing => "Critical configuration file missing",
                ConfigurationCorrupted => "Configuration file corruption detected",
                InvalidConfigEntry => "Invalid configuration entry detected",
                UpdateProcessError => "Error during update process",
                PartialUpdateWithIssues => "Update completed with issues",
                WrongExecutionDirectory => "Program executed from incorrect directory",
                TaskThreadTimeout => "A multi-threaded task has reached a timeout threshold",
                IntegrityCheckFailure => "Data validation check failed",
                _ => "Unknown error occurred"
            };
        }
    }
}