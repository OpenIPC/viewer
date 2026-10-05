using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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
    public static readonly StyledProperty<TerminalTheme> ColorThemeProperty =
        AvaloniaProperty.Register<TerminalView, TerminalTheme>(nameof(ColorTheme), TerminalTheme.Default,
            // A binding that briefly resolves to nothing (a combo box between selections) must not
            // leave the renderer without a palette.
            coerce: (_, theme) => theme ?? TerminalTheme.Default);

    /// <summary>The palette the grid is drawn with (terminals don't follow the app theme).</summary>
    public TerminalTheme ColorTheme
    {
        get => GetValue(ColorThemeProperty);
        set => SetValue(ColorThemeProperty, value);
    }

    public static readonly StyledProperty<TerminalCursorStyle> CursorStyleProperty =
        AvaloniaProperty.Register<TerminalView, TerminalCursorStyle>(nameof(CursorStyle));

    public TerminalCursorStyle CursorStyle
    {
        get => GetValue(CursorStyleProperty);
        set => SetValue(CursorStyleProperty, value);
    }

    public static readonly StyledProperty<bool> CursorBlinkProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(CursorBlink));

    public bool CursorBlink
    {
        get => GetValue(CursorBlinkProperty);
        set => SetValue(CursorBlinkProperty, value);
    }

    public static readonly StyledProperty<bool> FitToEmulatorProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(FitToEmulator));

    /// <summary>
    /// Size the control to the emulator's grid instead of filling the space it is given — for a
    /// fixed-size sample such as the settings preview. A live terminal leaves this off and
    /// resizes the emulator to fit the control instead.
    /// </summary>
    public bool FitToEmulator
    {
        get => GetValue(FitToEmulatorProperty);
        set => SetValue(FitToEmulatorProperty, value);
    }

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

    // JetBrains Mono ships inside the app (Assets/Fonts, SIL OFL 1.1). Asking the platform for a
    // monospace family by name failed on Android: none of Cascadia / Consolas / Menlo exists there
    // and what the names fell back to was proportional, so even with one glyph pinned per cell a
    // narrow `i` left a hole and a wide `m` ran into its neighbour. Characters the bundled font
    // lacks (CJK, emoji) still come from the platform's own fallback.
    private static readonly FontFamily Mono =
        new("avares://OpenIPC.Viewer.App/Assets/Fonts#JetBrains Mono");
    private readonly Typeface _typeface = new(Mono);
    private readonly Typeface _boldTypeface = new(Mono, weight: FontWeight.Bold);
    private readonly Typeface _italicTypeface = new(Mono, FontStyle.Italic);
    private readonly Typeface _boldItalicTypeface = new(Mono, FontStyle.Italic, FontWeight.Bold);

    // Only the attributes that change the glyph itself; colors are part of the key separately and
    // lines (underline, strikethrough) are drawn over the glyph.
    private const TerminalAttributes GlyphStyle = TerminalAttributes.Bold | TerminalAttributes.Italic;

    private double _cellWidth;
    private double _cellHeight;
    private readonly Dictionary<(char Char, uint Rgb, TerminalAttributes Style), FormattedText> _glyphs = new();
    // One brush per color: 256-color and truecolor output can use many, and allocating a brush per
    // cell per repaint is what the cache is for.
    private readonly Dictionary<uint, IBrush> _brushes = new();

    // Blink phase. The cursor is drawn while this is true; typing or new output sets it back so
    // the cursor never vanishes at the moment you are looking for it.
    private DispatcherTimer? _blinkTimer;
    private bool _blinkOn = true;
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(530);

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

    // Auto-scroll while a selection drag sits against the top or bottom edge. Timer-driven, at a
    // fixed pace: stepping once per pointer event made the speed depend on how much the finger
    // trembled, and a resting fingertip produces a stream of them.
    private DispatcherTimer? _edgeScroll;
    private int _edgeDirection;
    private Point _selectPoint;
    private static readonly TimeSpan EdgeScrollInterval = TimeSpan.FromMilliseconds(110);

    private const double HandleRadius = 9;
    // Generous, because a fingertip is: the knob is 18px across and the nearest one inside this
    // radius wins, so the handles stay catchable without hijacking taps elsewhere.
    private const double HandleTouchSlop = 30;

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
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        if (change.Property == ColorThemeProperty)
        {
            // Cached glyphs and brushes are keyed by resolved color; the old theme's are dead weight.
            _glyphs.Clear();
            _brushes.Clear();
            InvalidateVisual();
            return;
        }

        if (change.Property == CursorStyleProperty)
        {
            InvalidateVisual();
            return;
        }

        if (change.Property == CursorBlinkProperty)
        {
            UpdateBlinkTimer();
            return;
        }

        if (change.Property == FitToEmulatorProperty)
        {
            InvalidateMeasure();
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
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!FitToEmulator || Emulator is not { } emu)
            return base.MeasureOverride(availableSize);
        return new Size(emu.Columns * _cellWidth, emu.Rows * _cellHeight);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateBlinkTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blinkTimer?.Stop();
    }

    private void UpdateBlinkTimer()
    {
        _blinkOn = true;
        if (!CursorBlink || VisualRoot is null)
        {
            _blinkTimer?.Stop();
            InvalidateVisual();
            return;
        }

        if (_blinkTimer is null)
        {
            _blinkTimer = new DispatcherTimer { Interval = BlinkInterval };
            _blinkTimer.Tick += (_, _) =>
            {
                _blinkOn = !_blinkOn;
                InvalidateVisual();
            };
        }
        _blinkTimer.Stop();
        _blinkTimer.Start();
    }

    // Show the cursor now and start the blink cycle over.
    private void WakeCursor()
    {
        if (!CursorBlink || _blinkTimer is null)
            return;
        _blinkOn = true;
        _blinkTimer.Stop();
        _blinkTimer.Start();
    }

    // Updated fires on the UI thread (the VM marshals shell data), so a direct
    // invalidate is safe.
    private void OnEmulatorUpdated()
    {
        // Switching screens changes what the row indices mean — the alternate screen has no
        // history — so a selection or a scrolled-back view from before is meaningless after.
        var alternate = _subscribed?.IsAlternateScreen ?? false;
        if (alternate != _wasAlternate)
        {
            _wasAlternate = alternate;
            _scrollOffset = 0;
            ClearSelection();
        }

        WakeCursor();
        InvalidateVisual();
    }

    private bool _wasAlternate;

    // Wheel and touch scrolling. On the alternate screen there is no history to show, so — like
    // xterm's alternateScroll — the gesture becomes cursor keys and the tool (less, vi, htop)
    // scrolls its own content. Positive rows are "back in time", i.e. up.
    private void ScrollOrSendKeys(int rows)
    {
        if (rows == 0)
            return;
        if (Emulator is { IsAlternateScreen: true } emu)
        {
            var key = emu.CursorKey(rows > 0 ? 'A' : 'B');
            Input?.Invoke(this, string.Concat(Enumerable.Repeat(key, Math.Abs(rows))));
            return;
        }
        ScrollBy(rows);
    }

    private void EnsureMetrics()
    {
        var sample = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, TerminalFontSize, Brushes.White);
        _cellWidth = sample.Width;
        _cellHeight = sample.Height;
    }

    private IBrush BrushFor(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var brush))
            return brush;
        if (_brushes.Count > 4096)
            _brushes.Clear();
        brush = new SolidColorBrush(Color.FromUInt32(argb));
        _brushes[argb] = brush;
        return brush;
    }

    private IBrush OpaqueBrush(uint rgb) => BrushFor(0xFF00_0000 | rgb);

    // A coordinate rounded to the nearest physical pixel at the render scale of this frame.
    private double _renderScale = 1;
    private double Snap(double v) => Math.Round(v * _renderScale) / _renderScale;

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
        _renderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        context.FillRectangle(OpaqueBrush(ColorTheme.Background), new Rect(Bounds.Size));

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
        context.DrawEllipse(HandleBrush, null, KnobCenter(anchor), HandleRadius, HandleRadius);
    }

    // Where the knob of a handle is drawn, and therefore where a finger has to land to pick it up.
    // One place for both: on the bottom row the knob flips above the row, and hit-testing used to
    // keep looking for it below, so the knob you could see there could not be grabbed and the
    // press scrolled the terminal instead.
    private Point KnobCenter(Point anchor)
    {
        var rowTop = anchor.Y - _cellHeight / 2;
        var below = rowTop + _cellHeight + HandleRadius;
        var y = below + HandleRadius <= Bounds.Height ? below : rowTop - HandleRadius;
        return new Point(anchor.X, y);
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

    // The handle a finger landed on, if it landed on one: the nearest knob within reach.
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
            var knob = KnobCenter(anchor);
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
        var text = new LineJoiner(emu);
        for (var row = start.Row; row <= end.Row; row++)
        {
            var cells = RowAt(emu, row);
            var from = row == start.Row ? Math.Clamp(start.Col, 0, cells.Length) : 0;
            var to = row == end.Row ? Math.Clamp(end.Col, 0, cells.Length) : cells.Length;
            text.Add(cells, from, to, last: row == end.Row);
        }
        return text.ToString();
    }

    /// <summary>Everything on screen right now — what a touch user gets when copying without a highlight.</summary>
    public string GetVisibleText()
    {
        var emu = Emulator;
        if (emu is null)
            return "";

        var top = emu.ScrollbackRows - _scrollOffset;
        var text = new LineJoiner(emu);
        for (var row = 0; row < emu.Rows; row++)
        {
            var cells = RowAt(emu, top + row);
            text.Add(cells, 0, cells.Length, last: row == emu.Rows - 1);
        }
        // Trailing blank rows are the unused part of the screen, not content.
        return text.ToString().TrimEnd();
    }

    // Rows back into lines of text. A row the emulator wrapped runs straight on into the next one
    // with no break, and keeps its trailing blanks, which are real spaces when the wrap fell
    // between two words. Every other row ends its line.
    private readonly struct LineJoiner
    {
        private readonly TerminalEmulator _emu;
        private readonly StringBuilder _text;

        public LineJoiner(TerminalEmulator emu)
        {
            _emu = emu;
            _text = new StringBuilder();
        }

        public void Add(TerminalCell[] cells, int from, int to, bool last)
        {
            var continues = !last && to == cells.Length && cells.Length > 0 && _emu.IsWrapped(cells);
            _text.Append(TextOf(cells, from, to, trim: !continues));
            if (!last && !continues)
                _text.Append(Environment.NewLine);
        }

        public override string ToString() => _text.ToString();
    }

    private static string TextOf(TerminalCell[] cells, int from, int to, bool trim = true)
    {
        var buffer = new char[Math.Max(0, to - from)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var ch = cells[from + i].Char;
            // A never-written cell holds NUL; on the way out it is a space like any other blank.
            buffer[i] = ch < ' ' ? ' ' : ch;
        }
        var text = new string(buffer);
        return trim ? text.TrimEnd() : text;
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
        ScrollOrSendKeys((int)Math.Round(e.Delta.Y) * 3);
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

            _selectPoint = _grabbed == Handle.None ? at : at + _grabOffset;
            UpdateEdgeScroll(at);
            _selFocus = CellAt(_selectPoint);
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
        ScrollOrSendKeys(rows);
    }

    // Dragging a selection against either edge walks the view, so it can run past the top of the
    // screen into the scrollback and past the bottom into the newest output. Top is back in time
    // (a bigger offset), bottom is towards the live screen. This used to be the other way round,
    // so a selection dragged down to the last rows sent the view racing off into the history.
    private void UpdateEdgeScroll(Point at)
    {
        var zone = _cellHeight / 2;
        _edgeDirection = _cellHeight <= 0 ? 0
            : at.Y < zone ? 1
            : at.Y > Bounds.Height - zone ? -1
            : 0;

        if (_edgeDirection == 0)
        {
            _edgeScroll?.Stop();
            return;
        }

        if (_edgeScroll is null)
        {
            _edgeScroll = new DispatcherTimer { Interval = EdgeScrollInterval };
            _edgeScroll.Tick += (_, _) => EdgeScrollStep();
        }
        // The first step waits a beat too: brushing the edge on the way to the last row must not
        // move anything.
        if (!_edgeScroll.IsEnabled)
            _edgeScroll.Start();
    }

    private void EdgeScrollStep()
    {
        if (!_selecting || _edgeDirection == 0)
        {
            StopEdgeScroll();
            return;
        }
        ScrollBy(_edgeDirection);
        // The finger has not moved, but the text under it has.
        _selFocus = CellAt(_selectPoint);
        InvalidateVisual();
    }

    private void StopEdgeScroll()
    {
        _edgeDirection = 0;
        _edgeScroll?.Stop();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        StopEdgeScroll();

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
        StopEdgeScroll();
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
        var theme = ColorTheme;

        // Background fills first — only where a cell's background differs from the screen's,
        // which is also what makes an inverse cell on the default colors show up. A run of cells
        // sharing a color is one rectangle, and every edge is snapped to a device pixel: cell
        // widths are fractional, and abutting rectangles with antialiased edges left a visible
        // seam between every pair of colored cells.
        var top = Snap(y);
        var bottom = Snap(y + _cellHeight);
        for (var c = 0; c < cells.Length;)
        {
            var (_, bg) = theme.ColorsOf(cells[c]);
            var end = c + 1;
            while (end < cells.Length && theme.ColorsOf(cells[end]).Background == bg)
                end++;
            if (bg != theme.Background)
            {
                var left = Snap(c * _cellWidth);
                context.FillRectangle(OpaqueBrush(bg),
                    new Rect(left, top, Snap(end * _cellWidth) - left, bottom - top));
            }
            c = end;
        }

        // Selection sits over the cell backgrounds and under the glyphs, so highlighted text
        // stays readable and coloured output keeps its own background showing through.
        if (selEnd > selStart)
            context.FillRectangle(BrushFor(theme.Selection),
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
            var (fg, _) = theme.ColorsOf(cell);
            var x = c * _cellWidth;

            // Everything at or below U+0020 is blank or a control code: nothing to paint, and a
            // default-constructed cell carries NUL rather than a space. Hidden text (SGR 8) has
            // already been given the background color, so it paints as nothing too.
            if (cell.Char > ' ' && (cell.Attributes & TerminalAttributes.Hidden) == 0)
                DrawCellChar(context, cell.Char, fg, cell.Attributes, x, y);

            DrawLines(context, cell.Attributes, fg, x, y);
        }
    }

    // Underline and strikethrough are drawn by hand rather than as text decorations: they have to
    // run under blanks too (an underlined field in a form is mostly spaces) and join up from one
    // cell to the next.
    private void DrawLines(DrawingContext context, TerminalAttributes attrs, uint fg, double x, double y)
    {
        if ((attrs & (TerminalAttributes.Underline | TerminalAttributes.Strikethrough)) == 0 ||
            (attrs & TerminalAttributes.Hidden) != 0)
            return;

        var thickness = Math.Max(1, Math.Round(TerminalFontSize / 14));
        var brush = OpaqueBrush(fg);
        if ((attrs & TerminalAttributes.Underline) != 0)
            context.FillRectangle(brush, new Rect(x, y + _cellHeight - thickness * 2, _cellWidth, thickness));
        if ((attrs & TerminalAttributes.Strikethrough) != 0)
            context.FillRectangle(brush, new Rect(x, y + _cellHeight / 2, _cellWidth, thickness));
    }

    private void DrawCellChar(DrawingContext context, char ch, uint rgb, TerminalAttributes attrs, double x, double y)
    {
        if (!DrawBoxChar(context, ch, rgb, x, y))
            context.DrawText(GlyphFor(ch, rgb, attrs), new Point(x, y));
    }

    // --- Box drawing -----------------------------------------------------
    //
    // Line and block characters are drawn as rectangles instead of being taken from the font. A
    // font's │ is only as tall as its glyph box, which is shorter than the cell once line spacing
    // is added, so every frame a full-screen tool drew came out as dashes with gaps between rows —
    // and where the font has no such glyph at all, as whatever the fallback font made of it.
    // Drawn, the strokes meet the cell edges exactly and join up with their neighbours.

    // Light/heavy lines as four arms from the cell centre: 0 none, 1 light, 2 heavy.
    // Packed as up | down << 2 | left << 4 | right << 6.
    private static readonly Dictionary<char, int> BoxArms = BuildBoxArms();

    private static Dictionary<char, int> BuildBoxArms()
    {
        static int A(int up, int down, int left, int right) => up | (down << 2) | (left << 4) | (right << 6);
        var map = new Dictionary<char, int>();
        void Both(char light, char heavy, int up, int down, int left, int right)
        {
            map[light] = A(up, down, left, right);
            map[heavy] = A(up * 2, down * 2, left * 2, right * 2);
        }
        Both('─', '━', 0, 0, 1, 1);
        Both('│', '┃', 1, 1, 0, 0);
        Both('┌', '┏', 0, 1, 0, 1);
        Both('┐', '┓', 0, 1, 1, 0);
        Both('└', '┗', 1, 0, 0, 1);
        Both('┘', '┛', 1, 0, 1, 0);
        Both('├', '┣', 1, 1, 0, 1);
        Both('┤', '┫', 1, 1, 1, 0);
        Both('┬', '┳', 0, 1, 1, 1);
        Both('┴', '┻', 1, 0, 1, 1);
        Both('┼', '╋', 1, 1, 1, 1);
        Both('╴', '╸', 0, 0, 1, 0);
        Both('╵', '╹', 1, 0, 0, 0);
        Both('╶', '╺', 0, 0, 0, 1);
        Both('╷', '╻', 0, 1, 0, 0);
        // Rounded corners, drawn square: at terminal sizes the difference is a pixel or two.
        map['╭'] = map['┌'];
        map['╮'] = map['┐'];
        map['╰'] = map['└'];
        map['╯'] = map['┘'];
        return map;
    }

    // Double lines as explicit segments, four characters each: H or V, the line's level
    // (a/b = the two parallel strokes either side of the centre), then where it starts and ends
    // (L/R or T/B = the cell edges, a/b = the near and far stroke of the crossing direction).
    private static readonly Dictionary<char, string[]> BoxDouble = new()
    {
        ['═'] = new[] { "HaLR", "HbLR" },
        ['║'] = new[] { "VaTB", "VbTB" },
        ['╔'] = new[] { "HaaR", "HbbR", "VaaB", "VbbB" },
        ['╗'] = new[] { "HaLb", "HbLa", "VabB", "VbaB" },
        ['╚'] = new[] { "HabR", "HbaR", "VaTb", "VbTa" },
        ['╝'] = new[] { "HaLa", "HbLb", "VaTa", "VbTb" },
        ['╠'] = new[] { "VaTB", "VbTa", "VbbB", "HabR", "HbbR" },
        ['╣'] = new[] { "VbTB", "VaTa", "VabB", "HaLa", "HbLa" },
        ['╦'] = new[] { "HaLR", "HbLa", "HbbR", "VabB", "VbbB" },
        ['╩'] = new[] { "HbLR", "HaLa", "HabR", "VaTa", "VbTa" },
        ['╬'] = new[] { "HaLa", "HabR", "HbLa", "HbbR", "VaTa", "VabB", "VbTa", "VbbB" },
    };

    private bool DrawBoxChar(DrawingContext context, char ch, uint rgb, double x, double y)
    {
        if (ch < '─' || ch > '▟')
            return false;

        var left = Snap(x);
        var right = Snap(x + _cellWidth);
        var top = Snap(y);
        var bottom = Snap(y + _cellHeight);
        var brush = OpaqueBrush(rgb);
        var light = Math.Max(1, Math.Round(TerminalFontSize / 14));
        var cx = (left + right) / 2;
        var cy = (top + bottom) / 2;

        if (BoxArms.TryGetValue(ch, out var arms))
        {
            // Each arm runs from its edge to just past the centre, so the joint is filled
            // whatever mix of weights meets there.
            void Arm(int weight, bool horizontal, double from, double to)
            {
                if (weight == 0)
                    return;
                var t = weight == 2 ? light * 2 : light;
                if (horizontal)
                    FillSnapped(context, brush, from, cy - t / 2, to, cy + t / 2);
                else
                    FillSnapped(context, brush, cx - t / 2, from, cx + t / 2, to);
            }

            Arm(arms & 3, false, top, cy + light);
            Arm((arms >> 2) & 3, false, cy - light, bottom);
            Arm((arms >> 4) & 3, true, left, cx + light);
            Arm((arms >> 6) & 3, true, cx - light, right);
            return true;
        }

        if (BoxDouble.TryGetValue(ch, out var segments))
        {
            var gap = light * 1.5;
            double Level(char s, double centre) => s == 'a' ? centre - gap : centre + gap;
            // Ends at a stroke reach half a stroke further, so two strokes meeting at a corner
            // close it instead of leaving a notch.
            double End(char s, double edgeLow, double edgeHigh, double centre) => s switch
            {
                'L' or 'T' => edgeLow,
                'R' or 'B' => edgeHigh,
                'a' => centre - gap - light / 2,
                _ => centre + gap + light / 2,
            };

            foreach (var s in segments)
            {
                if (s[0] == 'H')
                {
                    var level = Level(s[1], cy);
                    FillSnapped(context, brush, End(s[2], left, right, cx), level - light / 2,
                        End(s[3], left, right, cx), level + light / 2);
                }
                else
                {
                    var level = Level(s[1], cx);
                    FillSnapped(context, brush, level - light / 2, End(s[2], top, bottom, cy),
                        level + light / 2, End(s[3], top, bottom, cy));
                }
            }
            return true;
        }

        // Block elements: full, halves, and the three shades as translucent fills.
        switch (ch)
        {
            case '█': FillSnapped(context, brush, left, top, right, bottom); return true;
            case '▀': FillSnapped(context, brush, left, top, right, cy); return true;
            case '▄': FillSnapped(context, brush, left, cy, right, bottom); return true;
            case '▌': FillSnapped(context, brush, left, top, cx, bottom); return true;
            case '▐': FillSnapped(context, brush, cx, top, right, bottom); return true;
            case '░': FillSnapped(context, BrushFor(0x4000_0000 | rgb), left, top, right, bottom); return true;
            case '▒': FillSnapped(context, BrushFor(0x8000_0000 | rgb), left, top, right, bottom); return true;
            case '▓': FillSnapped(context, BrushFor(0xC000_0000 | rgb), left, top, right, bottom); return true;
            default: return false;
        }
    }

    // Both ends go to the pixel grid, and a stroke never collapses below one physical pixel: a
    // one-pixel line centred on a whole pixel has both ends at .5, which round to the same value
    // and left thin verticals simply missing at small font sizes.
    private void FillSnapped(DrawingContext context, IBrush brush, double x0, double y0, double x1, double y1)
    {
        var pixel = 1 / _renderScale;
        x0 = Snap(x0);
        y0 = Snap(y0);
        x1 = Math.Max(Snap(x1), x0 + pixel);
        y1 = Math.Max(Snap(y1), y0 + pixel);
        context.FillRectangle(brush, new Rect(x0, y0, x1 - x0, y1 - y0));
    }

    private FormattedText GlyphFor(char ch, uint rgb, TerminalAttributes attrs)
    {
        var style = attrs & GlyphStyle;
        var key = (ch, rgb, style);
        if (_glyphs.TryGetValue(key, out var cached))
            return cached;

        // Cheap insurance against a pathological stream of distinct glyphs; the working set of a
        // shell session is a few hundred entries, more with 256-color or truecolor output.
        if (_glyphs.Count > 4096)
            _glyphs.Clear();

        var typeface = style switch
        {
            TerminalAttributes.Bold => _boldTypeface,
            TerminalAttributes.Italic => _italicTypeface,
            GlyphStyle => _boldItalicTypeface,
            _ => _typeface,
        };
        var text = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, TerminalFontSize, OpaqueBrush(rgb));
        _glyphs[key] = text;
        return text;
    }

    private void DrawCursor(DrawingContext context, TerminalEmulator emu)
    {
        if (!emu.CursorVisible || (CursorBlink && !_blinkOn))
            return;

        var theme = ColorTheme;
        var x = emu.CursorColumn * _cellWidth;
        var y = emu.CursorRow * _cellHeight;
        var cursor = OpaqueBrush(theme.Cursor);
        var thickness = Math.Max(2, Math.Round(TerminalFontSize / 7));

        switch (CursorStyle)
        {
            case TerminalCursorStyle.Block:
            {
                // Solid box, and the character under it redrawn in the screen color so it stays
                // readable — the way a hardware terminal inverts the cell.
                context.FillRectangle(cursor, new Rect(x, y, _cellWidth, _cellHeight));
                // The column runs one past the last cell while a wrap is pending.
                var row = emu.GetRow(emu.CursorRow);
                var cell = emu.CursorColumn < row.Length ? row[emu.CursorColumn] : TerminalCell.Blank;
                if (cell.Char > ' ')
                {
                    var (_, bg) = theme.ColorsOf(cell);
                    DrawCellChar(context, cell.Char, bg, cell.Attributes, x, y);
                }
                break;
            }
            case TerminalCursorStyle.Bar:
                context.FillRectangle(cursor, new Rect(x, y, thickness, _cellHeight));
                break;
            case TerminalCursorStyle.Underline:
                context.FillRectangle(cursor, new Rect(x, y + _cellHeight - thickness, _cellWidth, thickness));
                break;
            default:
                // Hollow box so the character under the cursor stays readable.
                context.DrawRectangle(null, new Pen(cursor, 1), new Rect(x, y, _cellWidth, _cellHeight));
                break;
        }
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

        var seq = SequenceFor(e.Key, e.KeyModifiers);
        if (seq is not null)
        {
            Input?.Invoke(this, seq);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The bytes a non-text key sends, or null for keys that arrive as text input. Cursor keys
    /// follow the mode the remote asked for (ESC O A once an ncurses tool has switched to
    /// application keys); the rest are the fixed xterm spellings mc, htop and vi look for — F10
    /// is how mc is quit.
    /// </summary>
    public string? SequenceFor(Key key, KeyModifiers modifiers)
    {
        var emu = Emulator;
        string Cursor(char final) => emu?.CursorKey(final) ?? "\x1b[" + final;
        return key switch
        {
            Key.Enter => "\r",
            Key.Back => "\x7f",
            Key.Tab => modifiers.HasFlag(KeyModifiers.Shift) ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            Key.Up => Cursor('A'),
            Key.Down => Cursor('B'),
            Key.Right => Cursor('C'),
            Key.Left => Cursor('D'),
            Key.Home => Cursor('H'),
            Key.End => Cursor('F'),
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null,
        };
    }
}
