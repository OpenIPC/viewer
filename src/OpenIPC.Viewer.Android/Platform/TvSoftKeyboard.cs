using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Views.InputMethods;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OpenIPC.Viewer.Android.Platform;

// Android TV: a text field opens the soft keyboard on OK, not when the D-pad reaches it.
//
// Avalonia.Android shows the keyboard as soon as a TextBox gets the focus (AndroidInputMethod
// SetClient → ShowSoftInput). With a touch screen that is a tap on the field; with a TV remote
// it fires whenever the D-pad merely passes through one (the Library search box sits between the
// toolbar and the filters), and the keyboard then covers half the screen and takes the D-pad.
// On a TV this hides the keyboard again when the focus reached the field by D-pad or Tab, and
// shows it when OK (D-pad centre, which Avalonia maps to Key.Space, or Enter) is pressed on a
// field whose keyboard is closed. Back closes it as usual. Focus set from code (the SSH
// terminal's keyboard button) is left alone, and so are phones and tablets.
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
        _activity = activity;
        if (_registered) return;
        _registered = true;

        InputElement.GotFocusEvent.AddClassHandler<TextBox>(OnGotFocus, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnKeyDown, RoutingStrategies.Tunnel);
    }

    public static void Uninstall(Activity activity)
    {
        if (ReferenceEquals(_activity, activity)) _activity = null;
    }

    private static void OnGotFocus(TextBox box, FocusChangedEventArgs e)
    {
        // Only the D-pad passing through: a focus set from code may be an explicit request for
        // the keyboard.
        if (e.NavigationMethod is not (NavigationMethod.Directional or NavigationMethod.Tab)) return;
        // Posted: Avalonia shows the keyboard while it hands the field to the input method,
        // around this focus change; hide it once that is done.
        Dispatcher.UIThread.Post(() =>
        {
            if (box.IsFocused) SetKeyboard(show: false);
        }, DispatcherPriority.Background);
    }

    private static void OnKeyDown(TextBox box, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Space or Key.Enter)) return;
        // With the keyboard up, the key is the keyboard's (or typing); only a closed keyboard —
        // never opened on this field, or closed with Back while it kept the focus — opens on OK.
        if (TopLevel.GetTopLevel(box)?.InputPane?.State == InputPaneState.Open) return;
        SetKeyboard(show: true);
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
