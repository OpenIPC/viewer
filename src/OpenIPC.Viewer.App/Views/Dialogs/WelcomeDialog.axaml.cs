using Avalonia.Controls;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class WelcomeDialog : Window
{
    public WelcomeDialog()
    {
        InitializeComponent();
        this.FindControl<Button>("DiscoverButton")!.Click += (_, _) => Close(WelcomeResult.Discover);
        this.FindControl<Button>("ScanQrButton")!.Click += (_, _) => Close(WelcomeResult.ScanQr);
        this.FindControl<Button>("AddManuallyButton")!.Click += (_, _) => Close(WelcomeResult.AddManually);
        this.FindControl<Button>("SkipButton")!.Click += (_, _) => Close(WelcomeResult.Skip);
    }
}
