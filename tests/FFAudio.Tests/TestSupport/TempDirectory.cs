using System;
using System.IO;
using System.Threading;

namespace FFAudio.Tests;

// Retry cleanup while asynchronous decoder shutdown releases files on Windows.
public static class TempDirectory
{
    public static void DeleteWhenReleased(string path, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(25);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }
}
