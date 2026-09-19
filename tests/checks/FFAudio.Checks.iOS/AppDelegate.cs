using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Foundation;

using UIKit;

namespace FFAudio.Checks.iOS;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    // The output contract, in three places at once because each is the only
    // one that works somewhere:
    //
    //  - a file in the app's Documents directory, which is what
    //    tests/checks/FFAudio.Checks.iOS/run.sh reads. Console.WriteLine from a .NET
    //    iOS app does not reliably reach `simctl launch --console-pty`, and a
    //    run that decodes correctly but reports nothing is indistinguishable
    //    from a hang. A file in a container the script can find its way into
    //    has no such failure mode.
    //  - stdout, which a device run launched through devicectl --console
    //    shows live.
    //  - the screen, so a run on a phone with no cable attached is readable
    //    by the person holding it.
    //
    // Deliberately the same three, under the same prefixes, as the Android
    // runner beside it: the two runs are only worth comparing if a reader can
    // compare them line for line.
    private const string ResultPrefix = "FFAUDIO-CHECK ";
    private const string TallyPrefix = "FFAUDIO-CHECKS ";

    public const string TranscriptName = "ffaudio-checks.log";

    public override UIWindow? Window { get; set; }

    private UITextView? _log;

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);

        _log = new UITextView(Window.Bounds)
        {
            Editable = false,
            Font = UIFont.FromName("Menlo", 11) ?? UIFont.SystemFontOfSize(11),
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
            Text = "Running...",
        };

        var root = new UIViewController();
        root.View!.AddSubview(_log);
        Window.RootViewController = root;
        Window.MakeKeyAndVisible();

        // Off the UI thread: the checks block on decoding for seconds at a
        // time, and a watchdog kill halfway through would look like a failing
        // check rather than a hung main thread.
        Task.Run(RunChecks);

        return true;
    }

    private static string TranscriptPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), TranscriptName);

    private void RunChecks()
    {
        var transcript = new StringBuilder();

        void Say(string line)
        {
            Console.WriteLine(line);
            transcript.AppendLine(line);

            var snapshot = transcript.ToString();

            // Rewritten whole each time rather than appended: the script may
            // read it at any moment, and a partial line would be read as a
            // missing tally.
            try
            {
                File.WriteAllText(TranscriptPath, snapshot);
            }
            catch (Exception unwritable)
            {
                Console.WriteLine($"could not write the transcript: {unwritable.Message}");
            }

            UIApplication.SharedApplication.InvokeOnMainThread(() => _log!.Text = snapshot);
        }

        try
        {
            var results = DecodeChecks.RunAll();

            foreach (var result in results)
                Say(ResultPrefix + result);

            var failed = results.Count(result => !result.Passed);
            Say($"{TallyPrefix}{results.Count - failed} passed, {failed} failed");
        }
        catch (Exception crashed)
        {
            // A throw out here is not a failed check, it is the checks being
            // unable to run at all, and that has to read differently from a
            // handful of honest failures.
            Say(crashed.ToString());
            Say($"{TallyPrefix}0 passed, 1 failed (the run itself threw)");
        }
    }
}
