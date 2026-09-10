using System;
using System.Linq;

using FFAudio.Checks;

namespace FFAudio.Checks.Desktop;

// The desktop head of the check suite: a Main, a loop, and an exit code.
//
// Deliberately the twin of the iOS and Android runners beside it, down to the
// FFAUDIO-CHECK and FFAUDIO-CHECKS prefixes, because the runs are only worth
// comparing if a reader can compare them line for line. Where those two write
// a transcript to a container a driver script digs into - a phone having no
// reliable stdout - this one has a console and an exit code, so it needs
// neither.
//
//     dotnet run --project checks/FFAudio.Checks.Desktop
//
// A native has to exist for it to find: native/artifacts/<platform>/, which
// is where native/build-all.sh puts one, or FFAUDIO_LIBRARY pointing at one.
// Without that the first check fails and says so, which is the honest answer
// rather than a crash.
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
            // A throw out here is not a failed check, it is the checks being
            // unable to run at all, and that has to read differently from a
            // handful of honest failures.
            Console.WriteLine(crashed.ToString());
            Console.WriteLine($"{TallyPrefix}0 passed, 1 failed (the run itself threw)");
            return 1;
        }
    }
}
