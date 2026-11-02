using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Views.InputMethods;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OpenIPC.Viewer.Android.Platform;

// Android TV: a text field opens the soft keyboard on OK, not on focus.
//
// Avalonia.Android shows the keyboard as soon as a TextBox gets the focus (AndroidInputMethod
// SetClient → ShowSoftInput). With a touch screen that is a tap on the field; with a TV remote
// it fires whenever the D-pad merely passes through one (the Library search box sits between the
// toolbar and the filters), and the keyboard then covers half the screen and takes the D-pad.
// On a TV this hides the keyboard again right after the focus lands, and shows it when OK
// (D-pad centre, which Avalonia maps to Key.Space, or Enter) is pressed on the field. Back closes
// it as usual. Phones and tablets are left alone.
internal static class TvSoftKeyboard
{
    private static Activity? _activity;
    // The focused field had its keyboard hidden by us and is waiting for OK.
    private static TextBox? _waitingForOk;

    public static void Install(Activity activity)
    {
        if (_activity is not null) return;
        var uiMode = activity.GetSystemService(Context.UiModeService) as UiModeManager;
        if (uiMode?.CurrentModeType != UiMode.TypeTelevision) return;
        _activity = activity;

        InputElement.GotFocusEvent.AddClassHandler<TextBox>(OnGotFocus, handledEventsToo: true);
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((box, _) =>
        {
            if (ReferenceEquals(_waitingForOk, box)) _waitingForOk = null;
        }, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnGotFocus(TextBox box, FocusChangedEventArgs e)
    {
        _waitingForOk = box;
        // Posted: Avalonia shows the keyboard while it hands the field to the input method,
        // around this focus change; hide it once that is done.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_waitingForOk, box)) SetKeyboard(show: false);
        }, DispatcherPriority.Background);
    }

    private static void OnKeyDown(TextBox box, KeyEventArgs e)
    {
        if (e.Handled || !ReferenceEquals(_waitingForOk, box)) return;
        if (e.Key is not (Key.Space or Key.Enter)) return;
        _waitingForOk = null;
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
