using System.Collections.Generic;
using System.Linq;
using OpenIPC.Viewer.Core.Ssh.Terminal;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Ssh;

// What full-screen tools (vi, less, htop, mc) need from the terminal: the alternate screen,
// scroll regions, line and character insert/delete, cursor-key mode, the line-drawing charset
// and answers to status queries.
public sealed class TerminalFullScreenTests
{
    private static string RowText(TerminalEmulator t, int row) =>
        new string(t.GetRow(row).Select(c => c.Char).ToArray()).TrimEnd();

    private static TerminalEmulator Lines(int cols, int rows, params string[] lines)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(string.Join("\r\n", lines));
        return t;
    }

    [Fact]
    public void AlternateScreen_1049_IsBlank_AndRestoresMainScreenAndCursor()
    {
        var t = Lines(20, 4, "$ ls", "a b c", "$ vi");
        Assert.Equal((2, 4), (t.CursorRow, t.CursorColumn));

        t.Feed("\x1b[?1049h");
        Assert.True(t.IsAlternateScreen);
        Assert.All(Enumerable.Range(0, 4), r => Assert.Equal("", RowText(t, r)));

        t.Feed("\x1b[1;1Hediting\x1b[3;5H");
        t.Feed("\x1b[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("$ ls", RowText(t, 0));
        Assert.Equal("$ vi", RowText(t, 2));
        Assert.Equal((2, 4), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void AlternateScreen_HasNoHistory_AndLeavesMainHistoryAlone()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("1\r\n2\r\n3\r\n");
        var history = t.ScrollbackRows;
        Assert.True(history > 0);

        t.Feed("\x1b[?1049h");
        Assert.Equal(0, t.ScrollbackRows);
        for (var i = 0; i < 20; i++)
            t.Feed("x\r\n");
        t.Feed("\x1b[?1049l");
        Assert.Equal(history, t.ScrollbackRows);
    }

    [Fact]
    public void AlternateScreen_SurvivesResize_BothScreensFollowTheNewSize()
    {
        var t = Lines(20, 6, "one", "two", "three");
        t.Feed("\x1b[?1049h\x1b[6;1Hbottom");
        t.Resize(30, 4);
        Assert.Equal(30, t.GetRow(0).Length);
        Assert.InRange(t.CursorRow, 0, 3);
        t.Feed("\x1b[?1049l");
        Assert.Equal(30, t.GetRow(0).Length);
        Assert.Equal(4, t.Rows);
        // The main screen shrank the usual way — from the top — so the last line is still there.
        Assert.Equal("three", RowText(t, Enumerable.Range(0, 4).Last(r => RowText(t, r).Length > 0)));
    }

    [Fact]
    public void ScrollRegion_LineFeedAtBottomScrollsOnlyTheRegion()
    {
        var t = Lines(10, 5, "head", "a", "b", "c", "status");
        t.Feed("\x1b[2;4r");          // rows 2..4
        t.Feed("\x1b[4;1H\nd");       // line feed at the region's bottom
        Assert.Equal("head", RowText(t, 0));
        Assert.Equal("b", RowText(t, 1));
        Assert.Equal("c", RowText(t, 2));
        Assert.Equal("d", RowText(t, 3));
        Assert.Equal("status", RowText(t, 4));
    }

    [Fact]
    public void ScrollRegion_InsideItDoesNotFeedHistory()
    {
        var t = Lines(10, 4, "a", "b", "c", "d");
        var before = t.ScrollbackRows;
        t.Feed("\x1b[2;4r\x1b[4;1H\n\n\n");
        Assert.Equal(before, t.ScrollbackRows);
    }

    [Fact]
    public void ReverseIndex_AtRegionTopScrollsDown()
    {
        var t = Lines(10, 4, "a", "b", "c", "d");
        t.Feed("\x1b[1;1H\x1bMz");
        Assert.Equal("z", RowText(t, 0));
        Assert.Equal("a", RowText(t, 1));
        Assert.Equal("c", RowText(t, 3));
    }

    [Fact]
    public void InsertAndDeleteLines_WorkWithinTheRegion()
    {
        var t = Lines(10, 5, "0", "1", "2", "3", "4");
        t.Feed("\x1b[1;4r\x1b[2;1H\x1b[L");
        Assert.Equal(new[] { "0", "", "1", "2", "4" }, Enumerable.Range(0, 5).Select(r => RowText(t, r)));

        t.Feed("\x1b[2M");
        Assert.Equal(new[] { "0", "2", "", "", "4" }, Enumerable.Range(0, 5).Select(r => RowText(t, r)));
    }

    [Fact]
    public void InsertDeleteEraseChars_EditTheRowInPlace()
    {
        var t = Lines(10, 2, "abcdef");
        t.Feed("\x1b[1;3H\x1b[2@");
        Assert.Equal("ab  cdef", RowText(t, 0));
        t.Feed("\x1b[3P");
        Assert.Equal("abdef", RowText(t, 0));
        t.Feed("\x1b[1;1H\x1b[2X");
        Assert.Equal("  def", RowText(t, 0));
    }

    [Fact]
    public void ScrollUpDownCommands_MoveTheRegion()
    {
        var t = Lines(10, 3, "a", "b", "c");
        t.Feed("\x1b[S");
        Assert.Equal(new[] { "b", "c", "" }, Enumerable.Range(0, 3).Select(r => RowText(t, r)));
        t.Feed("\x1b[2T");
        Assert.Equal(new[] { "", "", "b" }, Enumerable.Range(0, 3).Select(r => RowText(t, r)));
    }

    [Fact]
    public void OriginMode_AddressesRelativeToTheRegion()
    {
        var t = new TerminalEmulator(10, 6);
        t.Feed("\x1b[3;5r\x1b[?6h\x1b[1;1HX\x1b[9;1HY");
        Assert.Equal("X", RowText(t, 2));
        Assert.Equal("Y", RowText(t, 4)); // clamped to the region's bottom
    }

    [Fact]
    public void CharsetDesignation_DoesNotPrintItsFinalByte_AndDrawsBoxes()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("\x1b(0lqk\x1b(Bok");
        Assert.Equal("┌─┐ok", RowText(t, 0));
    }

    [Fact]
    public void ShiftOut_SelectsG1()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("\x1b)0x\x0ex\x0fx");
        Assert.Equal("x│x", RowText(t, 0));
    }

    [Fact]
    public void ApplicationCursorKeys_ChangeTheArrowSpelling()
    {
        var t = new TerminalEmulator(10, 2);
        Assert.Equal("\x1b[A", t.CursorKey('A'));
        t.Feed("\x1b[?1h");
        Assert.True(t.ApplicationCursorKeys);
        Assert.Equal("\x1bOA", t.CursorKey('A'));
        t.Feed("\x1b[?1l");
        Assert.Equal("\x1b[D", t.CursorKey('D'));
    }

    [Fact]
    public void StatusQueries_AreAnswered()
    {
        var t = new TerminalEmulator(20, 5);
        var replies = new List<string>();
        t.Reply += replies.Add;
        t.Feed("\x1b[3;7H\x1b[6n\x1b[5n\x1b[c\x1b[>c");
        Assert.Equal(new[] { "\x1b[3;7R", "\x1b[0n", "\x1b[?1;2c" }, replies);
    }

    [Fact]
    public void SaveRestoreCursor_Esc78_KeepsPositionAndRendition()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[2;3H\x1b[31m\x1b" + "7\x1b[0m\x1b[5;10H\x1b" + "8X");
        Assert.Equal('X', t.GetRow(1)[2].Char);
        Assert.Equal(TerminalColor.FromIndex(1), t.GetRow(1)[2].Foreground);
    }

    [Fact]
    public void AutoWrapOff_OverwritesTheLastColumn()
    {
        var t = new TerminalEmulator(5, 2);
        t.Feed("\x1b[?7labcdefg");
        Assert.Equal("abcdg", RowText(t, 0));
        Assert.Equal("", RowText(t, 1));
    }

    [Fact]
    public void Repeat_PrintsTheLastCharacterAgain()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("-\x1b[4b|");
        Assert.Equal("-----|", RowText(t, 0));
    }

    [Fact]
    public void EraseScrollback_Ed3ClearsHistory()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("1\r\n2\r\n3\r\n4");
        Assert.True(t.ScrollbackRows > 0);
        t.Feed("\x1b[3J");
        Assert.Equal(0, t.ScrollbackRows);
    }

    [Fact]
    public void FullReset_LeavesTheAlternateScreenAndModes()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("\x1b[?1049h\x1b[?1h\x1b[2;3r\x1b" + "c");
        Assert.False(t.IsAlternateScreen);
        Assert.False(t.ApplicationCursorKeys);
        // A line feed at the bottom scrolls the whole screen; with the old 2..3 region left
        // behind, row 1 would have stayed put.
        t.Feed("\x1b[1;1Htop\x1b[3;1H\n");
        Assert.Equal("", RowText(t, 0));
    }
}
