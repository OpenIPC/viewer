using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.Controls;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class SshTerminalContent : UserControl
{
    // Mobile soft keyboards can't focus the custom-drawn TerminalView, so an
    // invisible TextBox (ImeProxy) holds focus there and everything it receives
    // is forwarded into the shell. The box always contains this sentinel: typed
    // characters land after it, and an IME "delete" that eats the sentinel is
    // how we detect backspace (many IMEs send deleteSurroundingText instead of
    // a DEL key event, which never reaches KeyDown).
    private const string Sentinel = " ";
    private static readonly bool UseImeProxy = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    private readonly TaskCompletionSource<bool> _tcs = new();
    private TerminalView? _term;
    private TextBox? _proxy;
    private ToggleButton? _ctrlKey;
    private Button? _closeButton;
    private Avalonia.Threading.DispatcherTimer? _connectWhenSettled;
    private bool _suppressProxyChange;
    private bool _started;
    private bool _connected;
    private bool _autoKeyboard = true;
    // When the last touch selection gesture ended. The tap that ends one must not raise the
    // keyboard: the inset resizes the grid, rows spill into the scrollback and the selection
    // the user is still holding on to slides out from under the finger.
    private DateTime _selectionEndedAt = DateTime.MinValue;

    public Task<bool> Completion => _tcs.Task;

    public SshTerminalContent()
    {
        InitializeComponent();
        _term = this.FindControl<TerminalView>("Term");
        var close = this.FindControl<Button>("CloseButton")!;
        close.Click += OnClose;
        _closeButton = close;

        if (UseImeProxy)
            SetUpImeProxy();
    }

    private void SetUpImeProxy()
    {
        this.FindControl<Border>("KeyBar")!.IsVisible = true;

        _proxy = this.FindControl<TextBox>("ImeProxy")!;
        _proxy.IsVisible = true;
        // Tunnel so Enter/arrows/backspace are ours before the TextBox edits
        // its own text (which would corrupt the sentinel bookkeeping).
        _proxy.AddHandler(KeyDownEvent, OnProxyKeyDown, RoutingStrategies.Tunnel);
        _proxy.TextChanged += OnProxyTextChanged;
        ResetProxy();

        // The soft keyboard is dismissed with the system Back gesture and nothing on screen
        // obviously brings it back — a tap on the terminal does, but only if you think to try.
        this.FindControl<Button>("KeyShowKeyboard")!.Click += (_, _) => FocusInput();

        _ctrlKey = this.FindControl<ToggleButton>("KeyCtrl");
        WireKey("KeyEsc", "\x1b");
        WireKey("KeyTab", "\t");
        WireKey("KeyArrowUp", "\x1b[A");
        WireKey("KeyArrowDown", "\x1b[B");
        WireKey("KeyArrowRight", "\x1b[C");
        WireKey("KeyArrowLeft", "\x1b[D");
    }

    private void WireKey(string buttonName, string sequence)
    {
        this.FindControl<Button>(buttonName)!.Click += (_, _) =>
        {
            SendInput(sequence);
            // The key-bar buttons are not focusable, so the proxy still holds focus; this only
            // matters for bringing a dismissed keyboard back, which is opt-in.
            AutoFocusInput();
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        // Desktop hosts in a Window; mobile in an overlay awaiting Completion.
        if (VisualRoot is Window w)
            w.Close();
        else
            _tcs.TrySetResult(true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_started || DataContext is not SshTerminalViewModel vm || _term is null)
            return;
        _started = true;

        _term.Emulator = vm.Emulator;
        _term.TerminalFontSize = vm.FontSize;
        _term.Input += (_, text) => SendInput(text);
        _term.PasteRequested += (_, _) => _ = PasteAsync();
        _term.CopyRequested += (_, _) => _ = CopyAsync(_term.GetSelectedText());
        _autoKeyboard = vm.AutoKeyboard;
        // The touch gesture is over and the highlight is sized; the menu opens over it.
        _term.MenuRequested += (_, _) =>
        {
            _selectionEndedAt = DateTime.UtcNow;
            _term.ContextMenu?.Open(_term);
        };

        if (this.FindControl<MenuItem>("PasteMenuItem") is { } pasteItem)
            pasteItem.Click += (_, _) => _ = PasteAsync();
        if (this.FindControl<MenuItem>("CopyMenuItem") is { } copyItem)
            copyItem.Click += (_, _) => _ = CopyAsync(_term.GetSelectedText());
        if (this.FindControl<MenuItem>("CopyScreenMenuItem") is { } copyScreenItem)
            copyScreenItem.Click += (_, _) => _ = CopyAsync(_term.GetVisibleText());
        if (this.FindControl<ContextMenu>("TermMenu") is { } menu)
        {
            // "Copy" is only meaningful with something highlighted; on touch the gesture that
            // opens the menu has just made a selection, so it is normally live there too.
            menu.Opening += (_, _) =>
            {
                if (this.FindControl<MenuItem>("CopyMenuItem") is { } item)
                    item.IsEnabled = _term?.HasSelection == true;
                if (this.FindControl<MenuItem>("AutoKeyboardMenuItem") is { } keyboard)
                    keyboard.IsChecked = _autoKeyboard;
            };
            // The menu is a popup, and on mobile opening it takes focus off the IME proxy — which
            // takes the soft keyboard down with it. Nothing puts either back on its own, so after
            // a trip through the menu the terminal looked alive but swallowed everything typed at
            // it: no letters, no digits, not even Enter. Hand focus back as the menu closes.
            menu.Closed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
                AutoFocusInput, Avalonia.Threading.DispatcherPriority.Input);

            if (this.FindControl<MenuItem>("AutoKeyboardMenuItem") is { } keyboardItem)
            {
                // A phone-only choice: on desktop the keyboard is a piece of hardware.
                keyboardItem.IsVisible = UseImeProxy;
                if (this.FindControl<Separator>("KeyboardSeparator") is { } separator)
                    separator.IsVisible = UseImeProxy;
                // Avalonia does not flip IsChecked for a ToggleType item by itself — the tick is
                // ours to keep. Reading it back in the click handler is what made the item inert:
                // it still held the value we had set, so the setting never moved. The state lives
                // in _autoKeyboard now, and the tick is caught up every time the menu opens.
                keyboardItem.Click += (_, _) =>
                {
                    _autoKeyboard = !_autoKeyboard;
                    keyboardItem.IsChecked = _autoKeyboard;
                    _ = vm.SetAutoKeyboardAsync(_autoKeyboard);
                };
            }
        }
        // Defer the connect until the terminal has been measured: SSH.NET's
        // ShellStream can't be resized mid-session, so the PTY keeps whatever
        // size we open it with. Opening at the default 80x24 before layout means
        // a narrow (phone) view gets 80-column output that overflows off-screen.
        // Wait for the first real grid size, then open the shell to match.
        _term.GridResized += OnGridResized;

        if (UseImeProxy)
        {
            // Focus lives on the proxy; a tap on the terminal (e.g. after the
            // user dismissed the keyboard) re-focuses it so the IME reopens.
            // Tapped, not PointerPressed: a drag on the terminal scrolls its
            // history, and that must not throw the keyboard back up mid-read.
            _term.Focusable = false;
            _term.Tapped += (_, _) =>
            {
                // The release that finished a selection arrives here as a tap as well. Raising the
                // keyboard on it is what made a grabbed handle fly off with the scroll, so a tap
                // that close behind a selection is left alone.
                if (DateTime.UtcNow - _selectionEndedAt < TimeSpan.FromMilliseconds(600))
                    return;
                AutoFocusInput();
            };
        }
        // Focusing during attach is too early for Android's IME — the input
        // connection isn't wired yet, so the keyboard wouldn't open until the
        // user tapped the terminal. Post the initial focus past layout instead.
        Avalonia.Threading.Dispatcher.UIThread.Post(AutoFocusInput, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Focus for everything the user did not explicitly ask a keyboard for — opening the terminal,
    /// tapping it, closing the menu. With "keyboard on tap" off none of those bring the keyboard
    /// up; only the key-bar button does.
    /// </summary>
    private void AutoFocusInput()
    {
        if (_autoKeyboard || !UseImeProxy)
            FocusInput();
    }

    private void FocusInput()
    {
        if (!UseImeProxy)
        {
            _term?.Focus();
            return;
        }
        if (_proxy is null)
            return;

        // Focus() on a box that already holds focus is a no-op, and Android raises the soft keyboard
        // only when the focused text client *changes* — so once the user has dismissed the keyboard
        // the box still has focus and every tap meant to bring it back does nothing at all. Bounce
        // focus off the tree and back so the second Focus() is a real change. Only while the keyboard
        // is actually down: doing it on every call would blink it shut and open again on each tap of
        // the key bar.
        var top = TopLevel.GetTopLevel(this);
        if (_proxy.IsFocused && top?.InputPane?.State != InputPaneState.Open && _closeButton is not null)
        {
            // There is no "clear focus", so park it for one dispatcher turn on a control that is not
            // a text client. The IME closes, and focusing the box again right after is a change
            // Android acts on.
            _closeButton.Focus();
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _proxy?.Focus(), Avalonia.Threading.DispatcherPriority.Input);
            return;
        }

        _proxy.Focus();
    }

    // Everything user-typed funnels through here so the on-screen Ctrl key can
    // modify the next character regardless of which path produced it.
    private void SendInput(string text, bool applyCtrl = true)
    {
        if (DataContext is not SshTerminalViewModel vm || text.Length == 0)
            return;

        // Typing returns to the live screen — scrolled back into the history is no place to
        // watch your own keystrokes land.
        _term?.ScrollToLive();

        // Soft keyboards commit Enter as '\n'; shells expect CR.
        text = text.Replace('\n', '\r');

        if (applyCtrl && _ctrlKey?.IsChecked == true && text.Length == 1 && char.IsAsciiLetter(text[0]))
        {
            text = ((char)(char.ToUpperInvariant(text[0]) - 'A' + 1)).ToString();
            _ctrlKey.IsChecked = false;
        }
        _ = vm.SendAsync(text);
    }

    /// <summary>
    /// Sends the clipboard text into the shell. There is no in-terminal text editor to paste
    /// into — the bytes go straight down the PTY, exactly as if they had been typed.
    /// </summary>
    private async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch
        {
            // A clipboard read can fail on any platform (owner app gone, permission denied);
            // a failed paste is not worth tearing the session down.
            return;
        }

        if (string.IsNullOrEmpty(text))
            return;

        // A shell reads Enter as CR. Normalise both spellings so a multi-line paste runs its
        // lines instead of producing one long mangled command.
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        // Ctrl-latching a paste makes no sense — the toggle applies to the next typed key.
        SendInput(text, applyCtrl: false);
    }

    /// <summary>
    /// Puts terminal text on the clipboard. Nothing is sent to the shell — copying out of a
    /// terminal is a read of what is already on screen.
    /// </summary>
    private async Task CopyAsync(string? text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(text))
            return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch
        {
            // Same as paste: a clipboard the platform refused is not worth a crash.
            return;
        }

        _term?.ClearSelection();
    }

    private void OnProxyKeyDown(object? sender, KeyEventArgs e)
    {
        // Hardware Ctrl+letter (external keyboard on a tablet).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is >= Key.A and <= Key.Z)
        {
            SendInput(((char)(e.Key - Key.A + 1)).ToString());
            e.Handled = true;
            return;
        }

        var seq = e.Key switch
        {
            Key.Enter => "\r",
            Key.Back => "\x7f",
            Key.Tab => "\t",
            Key.Escape => "\x1b",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            _ => null,
        };
        if (seq is not null)
        {
            SendInput(seq);
            e.Handled = true;
        }
    }

    private void OnProxyTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressProxyChange || _proxy is null)
            return;

        var text = _proxy.Text ?? "";
        if (text == Sentinel)
            return;

        if (text.StartsWith(Sentinel, StringComparison.Ordinal))
            SendInput(text[Sentinel.Length..]);          // typed characters
        else if (text.Length < Sentinel.Length)
            SendInput("\x7f");                           // IME ate the sentinel → backspace
        else
            SendInput(text);                             // IME replaced everything (autocorrect) — best effort

        ResetProxy();
    }

    private void ResetProxy()
    {
        if (_proxy is null)
            return;
        _suppressProxyChange = true;
        _proxy.Text = Sentinel;
        _proxy.CaretIndex = Sentinel.Length;
        _suppressProxyChange = false;
    }

    private void OnGridResized(object? sender, (int Columns, int Rows) grid)
    {
        if (DataContext is not SshTerminalViewModel vm)
            return;
        _ = vm.ResizeAsync(grid.Columns, grid.Rows);

        // The pre-layout pass reports a 1x1 grid (Bounds still 0) — wait for a real measurement
        // before opening the shell at that size.
        if (_connected || grid.Columns <= 1 || grid.Rows <= 1)
            return;

        // And wait for the LAST of them, not the first. The PTY cannot be resized once it is open
        // (SshNetShell.Resize is a no-op — SSH.NET exposes no window-change), so the width we
        // connect at is the width the remote wraps every line at for the rest of the session.
        // Layout reaches its final width over several passes — the sheet animating in, the safe
        // area landing — and connecting on an early one locks the shell narrower than the screen:
        // output then stops short of the right edge and piles up against the left.
        _connectWhenSettled ??= NewSettleTimer();
        _connectWhenSettled.Stop();
        _connectWhenSettled.Start();
    }

    private Avalonia.Threading.DispatcherTimer NewSettleTimer()
    {
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_connected || DataContext is not SshTerminalViewModel vm)
                return;
            _connected = true;
            _ = vm.ConnectAsync();
        };
        return timer;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _connectWhenSettled?.Stop();
        _connectWhenSettled = null;
        _tcs.TrySetResult(true);
        if (DataContext is SshTerminalViewModel vm)
            _ = vm.DisposeAsync();
    }
}
