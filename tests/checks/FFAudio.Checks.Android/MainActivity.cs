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

// Pin the activity name used by the driver script.
[Activity(Name = "com.yanos.ffaudio.checks.MainActivity",
          Label = "FFAudio checks", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    // Report to a durable file for automation, logcat for live runs, and the
    // screen for disconnected devices. Keep prefixes aligned with iOS.
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

        // Decoding must not block the UI thread and trigger an ANR.
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

            // Rewrite the complete snapshot so automation never reads a partial line.
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
            // Distinguish runner failures from failed checks.
            Say(crashed.ToString());
            Say($"{TallyPrefix}0 passed, 1 failed (the run itself threw)");
        }
    }
}
