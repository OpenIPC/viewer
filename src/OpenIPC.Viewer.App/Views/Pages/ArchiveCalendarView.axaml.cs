using Avalonia;
using Avalonia.Controls;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class ArchiveCalendarView : UserControl
{
    // Phone layout: a full-width card with a week strip that expands to the
    // month, instead of the fixed 240px month column. Set by the host page's
    // compact style; surfaces as the :compact pseudo-class.
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<ArchiveCalendarView, bool>(nameof(IsCompact));

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public ArchiveCalendarView() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty)
            PseudoClasses.Set(":compact", IsCompact);
    }
}
