using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenIPC.Viewer.Core.Ssh.Terminal;

/// <summary>
/// A small VT/xterm terminal emulator (phase-13 §13.3). Feeds UTF-8 bytes through a state
/// machine that maintains a character grid: printable text, the common control codes, CSI
/// cursor/erase/insert/delete, scroll regions, the alternate screen full-screen tools draw on,
/// the DEC line-drawing charset, and SGR renditions (16/256/truecolor, bold, dim, italic,
/// underline, inverse, hidden, strikethrough). Mouse reporting is not modelled. Unknown escapes
/// are consumed and ignored rather than corrupting the screen.
/// </summary>
public sealed class TerminalEmulator
{
    private const char Esc = '\x1b';
    private const char Bel = '\x07';

    private enum State { Ground, Escape, EscapeIntermediate, Csi, String }

    // The grid being drawn and written: the main screen, or the alternate one while a full-screen
    // tool is running. The main screen waits in _mainScreen meanwhile, and only it has history —
    // a redraw of vi's screen scrolling past is not something anyone wants to scroll back through.
    private TerminalCell[][] _screen = Array.Empty<TerminalCell[]>();
    private TerminalCell[][]? _mainScreen;
    private readonly List<TerminalCell[]> _scrollback = new();
    private int _scrollbackLimit;

    public const int DefaultScrollbackLimit = 1000;
    public const int MinScrollbackLimit = 100;
    public const int MaxScrollbackLimit = 50_000;

    // Rows that ran out of width and carried on in the row below (autowrap), as opposed to rows a
    // line break ended. Copying needs the difference: a long command wraps across two or three rows
    // of a phone-width terminal, and copying it back with a break between each row turns one
    // command into several — each of which a paste then runs on its own. The mark rides on the row
    // object itself, so it follows the row into the scrollback and is gone when the row is.
    private readonly ConditionalWeakTable<TerminalCell[], object> _wrapped = new();
    private static readonly object WrapMark = new();

    private int _cursorRow;
    private int _cursorCol;

    private TerminalColor _fg;
    private TerminalColor _bg;
    private TerminalAttributes _attrs;

    // Scroll region (DECSTBM), inclusive. Line feeds at its bottom scroll only the rows inside it,
    // which is how a full-screen tool keeps a header and a status line still while a list moves.
    private int _scrollTop;
    private int _scrollBottom;
    private bool _originMode;
    private bool _autoWrap = true;

    // Character sets: G0 and G1 are each either ASCII or DEC special graphics (the line-drawing
    // set behind every box in mc and htop); SO/SI pick which one is in use.
    private bool _g0Graphics;
    private bool _g1Graphics;
    private bool _shiftOut;

    private char _lastPrinted;

    private struct SavedCursor
    {
        public int Row, Col;
        public TerminalColor Fg, Bg;
        public TerminalAttributes Attrs;
        public bool OriginMode, G0Graphics, G1Graphics, ShiftOut;
    }

    private SavedCursor _saved;
    // The cursor the main screen had when ?1049 switched away from it.
    private SavedCursor _mainCursor;

    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private bool _privateSeq;
    private char _escIntermediate;
    // How much of an unterminated escape we are willing to swallow before deciding the stream
    // lied to us and going back to printing. Without a ceiling one malformed sequence eats the
    // rest of the session: every byte after it disappears into the parser and the screen stops
    // changing at all.
    private const int MaxSequenceLength = 4096;
    // Room for a truecolor foreground and background plus a few flags in one SGR.
    private const int MaxParamsLength = 128;
    private int _stringLength;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _charBuf = new char[1024];

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    // While a wrap is pending the column is one past the last cell; nothing outside needs that.
    public int CursorRow => _cursorRow;
    public int CursorColumn => Math.Min(_cursorCol, Math.Max(0, Columns - 1));

    /// <summary>
    /// The remote turned on bracketed paste (CSI ?2004h): pasted text is to be sent between
    /// ESC[200~ and ESC[201~, and the shell will insert it rather than run its lines.
    /// </summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>False while the remote has hidden the cursor (CSI ?25l) — full-screen tools do.</summary>
    public bool CursorVisible { get; private set; } = true;

    /// <summary>A full-screen tool switched to the alternate screen (CSI ?1049h and friends).</summary>
    public bool IsAlternateScreen => _mainScreen is not null;

    /// <summary>
    /// The remote asked for "application" cursor keys (DECCKM, CSI ?1h): arrows are to be sent as
    /// ESC O A rather than ESC [ A. ncurses tools switch this on and only recognise that spelling.
    /// </summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>
    /// How many rows that scrolled off the top are remembered. Lowering it drops the oldest rows
    /// straight away.
    /// </summary>
    public int ScrollbackLimit
    {
        get => _scrollbackLimit;
        set
        {
            _scrollbackLimit = Math.Clamp(value, MinScrollbackLimit, MaxScrollbackLimit);
            TrimScrollback();
        }
    }

    /// <summary>
    /// True when <paramref name="row"/> (from <see cref="GetRow"/> or
    /// <see cref="GetScrollbackRow"/>) continues in the next row — the text ran past the right
    /// edge rather than ending in a line break.
    /// </summary>
    public bool IsWrapped(TerminalCell[] row) => _wrapped.TryGetValue(row, out _);

    /// <summary>Raised after a <see cref="Feed(byte[])"/> batch mutates the grid.</summary>
    public event Action? Updated;

    /// <summary>
    /// Bytes the terminal itself has to send back to the remote — answers to status queries
    /// (cursor position, device attributes). Raised while feeding.
    /// </summary>
    public event Action<string>? Reply;

    public TerminalEmulator(int columns, int rows, int scrollbackLimit = DefaultScrollbackLimit)
    {
        ScrollbackLimit = scrollbackLimit;
        Resize(columns, rows);
    }

    public TerminalCell[] GetRow(int row) => _screen[row];

    /// <summary>
    /// Rows that have scrolled off the top and are still remembered. None while the alternate
    /// screen is up: what is on it is all there is.
    /// </summary>
    public int ScrollbackRows => IsAlternateScreen ? 0 : _scrollback.Count;

    /// <summary>One remembered row; index 0 is the oldest.</summary>
    public TerminalCell[] GetScrollbackRow(int index) => _scrollback[index];

    /// <summary>What a cursor key (A/B/C/D, H/F) sends in the current cursor-key mode.</summary>
    public string CursorKey(char final) => (ApplicationCursorKeys ? "\x1bO" : "\x1b[") + final;

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
            return;

        if (_mainScreen is null)
        {
            _screen = ResizeMain(_screen, columns, rows, ref _cursorRow);
        }
        else
        {
            // The tool on the alternate screen redraws itself when told the new size; all that
            // matters here is that nothing is out of range until it does.
            _mainScreen = ResizeMain(_mainScreen, columns, rows, ref _mainCursor.Row);
            _screen = ResizeKeepingTop(_screen, columns, rows);
            _cursorRow = Math.Clamp(_cursorRow, 0, rows - 1);
        }

        Columns = columns;
        Rows = rows;
        _cursorCol = Math.Min(_cursorCol, columns - 1);
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        Updated?.Invoke();
    }

    private TerminalCell[][] ResizeMain(TerminalCell[][] screen, int columns, int rows, ref int cursorRow)
    {
        var had = screen.Length > 0;
        var oldRows = screen.Length;

        // Losing rows takes them off the TOP, the way a real terminal does it: the interesting end
        // of a shell session is the bottom one. Keeping the top instead would drop the prompt every
        // time the view got shorter — which on a phone is every time the soft keyboard opens.
        var drop = had ? Math.Max(0, oldRows - rows) : 0;
        for (var r = 0; r < drop; r++)
            _scrollback.Add(screen[r]);
        TrimScrollback();

        // And they come back when the view grows again. Without this the trip is one-way: showing
        // and hiding the keyboard once ate a few rows of output for good, and doing it a few times
        // scrolled a long listing off the top with no way to get it back.
        var restore = had ? Math.Min(Math.Max(0, rows - oldRows), _scrollback.Count) : 0;

        var next = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            next[r] = NewBlankRow(columns);

            TerminalCell[]? source;
            if (r < restore)
            {
                source = _scrollback[_scrollback.Count - restore + r];
            }
            else
            {
                var from = r - restore + drop;
                source = had && from < oldRows ? screen[from] : null;
            }

            if (source is not null)
                CopyRow(source, next[r], columns);
        }

        if (restore > 0)
            _scrollback.RemoveRange(_scrollback.Count - restore, restore);

        cursorRow = Math.Clamp(cursorRow - drop + restore, 0, rows - 1);
        return next;
    }

    private TerminalCell[][] ResizeKeepingTop(TerminalCell[][] screen, int columns, int rows)
    {
        var next = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            next[r] = NewBlankRow(columns);
            if (r < screen.Length)
                CopyRow(screen[r], next[r], columns);
        }
        return next;
    }

    private void CopyRow(TerminalCell[] source, TerminalCell[] target, int columns)
    {
        Array.Copy(source, target, Math.Min(columns, source.Length));
        if (IsWrapped(source))
            MarkWrapped(target, true);
    }

    public void Feed(byte[] bytes) => Feed(bytes.AsSpan());

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        var needed = _decoder.GetCharCount(bytes, flush: false);
        if (_charBuf.Length < needed)
            _charBuf = new char[needed];

        var written = _decoder.GetChars(bytes, _charBuf, flush: false);
        for (var i = 0; i < written; i++)
            ProcessChar(_charBuf[i]);

        Updated?.Invoke();
    }

    /// <summary>Convenience for tests and synthetic input.</summary>
    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    private void ProcessChar(char c)
    {
        switch (_state)
        {
            case State.Ground: ProcessGround(c); break;
            case State.Escape: ProcessEscape(c); break;
            case State.EscapeIntermediate: ProcessEscapeIntermediate(c); break;
            case State.Csi: ProcessCsi(c); break;
            case State.String: ProcessString(c); break;
        }
    }

    private void ProcessGround(char c)
    {
        switch (c)
        {
            case Esc: _state = State.Escape; break;
            case '\r': _cursorCol = 0; break;
            // VT and FF are line feeds on every terminal that matters.
            case '\n' or '\v' or '\f': LineFeed(); break;
            case '\b': if (_cursorCol > 0) _cursorCol = Math.Min(_cursorCol, Columns - 1) - 1; break;
            case '\t': _cursorCol = Math.Min(Columns - 1, (_cursorCol / 8 + 1) * 8); break;
            case '\x0e': _shiftOut = true; break;  // SO — G1
            case '\x0f': _shiftOut = false; break; // SI — G0
            case Bel: break;
            default:
                if (!char.IsControl(c))
                    PutChar(c);
                break;
        }
    }

    private void ProcessEscape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _params.Clear();
                _privateSeq = false;
                _state = State.Csi;
                break;
            // OSC (window title, shell integration) and the other string escapes: DCS, SOS, PM,
            // APC. None of them render; all of them run until a terminator.
            case ']' or 'P' or 'X' or '^' or '_':
                _stringLength = 0;
                _state = State.String;
                break;
            // Escapes with an intermediate byte — charset designation ESC ( 0, ESC # 8 and the
            // like — are three characters long. Treating them as two printed the third: every
            // ncurses "back to normal" (ESC ( B) left a stray B on the screen.
            case >= ' ' and <= '/':
                _escIntermediate = c;
                _state = State.EscapeIntermediate;
                break;
            case '7': SaveCursor(ref _saved); break;
            case '8': RestoreCursor(_saved); break;
            case 'D': LineFeed(); break;                      // IND
            case 'E': _cursorCol = 0; LineFeed(); break;      // NEL
            case 'M': ReverseIndex(); break;                  // RI
            case 'c': FullReset(); break;                     // RIS
            default: break; // ESC = / ESC > (keypad modes) and the rest: nothing to draw.
        }
    }

    private void ProcessEscapeIntermediate(char c)
    {
        _state = State.Ground;
        switch (_escIntermediate)
        {
            case '(': _g0Graphics = c == '0'; break;
            case ')': _g1Graphics = c == '0'; break;
            default: break;
        }
    }

    /// <summary>
    /// The payload of a string escape (OSC and friends) — swallowed, never drawn.
    /// </summary>
    /// <remarks>
    /// A string ends at BEL <em>or</em> at ST, which is spelled ESC-backslash — and only the BEL
    /// spelling used to be recognised here. A remote prompt that sets the window title the ST way
    /// (most do) therefore put the parser into a state nothing could leave: every byte from then
    /// on was swallowed, so the terminal froze mid-session with typing producing nothing on screen
    /// — no letters, no digits, not even a new line. ESC hands the character back to the escape
    /// parser, which is both terminators at once: ESC-backslash ends the string, and a real escape
    /// starting mid-string (an abort) is picked up correctly too.
    /// </remarks>
    private void ProcessString(char c)
    {
        if (c == Bel)
        {
            _state = State.Ground;
            return;
        }
        if (c == Esc)
        {
            _state = State.Escape;
            return;
        }
        // No string escape carries a control code; one means the stream is out of sync, so drop
        // back to printing and let Ground deal with the character.
        if (c < ' ')
        {
            _state = State.Ground;
            ProcessGround(c);
            return;
        }
        if (++_stringLength > MaxSequenceLength)
            _state = State.Ground;
    }

    private void ProcessCsi(char c)
    {
        // Private markers < = > ? (0x3C–0x3F) — ESC[?25l and the DA queries.
        if (c is >= '<' and <= '?')
        {
            _privateSeq = true;
            return;
        }
        if ((c >= '0' && c <= '9') || c == ';' || c == ':')
        {
            // ':' separates sub-parameters (SGR 38:2::r:g:b). They are kept as written: only SGR
            // reads them, and everything else looks at the number before the first colon.
            if (_params.Length < MaxParamsLength)
                _params.Append(c);
            return;
        }
        // Intermediate bytes (0x20–0x2F) belong to sequences we don't implement — consume them
        // and wait for the final byte rather than mistaking one for the end of the sequence.
        if (c is >= ' ' and <= '/')
            return;

        // Final byte (0x40–0x7E) dispatches the command.
        if (c is >= '@' and <= '~')
        {
            DispatchCsi(c, ParseParams());
            _state = State.Ground;
            return;
        }

        // A control code inside a CSI means the sequence was cut short; print it instead of
        // waiting forever for a final byte that is never coming.
        _state = State.Ground;
        ProcessGround(c);
    }

    private void DispatchCsi(char final, int[] ps)
    {
        if (_privateSeq)
        {
            if (final is 'h' or 'l')
                foreach (var mode in ps)
                    SetPrivateMode(mode, final == 'h');
            // Everything else private (DA2 queries, xterm key-modifier options, …) is ignored.
            return;
        }

        var n = Arg(ps, 0, 1);
        switch (final)
        {
            case 'm': ApplySgr(_params.ToString()); break;
            case 'H' or 'f': MoveTo(Arg(ps, 0, 1) - 1, Arg(ps, 1, 1) - 1); break;
            case 'A': CursorUp(n); break;
            case 'B' or 'e': CursorDown(n); break;
            case 'C' or 'a': _cursorCol = ClampCol(_cursorCol + n); break;
            case 'D': _cursorCol = ClampCol(Math.Min(_cursorCol, Columns - 1) - n); break;
            case 'E': CursorDown(n); _cursorCol = 0; break;
            case 'F': CursorUp(n); _cursorCol = 0; break;
            case 'G' or '`': _cursorCol = ClampCol(n - 1); break;
            case 'd': MoveTo(n - 1, _cursorCol); break;
            case 'J': EraseInDisplay(Arg(ps, 0, 0)); break;
            case 'K': EraseInLine(Arg(ps, 0, 0)); break;
            case 'L': InsertLines(n); break;
            case 'M': DeleteLines(n); break;
            case '@': InsertChars(n); break;
            case 'P': DeleteChars(n); break;
            case 'X': EraseChars(n); break;
            case 'S': ScrollUp(n); break;
            case 'T': ShiftDown(_scrollTop, _scrollBottom, n); break;
            case 'b': RepeatLast(n); break;
            case 'r': SetScrollRegion(Arg(ps, 0, 1) - 1, Arg(ps, 1, Rows) - 1); break;
            case 's': SaveCursor(ref _saved); break;
            case 'u': RestoreCursor(_saved); break;
            case 'n': DeviceStatus(Arg(ps, 0, 0)); break;
            case 'c' when Arg(ps, 0, 0) == 0: Reply?.Invoke("\x1b[?1;2c"); break; // a VT100 with AVO
            default: break; // unsupported — ignore
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on; break;
            case 6:
                _originMode = on;
                MoveTo(0, 0);
                break;
            case 7: _autoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 47 or 1047: SwitchScreen(on, saveCursor: false); break;
            case 1049: SwitchScreen(on, saveCursor: true); break;
            case 2004: BracketedPaste = on; break;
            default: break; // mouse modes, focus events, … — not modelled
        }
    }

    // ?1049 is "save the cursor, switch to a cleared alternate screen" and, on the way back,
    // "switch back and restore the cursor". ?47 / ?1047 switch without the cursor dance.
    private void SwitchScreen(bool alternate, bool saveCursor)
    {
        if (alternate == IsAlternateScreen)
            return;

        if (alternate)
        {
            if (saveCursor)
                SaveCursor(ref _mainCursor);
            _mainScreen = _screen;
            _screen = new TerminalCell[Rows][];
            for (var r = 0; r < Rows; r++)
                _screen[r] = NewBlankRow(Columns);
        }
        else
        {
            _screen = _mainScreen!;
            _mainScreen = null;
            if (saveCursor)
                RestoreCursor(_mainCursor);
        }
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    private void DeviceStatus(int query)
    {
        switch (query)
        {
            case 5: Reply?.Invoke("\x1b[0n"); break; // "terminal OK"
            case 6:
                var row = _originMode ? _cursorRow - _scrollTop : _cursorRow;
                Reply?.Invoke(string.Format(CultureInfo.InvariantCulture, "\x1b[{0};{1}R", row + 1, CursorColumn + 1));
                break;
        }
    }

    // SGR arrives in two spellings for the extended colors: the common ';' one, where 38;5;n and
    // 38;2;r;g;b borrow the parameters that follow, and the ITU ':' one, where the whole color
    // sits inside a single parameter (38:5:n, 38:2::r:g:b, 38:2:r:g:b).
    private void ApplySgr(string raw)
    {
        if (raw.Length == 0)
        {
            ResetAttributes();
            return;
        }

        var parts = raw.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.IndexOf(':') >= 0)
            {
                ApplySgrWithSubParams(part.Split(':'));
                continue;
            }

            var p = ParseInt(part);
            switch (p)
            {
                case 0: ResetAttributes(); break;
                case 1: _attrs |= TerminalAttributes.Bold; break;
                case 2: _attrs |= TerminalAttributes.Dim; break;
                case 3: _attrs |= TerminalAttributes.Italic; break;
                case 4 or 21: _attrs |= TerminalAttributes.Underline; break;
                case 7: _attrs |= TerminalAttributes.Inverse; break;
                case 8: _attrs |= TerminalAttributes.Hidden; break;
                case 9: _attrs |= TerminalAttributes.Strikethrough; break;
                case 22: _attrs &= ~(TerminalAttributes.Bold | TerminalAttributes.Dim); break;
                case 23: _attrs &= ~TerminalAttributes.Italic; break;
                case 24: _attrs &= ~TerminalAttributes.Underline; break;
                case 27: _attrs &= ~TerminalAttributes.Inverse; break;
                case 28: _attrs &= ~TerminalAttributes.Hidden; break;
                case 29: _attrs &= ~TerminalAttributes.Strikethrough; break;
                case >= 30 and <= 37: _fg = TerminalColor.FromIndex(p - 30); break;
                case 39: _fg = TerminalColor.Default; break;
                case >= 40 and <= 47: _bg = TerminalColor.FromIndex(p - 40); break;
                case 49: _bg = TerminalColor.Default; break;
                case >= 90 and <= 97: _fg = TerminalColor.FromIndex(p - 90 + 8); break;
                case >= 100 and <= 107: _bg = TerminalColor.FromIndex(p - 100 + 8); break;
                case 38: i = ConsumeExtendedColor(parts, i, ref _fg); break;
                case 48: i = ConsumeExtendedColor(parts, i, ref _bg); break;
                default: break;
            }
        }
    }

    private void ApplySgrWithSubParams(string[] sub)
    {
        switch (ParseInt(sub[0]))
        {
            case 38: _fg = ExtendedColor(sub) ?? _fg; break;
            case 48: _bg = ExtendedColor(sub) ?? _bg; break;
            // 4:0 is "no underline"; 4:1..4:5 are underline styles, all drawn as one line here.
            case 4:
                if (sub.Length > 1 && ParseInt(sub[1]) == 0)
                    _attrs &= ~TerminalAttributes.Underline;
                else
                    _attrs |= TerminalAttributes.Underline;
                break;
            default: break;
        }
    }

    // ';' spelling: 38;5;n or 38;2;r;g;b. Returns the index of the last parameter consumed. A
    // truncated sequence leaves the color as it was rather than guessing.
    private static int ConsumeExtendedColor(string[] parts, int i, ref TerminalColor color)
    {
        if (i + 1 >= parts.Length)
            return i;

        switch (ParseInt(parts[i + 1]))
        {
            case 5 when i + 2 < parts.Length:
                color = TerminalColor.FromIndex(Math.Clamp(ParseInt(parts[i + 2]), 0, 255));
                return i + 2;
            case 2 when i + 4 < parts.Length:
                color = TerminalColor.FromRgb(
                    ParseInt(parts[i + 2]), ParseInt(parts[i + 3]), ParseInt(parts[i + 4]));
                return i + 4;
            default:
                return i + 1;
        }
    }

    // ':' spelling — sub[0] is 38/48, then 5:n, 2:r:g:b, or 2:colorspace:r:g:b.
    private static TerminalColor? ExtendedColor(string[] sub)
    {
        if (sub.Length < 2)
            return null;

        var mode = ParseInt(sub[1]);
        var args = sub.Length - 2;
        if (mode == 5 && args >= 1)
            return TerminalColor.FromIndex(Math.Clamp(ParseInt(sub[2]), 0, 255));
        if (mode == 2 && args >= 3)
        {
            // With four arguments the first is the color-space id, usually left empty.
            var r = args >= 4 ? 3 : 2;
            return TerminalColor.FromRgb(ParseInt(sub[r]), ParseInt(sub[r + 1]), ParseInt(sub[r + 2]));
        }
        return null;
    }

    private void PutChar(char c)
    {
        if (_cursorCol >= Columns)
        {
            if (_autoWrap)
            {
                MarkWrapped(_screen[_cursorRow], true);
                _cursorCol = 0;
                LineFeed();
            }
            else
            {
                // No autowrap: the last column is overwritten until something moves the cursor.
                _cursorCol = Columns - 1;
            }
        }

        var graphics = _shiftOut ? _g1Graphics : _g0Graphics;
        _screen[_cursorRow][_cursorCol] =
            new TerminalCell(graphics ? DecSpecialGraphics(c) : c, _fg, _bg, _attrs);
        _cursorCol++;
        _lastPrinted = c;
    }

    // REP — the last printed character again, n times. ncurses uses it to draw long runs of the
    // same character (a horizontal rule, a blank stretch of a status line).
    private void RepeatLast(int n)
    {
        if (_lastPrinted == '\0')
            return;
        for (var i = 0; i < Math.Min(n, Columns * Rows); i++)
            PutChar(_lastPrinted);
    }

    // The DEC special graphics set: the lower-case letters and a few symbols turn into the
    // box-drawing pieces full-screen tools draw their frames with.
    private static char DecSpecialGraphics(char c) => c switch
    {
        '`' => '◆', 'a' => '▒', 'b' => '␉', 'c' => '␌', 'd' => '␍', 'e' => '␊', 'f' => '°',
        'g' => '±', 'h' => '␤', 'i' => '␋', 'j' => '┘', 'k' => '┐', 'l' => '┌', 'm' => '└',
        'n' => '┼', 'o' => '⎺', 'p' => '⎻', 'q' => '─', 'r' => '⎼', 's' => '⎽', 't' => '├',
        'u' => '┤', 'v' => '┴', 'w' => '┬', 'x' => '│', 'y' => '≤', 'z' => '≥', '{' => 'π',
        '|' => '≠', '}' => '£', '~' => '·',
        _ => c,
    };

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom)
            ScrollUp(1);
        else if (_cursorRow < Rows - 1)
            _cursorRow++;
    }

    private void ReverseIndex()
    {
        if (_cursorRow == _scrollTop)
            ShiftDown(_scrollTop, _scrollBottom, 1);
        else if (_cursorRow > 0)
            _cursorRow--;
    }

    // Scroll the region up by n. Rows leaving the very top of the main screen become history;
    // anything scrolled inside a smaller region, or on the alternate screen, is just gone.
    private void ScrollUp(int n)
    {
        n = Math.Min(n, _scrollBottom - _scrollTop + 1);
        if (!IsAlternateScreen && _scrollTop == 0)
        {
            for (var r = 0; r < n; r++)
                _scrollback.Add(_screen[r]);
            TrimScrollback();
        }
        ShiftUp(_scrollTop, _scrollBottom, n);
    }

    // Rows from..to (inclusive) move up by n; blank rows fill in at the bottom of the range.
    private void ShiftUp(int from, int to, int n)
    {
        n = Math.Min(n, to - from + 1);
        if (n <= 0)
            return;
        for (var r = from; r <= to - n; r++)
            _screen[r] = _screen[r + n];
        for (var r = to - n + 1; r <= to; r++)
            _screen[r] = NewErasedRow();
    }

    // Rows from..to (inclusive) move down by n; blank rows fill in at the top of the range.
    private void ShiftDown(int from, int to, int n)
    {
        n = Math.Min(n, to - from + 1);
        if (n <= 0)
            return;
        for (var r = to; r >= from + n; r--)
            _screen[r] = _screen[r - n];
        for (var r = from; r < from + n; r++)
            _screen[r] = NewErasedRow();
    }

    // IL / DL work from the cursor row to the bottom of the scroll region, and do nothing when
    // the cursor is outside it.
    private void InsertLines(int n)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
            return;
        ShiftDown(_cursorRow, _scrollBottom, n);
        _cursorCol = 0;
    }

    private void DeleteLines(int n)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
            return;
        ShiftUp(_cursorRow, _scrollBottom, n);
        _cursorCol = 0;
    }

    private void InsertChars(int n)
    {
        var row = _screen[_cursorRow];
        var col = Math.Min(_cursorCol, Columns - 1);
        n = Math.Min(n, Columns - col);
        Array.Copy(row, col, row, col + n, Columns - col - n);
        for (var c = col; c < col + n; c++)
            row[c] = BlankCell();
    }

    private void DeleteChars(int n)
    {
        var row = _screen[_cursorRow];
        var col = Math.Min(_cursorCol, Columns - 1);
        n = Math.Min(n, Columns - col);
        Array.Copy(row, col + n, row, col, Columns - col - n);
        for (var c = Columns - n; c < Columns; c++)
            row[c] = BlankCell();
    }

    private void EraseChars(int n)
    {
        var row = _screen[_cursorRow];
        var col = Math.Min(_cursorCol, Columns - 1);
        for (var c = col; c < Math.Min(Columns, col + n); c++)
            row[c] = BlankCell();
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Rows - 1);
        if (top < 0 || top >= bottom)
            return;
        _scrollTop = top;
        _scrollBottom = bottom;
        MoveTo(0, 0);
    }

    // CUP / VPA. In origin mode rows count from the top of the scroll region and stay inside it.
    private void MoveTo(int row, int col)
    {
        if (_originMode)
            _cursorRow = Math.Clamp(row + _scrollTop, _scrollTop, _scrollBottom);
        else
            _cursorRow = ClampRow(row);
        _cursorCol = ClampCol(col);
    }

    // Cursor up/down stop at the scroll margins when they start inside the region.
    private void CursorUp(int n)
    {
        var limit = _cursorRow >= _scrollTop ? _scrollTop : 0;
        _cursorRow = Math.Max(limit, _cursorRow - n);
    }

    private void CursorDown(int n)
    {
        var limit = _cursorRow <= _scrollBottom ? _scrollBottom : Rows - 1;
        _cursorRow = Math.Min(limit, _cursorRow + n);
    }

    private void EraseInLine(int mode)
    {
        var row = _screen[_cursorRow];
        var col = Math.Min(_cursorCol, Columns - 1);
        var (from, to) = mode switch
        {
            1 => (0, col),
            2 => (0, Columns - 1),
            _ => (col, Columns - 1),
        };
        for (var c = from; c <= to && c < Columns; c++)
            row[c] = BlankCell();
        // A line editor redraws a shortened command by erasing to the end of the row; whatever
        // follows starts on a line of its own.
        if (mode != 1)
            MarkWrapped(row, false);
    }

    private void EraseInDisplay(int mode)
    {
        var col = Math.Min(_cursorCol, Columns - 1);
        switch (mode)
        {
            case 1:
                for (var r = 0; r < _cursorRow; r++) ClearRow(r);
                for (var c = 0; c <= col; c++) _screen[_cursorRow][c] = BlankCell();
                break;
            case 2:
                for (var r = 0; r < Rows; r++) ClearRow(r);
                break;
            case 3:
                // `clear` sends this after ED 2: forget the history too.
                if (!IsAlternateScreen)
                    _scrollback.Clear();
                break;
            default:
                for (var c = col; c < Columns; c++) _screen[_cursorRow][c] = BlankCell();
                MarkWrapped(_screen[_cursorRow], false);
                for (var r = _cursorRow + 1; r < Rows; r++) ClearRow(r);
                break;
        }
    }

    private void TrimScrollback()
    {
        var excess = _scrollback.Count - _scrollbackLimit;
        if (excess > 0)
            _scrollback.RemoveRange(0, excess);
    }

    private void ClearRow(int row)
    {
        var r = _screen[row];
        for (var c = 0; c < Columns; c++)
            r[c] = BlankCell();
        MarkWrapped(r, false);
    }

    private void MarkWrapped(TerminalCell[] row, bool wrapped)
    {
        if (wrapped)
            _wrapped.AddOrUpdate(row, WrapMark);
        else
            _wrapped.Remove(row);
    }

    private void SaveCursor(ref SavedCursor slot)
    {
        slot.Row = _cursorRow;
        slot.Col = _cursorCol;
        slot.Fg = _fg;
        slot.Bg = _bg;
        slot.Attrs = _attrs;
        slot.OriginMode = _originMode;
        slot.G0Graphics = _g0Graphics;
        slot.G1Graphics = _g1Graphics;
        slot.ShiftOut = _shiftOut;
    }

    private void RestoreCursor(SavedCursor slot)
    {
        _cursorRow = ClampRow(slot.Row);
        _cursorCol = Math.Clamp(slot.Col, 0, Columns);
        _fg = slot.Fg;
        _bg = slot.Bg;
        _attrs = slot.Attrs;
        _originMode = slot.OriginMode;
        _g0Graphics = slot.G0Graphics;
        _g1Graphics = slot.G1Graphics;
        _shiftOut = slot.ShiftOut;
    }

    private void FullReset()
    {
        if (IsAlternateScreen)
        {
            _screen = _mainScreen!;
            _mainScreen = null;
        }
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _originMode = false;
        _autoWrap = true;
        _g0Graphics = _g1Graphics = _shiftOut = false;
        _saved = default;
        _mainCursor = default;
        BracketedPaste = false;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        ResetAttributes();
        for (var r = 0; r < Rows; r++) ClearRow(r);
        _cursorRow = 0;
        _cursorCol = 0;
    }

    private void ResetAttributes()
    {
        _fg = TerminalColor.Default;
        _bg = TerminalColor.Default;
        _attrs = TerminalAttributes.None;
    }

    // Erased cells keep the active background so colored fills survive a clear.
    private TerminalCell BlankCell() =>
        new(' ', TerminalColor.Default, _bg, TerminalAttributes.None);

    private TerminalCell[] NewErasedRow()
    {
        var row = new TerminalCell[Columns];
        var blank = BlankCell();
        for (var c = 0; c < Columns; c++)
            row[c] = blank;
        return row;
    }

    private int[] ParseParams()
    {
        if (_params.Length == 0)
            return Array.Empty<int>();

        var parts = _params.ToString().Split(';');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var colon = part.IndexOf(':');
            result[i] = ParseInt(colon >= 0 ? part.Substring(0, colon) : part);
        }
        return result;
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Arg(int[] ps, int index, int fallback) =>
        index < ps.Length && ps[index] > 0 ? ps[index] : fallback;

    private int ClampRow(int r) => Math.Clamp(r, 0, Rows - 1);
    private int ClampCol(int c) => Math.Clamp(c, 0, Columns - 1);

    private static TerminalCell[] NewBlankRow(int columns)
    {
        var row = new TerminalCell[columns];
        for (var c = 0; c < columns; c++)
            row[c] = TerminalCell.Blank;
        return row;
    }
}
