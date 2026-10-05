using System;
using System.Linq;
using OpenIPC.Viewer.Core.Ssh.Terminal;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Ssh;

// SGR renditions beyond the 16 basic colors (256-color, truecolor, both parameter spellings),
// the text attributes, cursor visibility, the scrollback limit, and how a theme turns a cell
// into the colors that are actually drawn.
public sealed class TerminalRenditionTests
{
    private static TerminalCell Cell(TerminalEmulator t, int col) => t.GetRow(0)[col];

    [Fact]
    public void Sgr256_KeepsTheFullIndex()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[38;5;196mA\x1b[48;5;21mB");
        Assert.Equal(TerminalColor.FromIndex(196), Cell(t, 0).Foreground);
        Assert.Equal(TerminalColor.FromIndex(21), Cell(t, 1).Background);
    }

    [Fact]
    public void SgrTruecolor_SemicolonSpelling()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[38;2;10;20;30;48;2;200;100;50mX");
        Assert.Equal(TerminalColor.FromRgb(10, 20, 30), Cell(t, 0).Foreground);
        Assert.Equal(TerminalColor.FromRgb(200, 100, 50), Cell(t, 0).Background);
    }

    [Fact]
    public void SgrTruecolor_ColonSpelling_WithAndWithoutColorSpace()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[38:2::1:2:3mA\x1b[38:2:4:5:6mB\x1b[38:5:100mC");
        Assert.Equal(TerminalColor.FromRgb(1, 2, 3), Cell(t, 0).Foreground);
        Assert.Equal(TerminalColor.FromRgb(4, 5, 6), Cell(t, 1).Foreground);
        Assert.Equal(TerminalColor.FromIndex(100), Cell(t, 2).Foreground);
    }

    [Fact]
    public void SgrColonColor_DoesNotLeakIntoFollowingParameters()
    {
        // With ':' folded into ';' the trailing blue component (4) used to read as "underline".
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[38:2::1:2:4mX");
        Assert.Equal(TerminalAttributes.None, Cell(t, 0).Attributes);
    }

    [Fact]
    public void SgrTruncatedExtendedColor_LeavesColorAlone()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[32m\x1b[38;5mX\x1b[48;2;1mY");
        Assert.Equal(TerminalColor.FromIndex(2), Cell(t, 0).Foreground);
        Assert.True(Cell(t, 1).Background.IsDefault);
    }

    [Fact]
    public void SgrAttributes_SetAndClearIndividually()
    {
        var t = new TerminalEmulator(30, 5);
        t.Feed("\x1b[1;2;3;4;7;8;9mA");
        Assert.Equal(
            TerminalAttributes.Bold | TerminalAttributes.Dim | TerminalAttributes.Italic |
            TerminalAttributes.Underline | TerminalAttributes.Inverse | TerminalAttributes.Hidden |
            TerminalAttributes.Strikethrough,
            Cell(t, 0).Attributes);

        t.Feed("\x1b[22;23;24;27;28;29mB");
        Assert.Equal(TerminalAttributes.None, Cell(t, 1).Attributes);

        t.Feed("\x1b[4m\x1b[4:0mC\x1b[4:3mD");
        Assert.Equal(TerminalAttributes.None, Cell(t, 2).Attributes);
        Assert.Equal(TerminalAttributes.Underline, Cell(t, 3).Attributes);
    }

    [Fact]
    public void SgrReset_ClearsAttributesAndColors()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("\x1b[1;7;31;44mA\x1b[mB");
        Assert.Equal(TerminalCell.Blank with { Char = 'B' }, Cell(t, 1));
    }

    [Fact]
    public void CursorVisibility_FollowsPrivateMode25()
    {
        var t = new TerminalEmulator(20, 5);
        Assert.True(t.CursorVisible);
        t.Feed("\x1b[?25l");
        Assert.False(t.CursorVisible);
        t.Feed("\x1b[?25h");
        Assert.True(t.CursorVisible);
        t.Feed("\x1b[?25l\x1b" + "c");
        Assert.True(t.CursorVisible);
    }

    [Fact]
    public void Scrollback_IsCappedAtTheLimit_AndShrinksWithIt()
    {
        var t = new TerminalEmulator(10, 2, scrollbackLimit: 150);
        for (var i = 0; i < 300; i++)
            t.Feed($"{i}\r\n");
        Assert.Equal(150, t.ScrollbackRows);

        t.ScrollbackLimit = 120;
        Assert.Equal(120, t.ScrollbackRows);
        // The newest rows are the ones kept.
        Assert.Equal('2', t.GetScrollbackRow(t.ScrollbackRows - 1)[0].Char);
    }

    [Fact]
    public void ScrollbackLimit_IsClamped()
    {
        Assert.Equal(TerminalEmulator.MinScrollbackLimit, new TerminalEmulator(10, 2, 1).ScrollbackLimit);
        Assert.Equal(TerminalEmulator.MaxScrollbackLimit, new TerminalEmulator(10, 2, int.MaxValue).ScrollbackLimit);
    }

    [Theory]
    [InlineData(16, 0x000000u)]
    [InlineData(196, 0xFF0000u)]
    [InlineData(21, 0x0000FFu)]
    [InlineData(231, 0xFFFFFFu)]
    [InlineData(232, 0x080808u)]
    [InlineData(244, 0x808080u)]
    [InlineData(255, 0xEEEEEEu)]
    public void Xterm256_MatchesTheStandardPalette(int index, uint rgb) =>
        Assert.Equal(rgb, TerminalPalette.Xterm256(index));

    [Fact]
    public void Theme_ResolvesEachKindOfColor()
    {
        var theme = TerminalTheme.Default;
        Assert.Equal(0x123456u, theme.Resolve(TerminalColor.Default, 0x123456));
        Assert.Equal(theme.Ansi[9], theme.Resolve(TerminalColor.FromIndex(9), 0));
        Assert.Equal(0xFF0000u, theme.Resolve(TerminalColor.FromIndex(196), 0));
        Assert.Equal(0x0A141Eu, theme.Resolve(TerminalColor.FromRgb(10, 20, 30), 0));
    }

    [Fact]
    public void Theme_InverseSwapsResolvedColors_EvenForDefaults()
    {
        var theme = TerminalTheme.Default;
        var cell = TerminalCell.Blank with { Char = 'x', Attributes = TerminalAttributes.Inverse };
        Assert.Equal((theme.Background, theme.Foreground), theme.ColorsOf(cell));
    }

    [Fact]
    public void Theme_DimBlendsTowardsBackground_HiddenMatchesIt()
    {
        var theme = TerminalTheme.Default;
        var red = TerminalColor.FromRgb(200, 0, 0);
        var black = TerminalColor.FromRgb(0, 0, 0);

        var dim = new TerminalCell('x', red, black, TerminalAttributes.Dim);
        Assert.Equal(0x640000u, theme.ColorsOf(dim).Foreground);

        var hidden = new TerminalCell('x', red, black, TerminalAttributes.Hidden);
        Assert.Equal(0x000000u, theme.ColorsOf(hidden).Foreground);
    }

    [Fact]
    public void Themes_AreCompleteAndFoundById()
    {
        Assert.All(TerminalTheme.BuiltIn, t => Assert.Equal(16, t.Ansi.Count));
        Assert.Equal(TerminalTheme.BuiltIn.Count, TerminalTheme.BuiltIn.Select(t => t.Id).Distinct().Count());
        Assert.Same(TerminalTheme.BuiltIn[2], TerminalTheme.ById(TerminalTheme.BuiltIn[2].Id.ToUpperInvariant()));
        Assert.Same(TerminalTheme.Default, TerminalTheme.ById("no-such-theme"));
        Assert.Same(TerminalTheme.Default, TerminalTheme.ById(null));
    }

    [Fact]
    public void Xterm256_RejectsThemeIndices() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalPalette.Xterm256(15));
}
