using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

// The form, extracted from CameraEditorWindow so the same control can host
// inside a Window (desktop) or in TopLevel.OverlayLayer (mobile). Owns the
// TaskCompletionSource that the caller awaits — the Window wrapper bridges
// completion to Window.Close, the overlay path returns it directly.
public sealed partial class CameraEditorContent : UserControl
{
    private readonly TaskCompletionSource<CameraEditorResult?> _tcs = new();
    private int _addressLength;

    public Task<CameraEditorResult?> Completion => _tcs.Task;

    // Raised when the header is pressed, so a borderless host window can drag.
    public event EventHandler<PointerPressedEventArgs>? HeaderPressed;

    public CameraEditorContent()
    {
        InitializeComponent();

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Finish(null);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Finish(null);
        this.FindControl<Button>("SaveButton")!.Click += OnSave;
        this.FindControl<Button>("DiscoverLink")!.Click += (_, _) =>
            Finish(new CameraEditorResult(null, null, Redirect: CameraEditorRedirect.Discover));
        this.FindControl<Button>("QrLink")!.Click += (_, _) =>
            Finish(new CameraEditorResult(null, null, Redirect: CameraEditorRedirect.ScanQr));
        this.FindControl<Border>("HeaderBar")!.PointerPressed += (_, e) => HeaderPressed?.Invoke(this, e);

        // Desktop has Ctrl+V and a right-click menu; the inline paste buttons
        // exist for mobile, where the long-press flyout is unreliable. QR import
        // picks an image file — a desktop flow.
        var isMobile = OverlayDialogPresenter.IsMobile;
        this.FindControl<Button>("PasteUsernameButton")!.IsVisible = isMobile;
        this.FindControl<Button>("PastePasswordButton")!.IsVisible = isMobile;
        this.FindControl<Button>("QrLink")!.IsVisible = !isMobile;
        this.FindControl<TextBlock>("QrSeparator")!.IsVisible = !isMobile;

        // The address box splits "rtsp://user:pass@host/..." and "host:port"
        // into the fields when focus leaves, or right after a paste (a jump of
        // several characters) — never per keystroke.
        var address = this.FindControl<TextBox>("AddressBox")!;
        address.LostFocus += (_, _) => (DataContext as CameraEditorViewModel)?.NormalizeAddress();
        address.TextChanged += (_, _) =>
        {
            var len = address.Text?.Length ?? 0;
            var jumped = len - _addressLength >= 6;
            _addressLength = len;
            if (jumped && address.Text!.Contains(':'))
                Dispatcher.UIThread.Post(() => (DataContext as CameraEditorViewModel)?.NormalizeAddress());
        };

        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is CameraEditorViewModel vm)
            {
                await vm.LoadGroupsAsync(CancellationToken.None);
                if (vm.IsNew) address.Focus();
                if (vm.AutoConnect && vm.ConnectCommand.CanExecute(null))
                    await vm.ConnectCommand.ExecuteAsync(null);
            }
        };
    }

    private void Finish(CameraEditorResult? result)
    {
        (DataContext as CameraEditorViewModel)?.CancelPending();
        _tcs.TrySetResult(result);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CameraEditorViewModel vm) return;
        if (!vm.TryBuildRequest(out var newRequest, out var updateRequest)) return;
        Finish(new CameraEditorResult(newRequest, updateRequest, vm.DetectedOnvif, vm.DetectedMajestic));
    }

    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CameraEditorViewModel vm || sender is not Button { Tag: string tag }) return;
        switch (tag)
        {
            case "streams": vm.IsStreamsOpen = !vm.IsStreamsOpen; break;
            case "ports": vm.IsPortsOpen = !vm.IsPortsOpen; break;
            case "ssh": vm.IsSshOpen = !vm.IsSshOpen; break;
            case "quality": vm.IsQualityOpen = !vm.IsQualityOpen; break;
            case "ai": vm.IsAiOpen = !vm.IsAiOpen; break;
        }
    }

    private void OnPasteUsername(object? sender, RoutedEventArgs e) =>
        PasteInto(this.FindControl<TextBox>("UsernameBox")!);

    private void OnPastePassword(object? sender, RoutedEventArgs e) =>
        PasteInto(this.FindControl<TextBox>("PasswordBox")!);

    private static void PasteInto(TextBox box)
    {
        box.Focus();
        box.Paste();
    }
}
