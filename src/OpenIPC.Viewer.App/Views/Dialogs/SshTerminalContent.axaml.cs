using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
    private bool _suppressProxyChange;
    private bool _started;
    private bool _connected;

    public Task<bool> Completion => _tcs.Task;

    public SshTerminalContent()
    {
        InitializeComponent();
        _term = this.FindControl<TerminalView>("Term");
        var close = this.FindControl<Button>("CloseButton")!;
        close.Click += OnClose;

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
            FocusInput();
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
            _term.Focusable = false;
            _term.PointerPressed += (_, _) => FocusInput();
        }
        // Focusing during attach is too early for Android's IME — the input
        // connection isn't wired yet, so the keyboard wouldn't open until the
        // user tapped the terminal. Post the initial focus past layout instead.
        Avalonia.Threading.Dispatcher.UIThread.Post(FocusInput, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void FocusInput()
    {
        if (UseImeProxy)
            _proxy?.Focus();
        else
            _term?.Focus();
    }

    // Everything user-typed funnels through here so the on-screen Ctrl key can
    // modify the next character regardless of which path produced it.
    private void SendInput(string text)
    {
        if (DataContext is not SshTerminalViewModel vm || text.Length == 0)
            return;

        // Soft keyboards commit Enter as '\n'; shells expect CR.
        text = text.Replace('\n', '\r');

        if (_ctrlKey?.IsChecked == true && text.Length == 1 && char.IsAsciiLetter(text[0]))
        {
            text = ((char)(char.ToUpperInvariant(text[0]) - 'A' + 1)).ToString();
            _ctrlKey.IsChecked = false;
        }
        _ = vm.SendAsync(text);
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
        // The pre-layout pass reports a 1x1 grid (Bounds still 0) — wait for a
        // real measurement before opening the shell at that size.
        if (!_connected && grid.Columns > 1 && grid.Rows > 1)
        {
            _connected = true;
            _ = vm.ConnectAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tcs.TrySetResult(true);
        if (DataContext is SshTerminalViewModel vm)
            _ = vm.DisposeAsync();
    }
}
