using System;
using System.IO;
using System.Threading;

namespace FFAudio.Tests;

// Deleting a temp directory a decoder has been reading out of.
//
// A `using var decoder` going out of scope does not always mean the fixture
// file is closed yet: a caller is free to close the native decoder on a
// background task, and Flower's does exactly that, because joining a decode
// thread mid-read of a slow network stream would stall its UI.
//
// On macOS and Linux that costs nothing: unlink on an open file succeeds and
// the inode goes when the last handle does. Windows refuses outright, and the
// test fails in its own Dispose - "the process cannot access the file
// 'fixture.wav' because it is being used by another process" - attributing a
// race in the teardown to whichever test happened to run last. Retrying for a
// couple of seconds is enough for a retire that is already under way, and the
// alternative (making Retire synchronous) would trade a tidy teardown for the
// UI stall it exists to avoid.
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
