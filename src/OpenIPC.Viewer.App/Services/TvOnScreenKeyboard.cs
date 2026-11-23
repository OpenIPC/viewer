using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Services;

// Android TV: the system keyboard does not get the remote. Avalonia keeps the
// focus, so Left/Right/Up/Down leave the text field instead of moving across
// the keys. This grid is ordinary buttons, so the same D-pad focus that walks
// the rest of the app walks the keys, and OK types the focused one.
public static class TvOnScreenKeyboard
{
    private static Control? _layer;
    private static IDisposable? _sizeSub;
    private static TextBox? _target;
    private static bool _shifted;
    private static TextBlock? _preview;
    private static readonly List<Button> _letters = new();

    public static bool IsOpen => _layer is not null;

    // Set by the TV head. Phones keep the system keyboard, so a request from
    // the SSH key bar only opens this grid on a television.
    public static bool Enabled { get; set; }

    public static void ShowRequested(TextBox box)
    {
        if (!Enabled) return;
        Show(box);
    }

    public static void Show(TextBox box)
    {
        if (box.IsReadOnly || !box.IsEffectivelyVisible) return;
        if (ReferenceEquals(_target, box) && _layer is not null) return;
        Dismiss();
        var overlay = Overlay();
        if (overlay is null) return;

        _target = box;
        _shifted = false;
        _letters.Clear();

        var panel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(12, 10, 12, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _preview = new TextBlock
        {
            FontSize = 18,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextPrimaryBrush", 240, 240, 240),
            Margin = new Thickness(4, 0, 4, 4),
        };
        panel.Children.Add(_preview);
        RefreshPreview();

        AddRow(panel, "1234567890");
        AddRow(panel, "qwertyuiop");
        AddRow(panel, "asdfghjkl");
        AddRow(panel, "zxcvbnm");
        AddRow(panel, ".-_@:/");
        AddRow(panel, "?&=+#*!");

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        actions.Children.Add(ActionButton("ABC", ToggleShift));
        actions.Children.Add(ActionButton("space", () => Type(" ")));
        actions.Children.Add(ActionButton("⌫", Backspace));
        actions.Children.Add(ActionButton("OK", DismissAndRestoreFocus));
        panel.Children.Add(actions);

        var card = new Border
        {
            Background = Brush("Bg1Brush", 28, 28, 32),
            BorderBrush = Brush("BorderMediumBrush", 80, 80, 88),
            BorderThickness = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = panel,
            Focusable = true,
        };
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);
        card.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        // OverlayLayer is a Canvas: a child is not stretched, so size it ourselves.
        var host = new Border
        {
            Background = Brushes.Transparent,
            Child = card,
        };
        void SizeTo(Size s)
        {
            if (s.Width <= 0 || s.Height <= 0) return;
            host.Width = s.Width;
            host.Height = s.Height;
        }
        var top = TopLevel.GetTopLevel(overlay);
        if (top is not null) SizeTo(top.ClientSize);
        _sizeSub = top?.GetObservable(TopLevel.ClientSizeProperty).Subscribe(SizeTo);

        overlay.Children.Add(host);
        _layer = host;

        Dispatcher.UIThread.Post(() =>
        {
            if (_letters.Count > 0)
                _letters[0].Focus(NavigationMethod.Directional);
            else
                card.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Background);
    }

    public static bool TryDismiss()
    {
        if (_layer is null) return false;
        DismissAndRestoreFocus();
        return true;
    }

    // Activity teardown. Clears the static open state even when the overlay
    // that owned the grid has already gone, so Back is not swallowed afterwards.
    public static void Reset()
    {
        _sizeSub?.Dispose();
        _sizeSub = null;
        if (_layer is not null)
        {
            Overlay()?.Children.Remove(_layer);
            _layer = null;
        }
        _target = null;
        _preview = null;
        _letters.Clear();
        _shifted = false;
    }

    private static void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _layer is null) return;
        // Key.Back is Backspace. The remote's Back button arrives as a back
        // request and dismisses the grid there; this key has to delete.
        if (e.Key == Key.Back)
        {
            Backspace();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            DismissAndRestoreFocus();
            e.Handled = true;
            return;
        }

        var direction = e.Key switch
        {
            Key.Up => NavigationDirection.Up,
            Key.Down => NavigationDirection.Down,
            Key.Left => NavigationDirection.Left,
            Key.Right => NavigationDirection.Right,
            _ => (NavigationDirection?)null,
        };
        if (direction is null || sender is not Control root) return;

        var focus = TopLevel.GetTopLevel(root)?.FocusManager;
        (focus?.FindNextElement(direction.Value, new FindNextElementOptions { SearchRoot = root })
            as InputElement)?.Focus(NavigationMethod.Directional);
        // At the edge, stay on the keyboard. Leaving would land back on the page.
        e.Handled = true;
    }

    private static void AddRow(StackPanel panel, string keys)
    {
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        foreach (var ch in keys)
        {
            var letter = char.IsLetter(ch);
            var button = KeyButton(Label(ch), () => Type(Label(ch)));
            if (letter) _letters.Add(button);
            row.Children.Add(button);
        }
        panel.Children.Add(row);
    }

    private static string Label(char ch) =>
        _shifted && char.IsLetter(ch) ? char.ToUpperInvariant(ch).ToString() : ch.ToString();

    private static Button KeyButton(string label, Action onPress)
    {
        var button = new Button
        {
            Content = label,
            Width = 46,
            Height = 42,
            Margin = new Thickness(3),
            Padding = new Thickness(0),
            FontSize = 18,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush("Bg2Brush", 48, 48, 54),
            Foreground = Brush("TextPrimaryBrush", 240, 240, 240),
            CornerRadius = new CornerRadius(6),
        };
        button.Click += (_, _) => onPress();
        return button;
    }

    private static Button ActionButton(string label, Action onPress)
    {
        var button = KeyButton(label, onPress);
        button.Width = double.NaN;
        button.MinWidth = 72;
        button.Padding = new Thickness(12, 0);
        return button;
    }

    private static void ToggleShift()
    {
        _shifted = !_shifted;
        foreach (var button in _letters)
        {
            if (button.Content is string text && text.Length == 1)
                button.Content = _shifted ? text.ToUpperInvariant() : text.ToLowerInvariant();
        }
    }

    private static void Type(string text)
    {
        var box = _target;
        if (box is null || box.IsReadOnly) return;
        var current = box.Text ?? "";
        var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        box.Text = string.Concat(current.AsSpan(0, start), text, current.AsSpan(end));
        box.CaretIndex = start + text.Length;
        RefreshPreview();
    }

    private static void Backspace()
    {
        var box = _target;
        if (box is null || box.IsReadOnly) return;
        var current = box.Text ?? "";
        if (current.Length == 0) return;
        var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        if (start == end)
        {
            if (start == 0) return;
            start--;
        }
        box.Text = string.Concat(current.AsSpan(0, start), current.AsSpan(end));
        box.CaretIndex = start;
        RefreshPreview();
    }

    private static void RefreshPreview()
    {
        if (_preview is null || _target is null) return;
        var text = _target.Text ?? "";
        _preview.Text = _target.PasswordChar != default(char) && _target.PasswordChar != '\0'
            ? new string('•', text.Length)
            : text;
    }

    private static void DismissAndRestoreFocus()
    {
        var box = _target;
        Dismiss();
        if (box is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (box.IsEffectivelyVisible)
                box.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Background);
    }

    private static void Dismiss() => Reset();

    private static Panel? Overlay()
    {
        Control? root = null;
        var lifetime = Application.Current?.ApplicationLifetime;
        if (lifetime is ISingleViewApplicationLifetime single)
            root = single.MainView;
        else if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            root = desktop.MainWindow;
        if (root is null) return null;
        var top = TopLevel.GetTopLevel(root);
        return top is null ? null : OverlayLayer.GetOverlayLayer(top);
    }

    private static IBrush Brush(string key, byte r, byte g, byte b)
    {
        if (Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}
