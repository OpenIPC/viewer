using OpenIPC.Viewer.Core.Ssh.Terminal;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Ssh;

// Paste shaping: a pasted line break is Enter to a shell, so the trailing one is dropped and
// multi-line pastes are either joined, run deliberately, or bracketed.
public sealed class TerminalPasteTests
{
    [Theory]
    [InlineData("ls -la\n", "ls -la")]
    [InlineData("ls -la\r\n", "ls -la")]
    [InlineData("ls -la \n\n  \n", "ls -la")]
    [InlineData("a\r\nb\rc", "a\nb\nc")]
    [InlineData("rm\x1b[201~ -rf", "rm[201~ -rf")]
    public void Normalize_FoldsBreaksTrimsTailAndDropsControls(string input, string expected) =>
        Assert.Equal(expected, TerminalPaste.Normalize(input));

    [Fact]
    public void SingleLineWithTrailingBreak_IsNotMultiline()
    {
        var paste = TerminalPaste.Normalize("reboot\n");
        Assert.False(TerminalPaste.IsMultiline(paste));
        Assert.Equal(1, TerminalPaste.LineCount(paste));
    }

    [Fact]
    public void AsOneLine_JoinsTrimsAndDropsContinuationBackslash()
    {
        var paste = TerminalPaste.Normalize("curl -s \\n  http://cam/x\n\n  | head\n");
        Assert.Equal(4, TerminalPaste.LineCount(paste));
        Assert.Equal("curl -s http://cam/x | head", TerminalPaste.AsOneLine(paste));
    }

    [Fact]
    public void AsCommands_EndsEveryLineWithEnter() =>
        Assert.Equal("a\rb\r", TerminalPaste.AsCommands("a\nb"));

    [Fact]
    public void Bracketed_WrapsAndKeepsBreaksAsCr() =>
        Assert.Equal("\x1b[200~a\rb\x1b[201~", TerminalPaste.Bracketed("a\nb"));
}
