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
/// Thrown when the program cannot continue. Caught once in Main, which flushes the log and exits with
/// <see cref="ExitCode"/>. This replaces calling Environment.Exit from deep inside the code, which could
/// kill the process from any thread (for example mid-download, mid-write).
/// </summary>
internal sealed class FatalException(string message, int exitCode, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Thrown by the SSRF guard when a host resolves only to internal or reserved addresses.
/// This is a permanent condition, so retry logic must not treat it as a transient network error.
/// </summary>
internal sealed class SsrfBlockedException(string message) : IOException(message);
