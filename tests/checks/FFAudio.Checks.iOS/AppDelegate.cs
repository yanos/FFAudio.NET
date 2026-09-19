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
    // Report to a durable file for automation, stdout for live runs, and the
    // screen for disconnected devices. Keep prefixes aligned with Android.
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

        // Decoding must not block the UI thread and trigger the watchdog.
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

            // Rewrite the complete snapshot so automation never reads a partial line.
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
            // Distinguish runner failures from failed checks.
            Say(crashed.ToString());
            Say($"{TallyPrefix}0 passed, 1 failed (the run itself threw)");
        }
    }
}
