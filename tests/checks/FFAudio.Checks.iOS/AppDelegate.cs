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

    // The transcript so far, for CheckSceneDelegate's screen, which shows it
    // whenever it changes.
    public static string Snapshot { get; private set; } = "";

    public static event Action? TranscriptChanged;

    // The window belongs to CheckSceneDelegate: iOS 27 kills an app that has
    // not adopted the scene lifecycle at launch. The run does not wait for a
    // scene, so one that never connects costs the screen and nothing else.
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
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

            Snapshot = snapshot;
            TranscriptChanged?.Invoke();
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
