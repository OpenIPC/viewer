using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Views.InputMethods;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenIPC.Viewer.App.Services;

namespace OpenIPC.Viewer.Android.Platform;

// Android TV: a text field opens an on-screen keyboard on OK, not when the D-pad reaches it.
//
// Avalonia.Android shows the system keyboard as soon as a TextBox gets the focus. On a TV the
// remote's arrows never reach that keyboard: Avalonia still owns the focus, so Left/Right/Up/Down
// move to the next control and the field is abandoned. The system keyboard is kept closed, and
// OK (D-pad centre, which Avalonia maps to Key.Space, or Enter) opens TvOnScreenKeyboard, whose
// keys are ordinary buttons the same D-pad can walk. A tap opens it too. Focus set from code
// (the SSH terminal's keyboard button) is left alone, and so are phones and tablets.
internal static class TvSoftKeyboard
{
    private static bool _registered;
    // The activity whose window the keyboard belongs to. Replaced when Android recreates the
    // activity, cleared when it is destroyed.
    private static Activity? _activity;

    public static void Install(Activity activity)
    {
        var uiMode = activity.GetSystemService(Context.UiModeService) as UiModeManager;
        if (uiMode?.CurrentModeType != UiMode.TypeTelevision) return;
        var replaced = _activity is not null && !ReferenceEquals(_activity, activity);
        _activity = activity;
        TvOnScreenKeyboard.Enabled = true;
        if (replaced) TvOnScreenKeyboard.Reset();
        if (_registered) return;
        _registered = true;

        InputElement.GotFocusEvent.AddClassHandler<TextBox>(OnGotFocus, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnKeyDown, RoutingStrategies.Tunnel);
    }

    public static void Uninstall(Activity activity)
    {
        if (!ReferenceEquals(_activity, activity)) return;
        // While this activity's tree can still drop the grid. A later activity
        // must not inherit IsOpen from a layer that no longer exists.
        TvOnScreenKeyboard.Reset();
        TvOnScreenKeyboard.Enabled = false;
        _activity = null;
    }

    private static void OnGotFocus(TextBox box, FocusChangedEventArgs e)
    {
        // Posted: Avalonia shows the system keyboard while it hands the field to the input
        // method, around this focus change. Close it once that is done. A tap (pointer) also
        // opens our key grid; D-pad focus waits for OK so passing through a field stays quiet.
        // Focus set from code is neither, and is left alone.
        var fromPointer = e.NavigationMethod == NavigationMethod.Pointer;
        Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsFocused) return;
            SetKeyboard(show: false);
            if (fromPointer) TvOnScreenKeyboard.Show(box);
        }, DispatcherPriority.Background);
    }

    private static void OnKeyDown(TextBox box, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Space or Key.Enter)) return;
        if (TvOnScreenKeyboard.IsOpen) return;
        TvOnScreenKeyboard.Show(box);
        // OK only opens the keyboard: no space typed, no Enter action.
        e.Handled = true;
    }

    private static void SetKeyboard(bool show)
    {
        if (_activity?.GetSystemService(Context.InputMethodService) is not InputMethodManager imm) return;
        // Avalonia's view holds the Android focus and the input connection.
        var view = _activity.CurrentFocus ?? _activity.Window?.DecorView;
        if (view is null) return;
        if (show)
            imm.ShowSoftInput(view, ShowFlags.Implicit);
        else
            imm.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
    }
}
