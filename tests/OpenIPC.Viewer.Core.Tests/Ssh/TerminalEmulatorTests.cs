using System.Linq;
using System.Text;
using OpenIPC.Viewer.Core.Ssh.Terminal;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Ssh;

// Basic-VT emulator (Phase 13.3): printable text, control codes, CSI moves,
// SGR colors, scroll, and UTF-8 reassembly across feed boundaries.
public sealed class TerminalEmulatorTests
{
    private static string RowText(TerminalEmulator t, int row) =>
        new string(t.GetRow(row).Select(c => c.Char).ToArray()).TrimEnd();

    [Fact]
    public void PrintableText_LandsAtCursorAndAdvances()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("hello");
        Assert.Equal("hello", RowText(t, 0));
        Assert.Equal(5, t.CursorColumn);
        Assert.Equal(0, t.CursorRow);
    }

    [Fact]
    public void CrLf_WrapsToNextRow()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("a\r\nb");
        Assert.Equal("a", RowText(t, 0));
        Assert.Equal("b", RowText(t, 1));
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void Sgr_SetsForegroundColorIndex()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[31mX");
        Assert.Equal(1, t.GetRow(0)[0].Foreground); // 31 -> index 1 (red)
        // Reset returns to default.
        t.Feed("\x1b[0mY");
        Assert.Equal(TerminalPalette.DefaultForeground, t.GetRow(0)[1].Foreground);
    }

    [Fact]
    public void CursorPosition_PlacesCharAtRowCol()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[2;3HZ"); // 1-based row 2, col 3
        Assert.Equal('Z', t.GetRow(1)[2].Char);
    }

    [Fact]
    public void EraseDisplay_ClearsEverything()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("noise\r\nmore");
        t.Feed("\x1b[2J");
        Assert.Equal("", RowText(t, 0));
        Assert.Equal("", RowText(t, 1));
    }

    [Fact]
    public void Overflow_ScrollsContentUp()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("1\r\n2\r\n3\r\n4"); // 4 lines into a 3-row screen
        Assert.Equal("2", RowText(t, 0));
        Assert.Equal("3", RowText(t, 1));
        Assert.Equal("4", RowText(t, 2));
    }

    [Fact]
    public void Utf8_ReassemblesAcrossFeedBoundaries()
    {
        var t = new TerminalEmulator(20, 5);
        var bytes = Encoding.UTF8.GetBytes("é"); // 2 bytes (0xC3 0xA9)
        t.Feed(bytes[..1]); // partial — must not corrupt
        t.Feed(bytes[1..]);
        Assert.Equal('é', t.GetRow(0)[0].Char);
    }

    [Fact]
    public void PrivateMode_IsConsumedNotPrinted()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[?25lhi"); // hide-cursor private seq + "hi"
        Assert.Equal("hi", RowText(t, 0));
    }
}
