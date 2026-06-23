using Avalonia;
using Avalonia.Controls;

namespace OpenIPC.Viewer.App.Behaviors;

/// <summary>
/// Attached behaviour that toggles the <c>compact</c> style class on a control
/// when its own measured width drops below <see cref="CompactBelowProperty"/>.
///
/// Lets a page reflow for phone-width viewports straight from XAML style
/// selectors (e.g. <c>Selector="DockPanel.compact &gt; ...."</c>) without
/// hand-written per-page <c>SizeChanged</c> code-behind. It mirrors the 700px
/// desktop/mobile breakpoint in <see cref="Views.MainView"/>, but scoped to an
/// individual control rather than the whole window — useful because a panel's
/// own width (after sidebar + padding) is what actually decides whether its
/// children fit, not the raw window width.
/// </summary>
public static class Adaptive
{
    public static readonly AttachedProperty<double> CompactBelowProperty =
        AvaloniaProperty.RegisterAttached<Control, double>(
            "CompactBelow", typeof(Adaptive), double.NaN);

    public static void SetCompactBelow(Control control, double value) =>
        control.SetValue(CompactBelowProperty, value);

    public static double GetCompactBelow(Control control) =>
        control.GetValue(CompactBelowProperty);

    static Adaptive()
    {
        CompactBelowProperty.Changed.AddClassHandler<Control>(OnCompactBelowChanged);
    }

    private static void OnCompactBelowChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        // Idempotent — detach first so re-setting the threshold doesn't stack
        // handlers.
        control.SizeChanged -= OnSizeChanged;
        if (!double.IsNaN(GetCompactBelow(control)))
        {
            control.SizeChanged += OnSizeChanged;
            Apply(control, control.Bounds.Width);
        }
    }

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control control)
            Apply(control, e.NewSize.Width);
    }

    private static void Apply(Control control, double width)
    {
        var threshold = GetCompactBelow(control);
        if (double.IsNaN(threshold))
            return;

        // Width is 0 until the first layout pass — treat "unknown" as wide so a
        // page doesn't flash its compact form before it has been measured.
        var compact = width > 0 && width < threshold;
        if (compact == control.Classes.Contains("compact"))
            return;

        if (compact)
            control.Classes.Add("compact");
        else
            control.Classes.Remove("compact");
    }
}
