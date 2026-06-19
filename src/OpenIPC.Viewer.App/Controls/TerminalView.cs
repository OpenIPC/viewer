using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Ssh.Terminal;

namespace OpenIPC.Viewer.App.Controls;

/// <summary>
/// Renders a <see cref="TerminalEmulator"/> grid and turns keyboard input into
/// the bytes a shell expects (phase-13 §13.3). Drawn entirely in
/// <see cref="Render"/> — monospace text per row, background fills per cell,
/// a block cursor — so there are no child controls to hit-test.
/// </summary>
public sealed class TerminalView : Control
{
    // Fixed 16-color ANSI palette (terminals don't follow the app theme).
    private static readonly Color[] Palette =
    {
        Color.Parse("#1e1e1e"), Color.Parse("#cd3131"), Color.Parse("#0dbc79"), Color.Parse("#e5e510"),
        Color.Parse("#2472c8"), Color.Parse("#bc3fbc"), Color.Parse("#11a8cd"), Color.Parse("#cccccc"),
        Color.Parse("#666666"), Color.Parse("#f14c4c"), Color.Parse("#23d18b"), Color.Parse("#f5f543"),
        Color.Parse("#3b8eea"), Color.Parse("#d670d6"), Color.Parse("#29b8db"), Color.Parse("#ffffff"),
    };

    private static readonly Color DefaultFg = Color.Parse("#d4d4d4");
    private static readonly Color DefaultBg = Color.Parse("#0c0f14");
    private static readonly IBrush DefaultFgBrush = new SolidColorBrush(DefaultFg);
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.Parse("#d4d4d4"));

    public static readonly StyledProperty<TerminalEmulator?> EmulatorProperty =
        AvaloniaProperty.Register<TerminalView, TerminalEmulator?>(nameof(Emulator));

    public TerminalEmulator? Emulator
    {
        get => GetValue(EmulatorProperty);
        set => SetValue(EmulatorProperty, value);
    }

    public static readonly StyledProperty<double> TerminalFontSizeProperty =
        AvaloniaProperty.Register<TerminalView, double>(nameof(TerminalFontSize), 14);

    public double TerminalFontSize
    {
        get => GetValue(TerminalFontSizeProperty);
        set => SetValue(TerminalFontSizeProperty, value);
    }

    /// <summary>Raised with raw text/control bytes the user typed.</summary>
    public event EventHandler<string>? Input;

    /// <summary>Raised when the available size maps to a new column/row count.</summary>
    public event EventHandler<(int Columns, int Rows)>? GridResized;

    private readonly Typeface _typeface = new(new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"));
    private readonly Typeface _boldTypeface =
        new(new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), weight: FontWeight.Bold);

    private double _cellWidth;
    private double _cellHeight;
    private TerminalEmulator? _subscribed;
    private int _lastCols = -1;
    private int _lastRows = -1;

    public TerminalView()
    {
        Focusable = true;
        EnsureMetrics();
        this.GetObservable(BoundsProperty).Subscribe(_ => RecomputeGrid());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TerminalFontSizeProperty)
        {
            EnsureMetrics();
            RecomputeGrid();
            InvalidateVisual();
            return;
        }

        if (change.Property != EmulatorProperty)
            return;

        if (_subscribed is not null)
            _subscribed.Updated -= OnEmulatorUpdated;
        _subscribed = Emulator;
        if (_subscribed is not null)
            _subscribed.Updated += OnEmulatorUpdated;
        RecomputeGrid();
        InvalidateVisual();
    }

    // Updated fires on the UI thread (the VM marshals shell data), so a direct
    // invalidate is safe.
    private void OnEmulatorUpdated() => InvalidateVisual();

    private void EnsureMetrics()
    {
        var sample = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, TerminalFontSize, DefaultFgBrush);
        _cellWidth = sample.Width;
        _cellHeight = sample.Height;
    }

    private void RecomputeGrid()
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
            return;

        var cols = Math.Max(1, (int)(Bounds.Width / _cellWidth));
        var rows = Math.Max(1, (int)(Bounds.Height / _cellHeight));
        if (cols == _lastCols && rows == _lastRows)
            return;

        _lastCols = cols;
        _lastRows = rows;
        GridResized?.Invoke(this, (cols, rows));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(DefaultBg), new Rect(Bounds.Size));

        var emu = Emulator;
        if (emu is null)
            return;

        for (var row = 0; row < emu.Rows; row++)
        {
            var cells = emu.GetRow(row);
            var y = row * _cellHeight;
            DrawRow(context, cells, y);
        }

        DrawCursor(context, emu);
    }

    private void DrawRow(DrawingContext context, TerminalCell[] cells, double y)
    {
        // Background fills first (only non-default cells).
        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            if (cell.Background == TerminalPalette.DefaultBackground)
                continue;
            context.FillRectangle(
                new SolidColorBrush(Palette[cell.Background & 0x0F]),
                new Rect(c * _cellWidth, y, _cellWidth, _cellHeight));
        }

        // Text in runs of equal foreground/bold to cut FormattedText churn.
        var run = new StringBuilder();
        var runStart = 0;
        byte runFg = cells.Length > 0 ? cells[0].Foreground : TerminalPalette.DefaultForeground;
        var runBold = cells.Length > 0 && cells[0].Bold;

        void Flush(int end)
        {
            if (run.Length == 0) return;
            var brush = runFg == TerminalPalette.DefaultForeground
                ? DefaultFgBrush
                : new SolidColorBrush(Palette[runFg & 0x0F]);
            var ft = new FormattedText(run.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                runBold ? _boldTypeface : _typeface, TerminalFontSize, brush);
            context.DrawText(ft, new Point(runStart * _cellWidth, y));
            run.Clear();
        }

        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            if (c == 0 || cell.Foreground != runFg || cell.Bold != runBold)
            {
                Flush(c);
                runStart = c;
                runFg = cell.Foreground;
                runBold = cell.Bold;
            }
            run.Append(cell.Char);
        }
        Flush(cells.Length);
    }

    private void DrawCursor(DrawingContext context, TerminalEmulator emu)
    {
        var x = emu.CursorColumn * _cellWidth;
        var y = emu.CursorRow * _cellHeight;
        // Hollow block so the character under the cursor stays readable.
        context.DrawRectangle(null, new Pen(CursorBrush, 1),
            new Rect(x, y, _cellWidth, _cellHeight));
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text))
        {
            Input?.Invoke(this, e.Text);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl+letter -> control byte (Ctrl+C = 0x03, etc.).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is >= Key.A and <= Key.Z)
        {
            var b = (char)(e.Key - Key.A + 1);
            Input?.Invoke(this, b.ToString());
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
            Input?.Invoke(this, seq);
            e.Handled = true;
        }
    }
}
