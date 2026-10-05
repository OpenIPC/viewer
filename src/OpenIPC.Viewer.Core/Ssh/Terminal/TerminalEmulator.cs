using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenIPC.Viewer.Core.Ssh.Terminal;

/// <summary>
/// A deliberately small VT/ANSI terminal emulator (phase-13 §13.3 "basic VT" —
/// no alt-screen, mouse, or full ncurses). Feeds UTF-8 bytes through a state
/// machine that maintains a character grid: printable text, the common control
/// codes, CSI cursor/erase moves, and SGR renditions (16/256/truecolor, bold, dim,
/// italic, underline, inverse, hidden, strikethrough). Unknown escapes are
/// consumed and ignored rather than corrupting the screen.
/// </summary>
public sealed class TerminalEmulator
{
    private const char Esc = '\x1b';
    private const char Bel = '\x07';

    private enum State { Ground, Escape, Csi, String }

    private TerminalCell[][] _screen = Array.Empty<TerminalCell[]>();
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
    private int _savedRow;
    private int _savedCol;

    private TerminalColor _fg;
    private TerminalColor _bg;
    private TerminalAttributes _attrs;

    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private bool _privateSeq;
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
    public int CursorRow => _cursorRow;
    public int CursorColumn => _cursorCol;

    /// <summary>
    /// The remote turned on bracketed paste (CSI ?2004h): pasted text is to be sent between
    /// ESC[200~ and ESC[201~, and the shell will insert it rather than run its lines.
    /// </summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>False while the remote has hidden the cursor (CSI ?25l) — full-screen tools do.</summary>
    public bool CursorVisible { get; private set; } = true;

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

    public TerminalEmulator(int columns, int rows, int scrollbackLimit = DefaultScrollbackLimit)
    {
        ScrollbackLimit = scrollbackLimit;
        Resize(columns, rows);
    }

    public TerminalCell[] GetRow(int row) => _screen[row];

    /// <summary>Rows that have scrolled off the top and are still remembered.</summary>
    public int ScrollbackRows => _scrollback.Count;

    /// <summary>One remembered row; index 0 is the oldest.</summary>
    public TerminalCell[] GetScrollbackRow(int index) => _scrollback[index];

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
            return;

        var had = _screen.Length > 0;

        // Losing rows takes them off the TOP, the way a real terminal does it: the interesting end
        // of a shell session is the bottom one. Keeping the top instead would drop the prompt every
        // time the view got shorter — which on a phone is every time the soft keyboard opens.
        var drop = had ? Math.Max(0, Rows - rows) : 0;
        for (var r = 0; r < drop; r++)
            _scrollback.Add(_screen[r]);
        TrimScrollback();

        // And they come back when the view grows again. Without this the trip is one-way: showing
        // and hiding the keyboard once ate a few rows of output for good, and doing it a few times
        // scrolled a long listing off the top with no way to get it back.
        var restore = had ? Math.Min(Math.Max(0, rows - Rows), _scrollback.Count) : 0;

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
                source = had && from < Rows ? _screen[from] : null;
            }

            if (source is not null)
            {
                Array.Copy(source, next[r], Math.Min(columns, source.Length));
                if (IsWrapped(source))
                    MarkWrapped(next[r], true);
            }
        }

        if (restore > 0)
            _scrollback.RemoveRange(_scrollback.Count - restore, restore);

        Columns = columns;
        Rows = rows;
        _screen = next;
        _cursorRow = Math.Clamp(_cursorRow - drop + restore, 0, rows - 1);
        _cursorCol = Math.Min(_cursorCol, columns - 1);
        Updated?.Invoke();
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
            case '\n': LineFeed(); break;
            case '\b': if (_cursorCol > 0) _cursorCol--; break;
            case '\t': _cursorCol = Math.Min(Columns - 1, (_cursorCol / 8 + 1) * 8); break;
            case Bel: break;
            default:
                if (!char.IsControl(c))
                    PutChar(c);
                break;
        }
    }

    private void ProcessEscape(char c)
    {
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
            case 'c': // RIS — full reset
                ResetScreen();
                BracketedPaste = false;
                CursorVisible = true;
                _state = State.Ground;
                break;
            default:
                // Charset selection ESC( / ESC) and other two-char escapes —
                // ignore the parameter byte and return to ground.
                _state = State.Ground;
                break;
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
        // Private sequences (ESC[?…) are mode toggles. The ones we model are bracketed paste and
        // cursor visibility; the rest (alt screen, mouse, …) are consumed and ignored.
        if (_privateSeq)
        {
            if (final is 'h' or 'l')
            {
                if (Array.IndexOf(ps, 2004) >= 0)
                    BracketedPaste = final == 'h';
                if (Array.IndexOf(ps, 25) >= 0)
                    CursorVisible = final == 'h';
            }
            return;
        }

        switch (final)
        {
            case 'm': ApplySgr(_params.ToString()); break;
            case 'H' or 'f':
                _cursorRow = ClampRow(Arg(ps, 0, 1) - 1);
                _cursorCol = ClampCol(Arg(ps, 1, 1) - 1);
                break;
            case 'A': _cursorRow = ClampRow(_cursorRow - Arg(ps, 0, 1)); break;
            case 'B': _cursorRow = ClampRow(_cursorRow + Arg(ps, 0, 1)); break;
            case 'C': _cursorCol = ClampCol(_cursorCol + Arg(ps, 0, 1)); break;
            case 'D': _cursorCol = ClampCol(_cursorCol - Arg(ps, 0, 1)); break;
            case 'G': _cursorCol = ClampCol(Arg(ps, 0, 1) - 1); break;
            case 'd': _cursorRow = ClampRow(Arg(ps, 0, 1) - 1); break;
            case 'J': EraseInDisplay(Arg(ps, 0, 0)); break;
            case 'K': EraseInLine(Arg(ps, 0, 0)); break;
            case 's': _savedRow = _cursorRow; _savedCol = _cursorCol; break;
            case 'u': _cursorRow = ClampRow(_savedRow); _cursorCol = ClampCol(_savedCol); break;
            default: break; // unsupported — ignore
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
            MarkWrapped(_screen[_cursorRow], true);
            _cursorCol = 0;
            LineFeed();
        }
        _screen[_cursorRow][_cursorCol] = new TerminalCell(c, _fg, _bg, _attrs);
        _cursorCol++;
    }

    private void LineFeed()
    {
        if (_cursorRow >= Rows - 1)
            ScrollUp();
        else
            _cursorRow++;
    }

    private void ScrollUp()
    {
        _scrollback.Add(_screen[0]);
        TrimScrollback();

        for (var r = 1; r < Rows; r++)
            _screen[r - 1] = _screen[r];
        _screen[Rows - 1] = NewBlankRow(Columns);
    }

    private void EraseInLine(int mode)
    {
        var row = _screen[_cursorRow];
        var (from, to) = mode switch
        {
            1 => (0, _cursorCol),
            2 => (0, Columns - 1),
            _ => (_cursorCol, Columns - 1),
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
        switch (mode)
        {
            case 1:
                for (var r = 0; r < _cursorRow; r++) ClearRow(r);
                for (var c = 0; c <= _cursorCol && c < Columns; c++) _screen[_cursorRow][c] = BlankCell();
                break;
            case 2:
                for (var r = 0; r < Rows; r++) ClearRow(r);
                break;
            default:
                for (var c = _cursorCol; c < Columns; c++) _screen[_cursorRow][c] = BlankCell();
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

    private void ResetScreen()
    {
        for (var r = 0; r < Rows; r++) ClearRow(r);
        _cursorRow = 0;
        _cursorCol = 0;
        ResetAttributes();
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
