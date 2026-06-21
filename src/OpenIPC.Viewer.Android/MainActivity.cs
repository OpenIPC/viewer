using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace OpenIPC.Viewer.Android;

[Activity(
    Label = "OpenIPC Viewer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                          | ConfigChanges.UiMode | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity
{
    // All composition + early-startup work runs in MainApplication.OnCreate
    // so App.Services is populated before Avalonia's OnFrameworkInitializationCompleted
    // fires. This activity is the launcher entry; AvaloniaMainActivity does
    // the rest (window + view setup).

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Phase 17.6 — request the mic permission up front so push-to-talk works
        // without an inline prompt. Denied → AudioRecord reports unavailable and
        // the talk button just fails gracefully.
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            RequestPermissions(new[] { global::Android.Manifest.Permission.RecordAudio }, 17_06);
    }
}
