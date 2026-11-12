using Foundation;

using UIKit;

namespace FFAudio.Checks.iOS;

// The app's one screen: the transcript, as it grows.
//
// A scene delegate rather than a window made in FinishedLaunching because
// iOS 27 enforces the scene lifecycle: an app that does not adopt it is killed
// at launch with EXC_BREAKPOINT in
// UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption, before a line
// of the run is written, which run.sh reports as a run that never finished.
// Info.plist names this class in UIApplicationSceneManifest.
[Register("CheckSceneDelegate")]
public class CheckSceneDelegate : UIWindowSceneDelegate
{
    private UITextView? _log;

    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
            return;

        Window = new UIWindow(windowScene);

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

        AppDelegate.TranscriptChanged += Refresh;
        Refresh();
    }

    private void Refresh() =>
        _log?.BeginInvokeOnMainThread(() =>
        {
            var text = AppDelegate.Snapshot;
            if (text.Length > 0)
                _log.Text = text;
        });
}
