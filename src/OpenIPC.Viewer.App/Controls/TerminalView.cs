using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>Raised when the user asks for a clipboard paste (Ctrl+V, Shift+Insert, menu).</summary>
    public event EventHandler? PasteRequested;

    /// <summary>Raised when the user asks to copy (Ctrl+Shift+C, Ctrl+Insert).</summary>
    public event EventHandler? CopyRequested;

    /// <summary>
    /// Raised when a touch selection gesture ends — the long-press picked a word, the drag that
    /// may have followed sized it, and the menu belongs over the result.
    /// </summary>
    public event EventHandler? MenuRequested;

    // Desktop names first, then the two families Android actually ships, then the generic alias.
    // If any of them resolves we get real monospace metrics; DrawRow below is correct either way.
    private const string MonoFamilies = "Cascadia Mono,Consolas,Menlo,Droid Sans Mono,Roboto Mono,monospace";

    private readonly Typeface _typeface = new(new FontFamily(MonoFamilies));
    private readonly Typeface _boldTypeface =
        new(new FontFamily(MonoFamilies), weight: FontWeight.Bold);

    private double _cellWidth;
    private double _cellHeight;
    private readonly Dictionary<(char Char, byte Foreground, bool Bold), FormattedText> _glyphs = new();
    private TerminalEmulator? _subscribed;
    // Rows the view is scrolled back by; 0 is the live screen.
    private int _scrollOffset;
    private double _dragAnchorY;
    private bool _dragging;
    private int _lastCols = -1;
    private int _lastRows = -1;

    // Selection, in absolute row coordinates: scrollback rows are 0..ScrollbackRows-1 and the
    // live screen continues from there. Rows are appended at the end, so a row keeps its index as
    // output pushes the screen upwards and a selection survives a burst of output. The one
    // exception is the 1000-row scrollback cap: past it the oldest rows are dropped and the
    // indices below shift with them, which can slide a highlight left standing for that long.
    private (int Row, int Col)? _selAnchor;
    private (int Row, int Col)? _selFocus;
    private bool _selecting;
    // A touch selection is the one that opens the menu when the finger comes up.
    private bool _selectingByTouch;
    // Grab handles under the two ends of a touch selection, the way the platform's own text
    // selection works: word-at-a-time is a coarse instrument, and the handles are what turn it
    // into "from here to there". Only drawn for a selection a finger made.
    private bool _handlesVisible;
    private Handle _grabbed = Handle.None;
    private Vector _grabOffset;

    private enum Handle { None, Start, End }

    private const double HandleRadius = 9;
    // Generous, because a fingertip is: the knob is 18px across and the nearest one inside this
    // radius wins, so the handles stay catchable without hijacking taps elsewhere.
    private const double HandleTouchSlop = 30;

    private static readonly IBrush SelectionBrush =
        new SolidColorBrush(Color.FromArgb(0x66, 0x3b, 0x8e, 0xea));
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.Parse("#3b8eea"));

    public TerminalView()
    {
        Focusable = true;
        // Long-press is the only way to reach the menu on a phone (see OnHolding).
        SetValue(InputElement.IsHoldingEnabledProperty, true);
        EnsureMetrics();
        this.GetObservable(BoundsProperty).Subscribe(_ => RecomputeGrid());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TerminalFontSizeProperty)
        {
            _glyphs.Clear();
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

        var history = emu.ScrollbackRows;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, history);

        // The visible window is the last emu.Rows of (scrollback + screen), moved back by however
        // far the user has scrolled. A phone shows about twenty rows, so anything longer than that
        // is only readable if the rows above can be brought back.
        var top = history - _scrollOffset;
        var selection = NormalizedSelection();
        for (var row = 0; row < emu.Rows; row++)
        {
            var index = top + row;
            var cells = index < history ? emu.GetScrollbackRow(index) : emu.GetRow(index - history);
            var (selStart, selEnd) = SelectedColumns(selection, index, cells.Length);
            DrawRow(context, cells, row * _cellHeight, selStart, selEnd);
        }

        // The cursor belongs to the live screen; drawing it over history would be a lie.
        if (_scrollOffset == 0)
            DrawCursor(context, emu);

        if (_handlesVisible && selection is not null)
        {
            DrawHandle(context, true);
            DrawHandle(context, false);
        }
    }

    // A bar down the edge of the selection plus a knob just clear of the row, so the knob never
    // sits on the text it belongs to. On the bottom row there is no room below and it goes above.
    private void DrawHandle(DrawingContext context, bool start)
    {
        if (HandleAnchor(start) is not { } anchor)
            return;

        var rowTop = anchor.Y - _cellHeight / 2;
        context.FillRectangle(HandleBrush, new Rect(anchor.X - 1, rowTop, 2, _cellHeight));

        var below = rowTop + _cellHeight + HandleRadius;
        var y = below + HandleRadius <= Bounds.Height ? below : rowTop - HandleRadius;
        context.DrawEllipse(HandleBrush, null, new Point(anchor.X, y), HandleRadius, HandleRadius);
    }

    // Where a handle points: the middle of its end of the selection, in view coordinates. Null
    // when that end has been scrolled off the screen.
    private Point? HandleAnchor(bool start)
    {
        var emu = Emulator;
        if (emu is null || NormalizedSelection() is not { } selection || _cellHeight <= 0)
            return null;

        var (row, col) = start ? selection.Start : selection.End;
        var viewRow = row - (emu.ScrollbackRows - _scrollOffset);
        if (viewRow < 0 || viewRow >= emu.Rows)
            return null;

        return new Point(col * _cellWidth, viewRow * _cellHeight + _cellHeight / 2);
    }

    // The handle a finger landed on, if it landed on one. The knob is below the row it belongs
    // to, so the whole span from the row down past the knob counts as a hit.
    private Handle HandleAt(Point point)
    {
        if (!_handlesVisible)
            return Handle.None;

        var best = Handle.None;
        var bestDistance = HandleTouchSlop;
        foreach (var start in new[] { true, false })
        {
            if (HandleAnchor(start) is not { } anchor)
                continue;
            // Measured from the knob, which is where the finger aims.
            var knob = new Point(anchor.X, anchor.Y + _cellHeight / 2 + HandleRadius);
            var distance = Math.Sqrt(
                ((point.X - knob.X) * (point.X - knob.X)) + ((point.Y - knob.Y) * (point.Y - knob.Y)));
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = start ? Handle.Start : Handle.End;
        }
        return best;
    }

    /// <summary>Back to the live screen — what typing into a scrolled-back terminal does.</summary>
    public void ScrollToLive()
    {
        if (_scrollOffset == 0)
            return;
        _scrollOffset = 0;
        InvalidateVisual();
    }

    private void ScrollBy(int rows)
    {
        var history = Emulator?.ScrollbackRows ?? 0;
        var next = Math.Clamp(_scrollOffset + rows, 0, history);
        if (next == _scrollOffset)
            return;
        _scrollOffset = next;
        InvalidateVisual();
    }

    // --- Selection -------------------------------------------------------

    /// <summary>True when something is highlighted and <see cref="GetSelectedText"/> has content.</summary>
    public bool HasSelection => NormalizedSelection() is not null;

    /// <summary>Drops the highlight (a tap, a copy that has been taken, closing the menu).</summary>
    public void ClearSelection()
    {
        if (_selAnchor is null && _selFocus is null)
            return;
        _selAnchor = null;
        _selFocus = null;
        _handlesVisible = false;
        InvalidateVisual();
    }

    /// <summary>Highlights the whole line under <paramref name="point"/>.</summary>
    public void SelectLineAt(Point point)
    {
        var emu = Emulator;
        if (emu is null)
            return;

        var (row, _) = CellAt(point);
        _selAnchor = (row, 0);
        _selFocus = (row, RowAt(emu, row).Length);
        InvalidateVisual();
    }

    /// <summary>
    /// Highlights the word under <paramref name="point"/>, or the whole line when the spot is
    /// blank. The starting point of a touch selection: a word is the smallest thing worth reaching
    /// for, and the drag that follows grows it to whatever the user actually wants.
    /// </summary>
    public void SelectWordAt(Point point)
    {
        var emu = Emulator;
        if (emu is null)
            return;

        var (row, _) = CellAt(point);
        var cells = RowAt(emu, row);
        var text = TextOf(cells, 0, cells.Length);
        // The column the finger is over, not the nearest gap between columns.
        var col = _cellWidth > 0 ? (int)(point.X / _cellWidth) : 0;

        // Nothing under the finger: a hold on blank space is reaching for the line it is on.
        if (text.Length == 0 || col >= text.Length || col < 0 || text[col] == ' ')
        {
            SelectLineAt(point);
            return;
        }

        var start = col;
        while (start > 0 && text[start - 1] != ' ')
            start--;
        var end = col;
        while (end < text.Length && text[end] != ' ')
            end++;

        _selAnchor = (row, start);
        _selFocus = (row, end);
        InvalidateVisual();
    }

    /// <summary>The highlighted text, or null when nothing is highlighted.</summary>
    public string? GetSelectedText()
    {
        var emu = Emulator;
        if (emu is null || NormalizedSelection() is not { } selection)
            return null;

        var (start, end) = selection;
        var lines = new List<string>();
        for (var row = start.Row; row <= end.Row; row++)
        {
            var cells = RowAt(emu, row);
            var from = row == start.Row ? Math.Clamp(start.Col, 0, cells.Length) : 0;
            var to = row == end.Row ? Math.Clamp(end.Col, 0, cells.Length) : cells.Length;
            lines.Add(TextOf(cells, from, to));
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Everything on screen right now — what a touch user gets when copying without a highlight.</summary>
    public string GetVisibleText()
    {
        var emu = Emulator;
        if (emu is null)
            return "";

        var top = emu.ScrollbackRows - _scrollOffset;
        var lines = new List<string>();
        for (var row = 0; row < emu.Rows; row++)
        {
            var cells = RowAt(emu, top + row);
            lines.Add(TextOf(cells, 0, cells.Length));
        }
        // Trailing blank rows are the unused part of the screen, not content.
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return string.Join(Environment.NewLine, lines);
    }

    private static string TextOf(TerminalCell[] cells, int from, int to)
    {
        var buffer = new char[Math.Max(0, to - from)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var ch = cells[from + i].Char;
            // A never-written cell holds NUL; on the way out it is a space like any other blank.
            buffer[i] = ch < ' ' ? ' ' : ch;
        }
        return new string(buffer).TrimEnd();
    }

    private static TerminalCell[] RowAt(TerminalEmulator emu, int absoluteRow)
    {
        var history = emu.ScrollbackRows;
        if (absoluteRow < 0)
            return Array.Empty<TerminalCell>();
        if (absoluteRow < history)
            return emu.GetScrollbackRow(absoluteRow);
        var screenRow = absoluteRow - history;
        return screenRow < emu.Rows ? emu.GetRow(screenRow) : Array.Empty<TerminalCell>();
    }

    // Point -> (absolute row, column boundary). The column rounds to the nearest edge so a
    // selection ends where the pointer looks like it ended, not half a character short.
    private (int Row, int Col) CellAt(Point point)
    {
        var emu = Emulator;
        if (emu is null || _cellWidth <= 0 || _cellHeight <= 0)
            return (0, 0);

        var top = emu.ScrollbackRows - _scrollOffset;
        var viewRow = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, emu.Rows - 1));
        var col = Math.Clamp((int)Math.Round(point.X / _cellWidth), 0, emu.Columns);
        return (top + viewRow, col);
    }

    // Anchor and focus in the order they appear on screen; null when the two are the same
    // position, which is a click rather than a selection.
    private ((int Row, int Col) Start, (int Row, int Col) End)? NormalizedSelection()
    {
        if (_selAnchor is not { } anchor || _selFocus is not { } focus)
            return null;
        if (anchor == focus)
            return null;

        var anchorFirst = anchor.Row < focus.Row || (anchor.Row == focus.Row && anchor.Col <= focus.Col);
        return anchorFirst ? (anchor, focus) : (focus, anchor);
    }

    private static (int Start, int End) SelectedColumns(
        ((int Row, int Col) Start, (int Row, int Col) End)? selection, int absoluteRow, int rowLength)
    {
        if (selection is not { } range)
            return (0, 0);

        var (start, end) = range;
        if (absoluteRow < start.Row || absoluteRow > end.Row)
            return (0, 0);

        var from = absoluteRow == start.Row ? Math.Clamp(start.Col, 0, rowLength) : 0;
        var to = absoluteRow == end.Row ? Math.Clamp(end.Col, 0, rowLength) : rowLength;
        return to > from ? (from, to) : (0, 0);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ScrollBy((int)Math.Round(e.Delta.Y) * 3);
        e.Handled = true;
    }

    // Two different drags, decided by what is doing the dragging. A mouse selects text, the way
    // every desktop terminal does, and scrolls with the wheel. A finger scrolls the history —
    // there is no wheel, and selecting by dragging would make the scrollback unreachable.
    // Deliberately not a gesture recognizer: a tap still has to reach the host so it can raise the
    // soft keyboard, and only movement past a whole row counts as scrolling.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        if (e.Pointer.Type == PointerType.Mouse &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selecting = true;
            _selAnchor = CellAt(point);
            _selFocus = _selAnchor;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        if (e.Pointer.Type != PointerType.Mouse)
        {
            // A finger on one of the handles moves that end of the selection. The other end
            // becomes the anchor, which is all the drag handler needs to know — from there it is
            // the same code that sized the selection in the first place.
            if (HandleAt(point) is var grabbed && grabbed != Handle.None &&
                NormalizedSelection() is { } selection)
            {
                _grabbed = grabbed;
                var moving = grabbed == Handle.Start ? selection.Start : selection.End;
                _selAnchor = grabbed == Handle.Start ? selection.End : selection.Start;
                _selFocus = moving;
                // Keep the grip: the cell the handle points at stays under the same part of the
                // finger, instead of jumping to wherever the fingertip happens to land.
                _grabOffset = (HandleAnchor(grabbed == Handle.Start) ?? point) - point;
                _selecting = true;
                _selectingByTouch = true;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }

            // Any other press (a tap, a right-click that is about to open the menu) drops a stale
            // selection — except the right-click itself, which needs the selection it is acting on.
            ClearSelection();
        }

        _dragAnchorY = point.Y;
        _dragging = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_selecting)
        {
            var at = e.GetPosition(this);

            // A touch selection starts from a long-press, which never captured the pointer —
            // take it now so the finger can leave the control without dropping the drag.
            if (_selectingByTouch && e.Pointer.Captured != this)
                e.Pointer.Capture(this);

            // Dragging against either edge walks the view, so a selection can run past the top of
            // the screen into the scrollback and past the bottom into the newest output.
            if (_cellHeight > 0)
            {
                if (at.Y < _cellHeight)
                    ScrollBy(-1);
                else if (at.Y > Bounds.Height - _cellHeight)
                    ScrollBy(1);
            }

            _selFocus = CellAt(_grabbed == Handle.None ? at : at + _grabOffset);
            InvalidateVisual();
            return;
        }

        if (!_dragging || _cellHeight <= 0)
            return;

        var dy = e.GetPosition(this).Y - _dragAnchorY;
        var rows = (int)(dy / _cellHeight);
        if (rows == 0)
            return;

        // Pull down to go back in time, the way every list on the platform behaves.
        _dragAnchorY += rows * _cellHeight;
        ScrollBy(rows);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;

        if (!_selecting)
            return;

        // Read and clear the touch flag BEFORE dropping the capture: releasing it raises
        // PointerCaptureLost right here, synchronously, and that handler used to clear the flag
        // this method was about to test — which is why the menu stopped opening at all.
        var byTouch = _selectingByTouch;
        _selecting = false;
        _selectingByTouch = false;
        _grabbed = Handle.None;
        e.Pointer.Capture(null);

        // A click that never moved is not a selection — leave nothing highlighted.
        if (NormalizedSelection() is null)
            ClearSelection();

        // The finger is up and the highlight is final: now the menu can open over it, and the
        // handles come out so the ends can still be moved afterwards. Opening it back when the
        // press was recognised instead would have covered the very text being selected.
        if (byTouch)
        {
            _handlesVisible = HasSelection;
            MenuRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
        _selecting = false;
        _grabbed = Handle.None;

        // Something took the gesture away mid-selection (the system, or a popup opening). The
        // highlight stands, so finish the job the release would have done.
        if (!_selectingByTouch)
            return;
        _selectingByTouch = false;
        _handlesVisible = HasSelection;
        MenuRequested?.Invoke(this, EventArgs.Empty);
    }

    // Touch long-press: there is no right mouse button on a phone, so the hold is what starts a
    // selection. It takes the word under the finger and then hands the gesture over to the drag
    // handler — keeping the finger down and moving it grows the highlight a character at a time,
    // across rows and into the scrollback, and the menu opens when the finger comes up.
    //
    // The hold used to grab the whole line and open the menu immediately, which made a line the
    // only thing a touch user could ever copy: half a path, one field of a listing or a single
    // number out of a status line were all out of reach.
    protected override void OnHolding(HoldingRoutedEventArgs e)
    {
        base.OnHolding(e);
        if (e.HoldingState != HoldingState.Started || e.PointerType != PointerType.Touch)
            return;

        SelectWordAt(e.Position);
        // The hold cancels the scroll drag it grew out of, so the view doesn't fling mid-selection.
        _dragging = false;
        _selecting = true;
        _selectingByTouch = true;
        // Handles from the first moment, so both ends of the selection are visible while it grows.
        _handlesVisible = true;
        e.Handled = true;
    }

    private void DrawRow(DrawingContext context, TerminalCell[] cells, double y, int selStart, int selEnd)
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

        // Selection sits over the cell backgrounds and under the glyphs, so highlighted text
        // stays readable and coloured output keeps its own background showing through.
        if (selEnd > selStart)
            context.FillRectangle(SelectionBrush,
                new Rect(selStart * _cellWidth, y, (selEnd - selStart) * _cellWidth, _cellHeight));

        // One character at a time, each pinned to its own cell.
        //
        // Handing the text engine a whole run and letting it lay the run out itself is what a normal
        // control does, and it is wrong here: the run is only monospaced if the platform actually has
        // a monospaced font. None of Cascadia / Consolas / Menlo exists on Android, and what it falls
        // back to is proportional — so a row of text drew about 70% as wide as the grid it belongs
        // to. Lines stopped short of the right edge, the cursor sat well past the end of the line it
        // was on, and the whole screen read as squashed against the left.
        //
        // Per-cell drawing costs a DrawText per visible character, which is why the FormattedText
        // objects are cached: a terminal repaints on arriving output, not per frame, and the set of
        // (character, colour, weight) it ever shows is small.
        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            // Everything at or below U+0020 is blank or a control code: nothing to paint, and a
            // default-constructed cell carries NUL rather than a space.
            if (cell.Char <= ' ')
                continue;
            context.DrawText(GlyphFor(cell), new Point(c * _cellWidth, y));
        }
    }

    private FormattedText GlyphFor(TerminalCell cell)
    {
        var key = (cell.Char, cell.Foreground, cell.Bold);
        if (_glyphs.TryGetValue(key, out var cached))
            return cached;

        // Cheap insurance against a pathological stream of distinct glyphs; the working set of a
        // shell session is a few hundred entries.
        if (_glyphs.Count > 4096)
            _glyphs.Clear();

        var brush = cell.Foreground == TerminalPalette.DefaultForeground
            ? DefaultFgBrush
            : new SolidColorBrush(Palette[cell.Foreground & 0x0F]);
        var text = new FormattedText(cell.Char.ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, cell.Bold ? _boldTypeface : _typeface, TerminalFontSize, brush);
        _glyphs[key] = text;
        return text;
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

        // Paste before the Ctrl+letter branch below, which would otherwise swallow Ctrl+V as
        // 0x16 (readline's quoted-insert — it eats the next keystroke and prints nothing).
        // Ctrl+Shift+V is the terminal-native spelling, Shift+Insert the old one; plain Ctrl+V
        // is what everyone actually presses and nothing in a shell needs 0x16.
        var pasteCombo =
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V) ||
            (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Insert);
        if (pasteCombo)
        {
            PasteRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Copy, likewise before the Ctrl+letter branch — but only in the spellings that are not
        // already taken. Plain Ctrl+C must stay SIGINT: it is how a running command is stopped,
        // and no amount of clipboard convenience is worth losing that.
        var copyCombo =
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && e.Key == Key.C) ||
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Insert);
        if (copyCombo)
        {
            CopyRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

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
