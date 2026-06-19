using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenIPC.Viewer.Core.Ssh.Terminal;

/// <summary>
/// A deliberately small VT/ANSI terminal emulator (phase-13 §13.3 "basic VT" —
/// no alt-screen, mouse, or full ncurses). Feeds UTF-8 bytes through a state
/// machine that maintains a character grid: printable text, the common control
/// codes, CSI cursor/erase moves, and SGR colors. Unknown escapes are consumed
/// and ignored rather than corrupting the screen.
/// </summary>
public sealed class TerminalEmulator
{
    private const char Esc = '\x1b';
    private const char Bel = '\x07';

    private enum State { Ground, Escape, Csi, Osc }

    private TerminalCell[][] _screen = Array.Empty<TerminalCell[]>();
    private readonly List<TerminalCell[]> _scrollback = new();
    private const int MaxScrollback = 1000;

    private int _cursorRow;
    private int _cursorCol;
    private int _savedRow;
    private int _savedCol;

    private byte _fg = TerminalPalette.DefaultForeground;
    private byte _bg = TerminalPalette.DefaultBackground;
    private bool _bold;

    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private bool _privateSeq;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _charBuf = new char[1024];

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow => _cursorRow;
    public int CursorColumn => _cursorCol;

    /// <summary>Raised after a <see cref="Feed(byte[])"/> batch mutates the grid.</summary>
    public event Action? Updated;

    public TerminalEmulator(int columns, int rows) => Resize(columns, rows);

    public TerminalCell[] GetRow(int row) => _screen[row];

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
            return;

        var next = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            next[r] = NewBlankRow(columns);
            if (r < Rows && _screen.Length > 0)
            {
                var old = _screen[r];
                var copy = Math.Min(columns, old.Length);
                Array.Copy(old, next[r], copy);
            }
        }

        Columns = columns;
        Rows = rows;
        _screen = next;
        _cursorRow = Math.Min(_cursorRow, rows - 1);
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
            case State.Osc:
                // OS Command (e.g. window title) — swallow until BEL. Not rendered.
                if (c == Bel) _state = State.Ground;
                break;
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
            case ']':
                _state = State.Osc;
                break;
            case 'c': // RIS — full reset
                ResetScreen();
                _state = State.Ground;
                break;
            default:
                // Charset selection ESC( / ESC) and other two-char escapes —
                // ignore the parameter byte and return to ground.
                _state = State.Ground;
                break;
        }
    }

    private void ProcessCsi(char c)
    {
        if (c == '?')
        {
            _privateSeq = true;
            return;
        }
        if ((c >= '0' && c <= '9') || c == ';')
        {
            _params.Append(c);
            return;
        }

        // Final byte (0x40–0x7E) dispatches the command.
        DispatchCsi(c, ParseParams());
        _state = State.Ground;
    }

    private void DispatchCsi(char final, int[] ps)
    {
        // Private sequences (ESC[?…) are mode toggles like cursor visibility —
        // we don't model them, just consume.
        if (_privateSeq)
            return;

        switch (final)
        {
            case 'm': ApplySgr(ps); break;
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

    private void ApplySgr(int[] ps)
    {
        if (ps.Length == 0)
        {
            ResetAttributes();
            return;
        }

        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            switch (p)
            {
                case 0: ResetAttributes(); break;
                case 1: _bold = true; break;
                case 22: _bold = false; break;
                case >= 30 and <= 37: _fg = (byte)(p - 30); break;
                case 39: _fg = TerminalPalette.DefaultForeground; break;
                case >= 40 and <= 47: _bg = (byte)(p - 40); break;
                case 49: _bg = TerminalPalette.DefaultBackground; break;
                case >= 90 and <= 97: _fg = (byte)(p - 90 + 8); break;
                case >= 100 and <= 107: _bg = (byte)(p - 100 + 8); break;
                case 38: i = ConsumeExtendedColor(ps, i, out _fg); break;
                case 48: i = ConsumeExtendedColor(ps, i, out _bg); break;
                default: break;
            }
        }
    }

    // 38/48 ; 5 ; n  (256-color)  or  38/48 ; 2 ; r ; g ; b  (truecolor). We map
    // a low index to its palette slot and fall back to default for the rest.
    private static int ConsumeExtendedColor(int[] ps, int i, out byte color)
    {
        color = TerminalPalette.DefaultForeground;
        if (i + 1 >= ps.Length)
            return i;

        var mode = ps[i + 1];
        if (mode == 5 && i + 2 < ps.Length)
        {
            var n = ps[i + 2];
            color = n < 16 ? (byte)n : TerminalPalette.DefaultForeground;
            return i + 2;
        }
        if (mode == 2 && i + 4 < ps.Length)
            return i + 4; // truecolor not represented — leave default

        return i + 1;
    }

    private void PutChar(char c)
    {
        if (_cursorCol >= Columns)
        {
            _cursorCol = 0;
            LineFeed();
        }
        _screen[_cursorRow][_cursorCol] = new TerminalCell(c, _fg, _bg, _bold);
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
        if (_scrollback.Count > MaxScrollback)
            _scrollback.RemoveAt(0);

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
                for (var r = _cursorRow + 1; r < Rows; r++) ClearRow(r);
                break;
        }
    }

    private void ClearRow(int row)
    {
        var r = _screen[row];
        for (var c = 0; c < Columns; c++)
            r[c] = BlankCell();
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
        _fg = TerminalPalette.DefaultForeground;
        _bg = TerminalPalette.DefaultBackground;
        _bold = false;
    }

    // Erased cells keep the active background so colored fills survive a clear.
    private TerminalCell BlankCell() =>
        new(' ', TerminalPalette.DefaultForeground, _bg, false);

    private int[] ParseParams()
    {
        if (_params.Length == 0)
            return Array.Empty<int>();

        var parts = _params.ToString().Split(';');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            result[i] = int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return result;
    }

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
