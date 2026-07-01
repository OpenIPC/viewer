using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace OpenIPC.Viewer.Android;

[Activity(
    Label = "OpenIPC Viewer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    // AdjustResize shrinks the TopLevel client area when the soft keyboard opens
    // (instead of the keyboard overlaying content). The overlay dialog presenter
    // caps its bottom-sheet to ClientSize and wraps content in a ScrollViewer, so
    // this lets a focused field (e.g. ONVIF login/password at the bottom of the
    // add sheet) scroll into view above the keyboard. StateHidden keeps the
    // keyboard down until the user taps a field.
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden,
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

        // Request the mic (Phase 17.6 push-to-talk) and notifications (Phase 19.3)
        // permissions up front. Denied → those features degrade gracefully.
        var wanted = new System.Collections.Generic.List<string>();
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            wanted.Add(global::Android.Manifest.Permission.RecordAudio);
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            wanted.Add(global::Android.Manifest.Permission.PostNotifications);
        if (wanted.Count > 0)
            RequestPermissions(wanted.ToArray(), 1906);
    }
}
