using System;
using System.Linq;

using FFAudio.Checks;

namespace FFAudio.Checks.Desktop;

// Desktop runner mirroring the mobile output format. It loads the native from
// native/artifacts/<platform>/ or FFAUDIO_LIBRARY.
public static class Program
{
    private const string ResultPrefix = "FFAUDIO-CHECK ";
    private const string TallyPrefix = "FFAUDIO-CHECKS ";

    public static int Main()
    {
        try
        {
            var results = DecodeChecks.RunAll();

            foreach (var result in results)
                Console.WriteLine(ResultPrefix + result);

            var failed = results.Count(result => !result.Passed);
            Console.WriteLine($"{TallyPrefix}{results.Count - failed} passed, {failed} failed");

            return failed == 0 ? 0 : 1;
        }
        catch (Exception crashed)
        {
            // Distinguish runner failures from failed checks.
            Console.WriteLine(crashed.ToString());
            Console.WriteLine($"{TallyPrefix}0 passed, 1 failed (the run itself threw)");
            return 1;
        }
    }
}
