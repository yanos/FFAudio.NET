using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace FFAudio.Checks.Android;

// Name is pinned rather than left to the generated crc64 one: the driver
// script has to name this activity to `am start` it, and a generated name
// changes whenever the namespace does.
[Activity(Name = "com.yanos.ffaudio.checks.MainActivity",
          Label = "FFAudio checks", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    // The output contract, in three places at once because each is the only
    // one that works somewhere:
    //
    //  - a file in the app's private files directory, which is what
    //    tests/checks/FFAudio.Checks.Android/run.sh reads back through `run-as`. It is
    //    the reliable one: logcat is a ring buffer shared with the whole
    //    system, so a long transcript competing with a chatty emulator can
    //    lose lines, and a run that decoded everything but reported half its
    //    tally is indistinguishable from a failing one.
    //  - logcat, which is what a run watched live shows.
    //  - the screen, so a run on a phone with no cable attached is readable
    //    by the person holding it.
    //
    // Deliberately the same three, under the same prefixes, as the iOS
    // runner's AppDelegate.
    private const string ResultPrefix = "FFAUDIO-CHECK ";
    private const string TallyPrefix = "FFAUDIO-CHECKS ";
    private const string LogTag = "FFAudioChecks";

    public const string TranscriptName = "ffaudio-checks.log";

    private TextView? _log;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _log = new TextView(this)
        {
            Text = "Running...",
            Typeface = global::Android.Graphics.Typeface.Monospace,
            TextSize = 9f,
        };

        var scroller = new ScrollView(this);
        scroller.AddView(_log);
        SetContentView(scroller, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        // Off the UI thread: the checks block on decoding for seconds at a
        // time, and an ANR kill halfway through would look like a failing
        // check rather than a blocked main thread.
        Task.Run(RunChecks);
    }

    private string TranscriptPath => Path.Combine(FilesDir!.AbsolutePath, TranscriptName);

    private void RunChecks()
    {
        var transcript = new StringBuilder();

        void Say(string line)
        {
            global::Android.Util.Log.Info(LogTag, line);
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
                global::Android.Util.Log.Warn(LogTag, $"could not write the transcript: {unwritable.Message}");
            }

            RunOnUiThread(() => _log!.Text = snapshot);
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
