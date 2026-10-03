using System.Collections.Generic;
using System.Text;

namespace OpenIPC.Viewer.Core.Ssh.Terminal;

/// <summary>
/// Turns clipboard text into the bytes a paste sends down the PTY.
/// </summary>
/// <remarks>
/// A shell cannot tell a pasted line break from the user pressing Enter, so a naive paste of
/// copied lines runs every one of them the moment it lands. Two things keep that from happening:
/// a trailing line break is never sent (copying a line almost always picks up its end), and a
/// paste that still spans several lines is offered back to the user as a choice — run the lines,
/// or join them into one to edit first. When the remote shell has turned on bracketed paste
/// (CSI ?2004h) neither is needed: the shell itself knows the text was pasted and only inserts it.
/// </remarks>
public static class TerminalPaste
{
    private const string BracketStart = "\x1b[200~";
    private const string BracketEnd = "\x1b[201~";

    /// <summary>
    /// Clipboard text with every line-break spelling folded into '\n', control codes dropped and
    /// the trailing line break (and blank tail) trimmed off.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                // CRLF is one break, a lone CR (old Mac, some web copies) is one too.
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                sb.Append('\n');
            }
            else if (c == '\n' || c == '\t' || (c >= ' ' && c != '\x7f'))
            {
                sb.Append(c);
            }
            // Anything else is a control code — ESC above all. Pasted, it would act on the line
            // editor (or end a bracketed paste early) instead of being text.
        }

        var end = sb.Length;
        while (end > 0 && sb[end - 1] is '\n' or ' ' or '\t')
            end--;
        sb.Length = end;
        return sb.ToString();
    }

    /// <summary>True when <paramref name="normalized"/> still breaks a line somewhere.</summary>
    public static bool IsMultiline(string normalized) => normalized.IndexOf('\n') >= 0;

    /// <summary>How many lines a normalized paste carries.</summary>
    public static int LineCount(string normalized)
    {
        var count = 1;
        foreach (var c in normalized)
        {
            if (c == '\n')
                count++;
        }
        return count;
    }

    /// <summary>
    /// The lines joined into one command line, for the user to edit before running: blank lines
    /// dropped, indentation trimmed, and a shell continuation backslash at a line end removed
    /// rather than left to escape the space that replaces the break.
    /// </summary>
    public static string AsOneLine(string normalized)
    {
        var parts = new List<string>();
        foreach (var raw in normalized.Split('\n'))
        {
            var line = raw.Trim();
            if (line.EndsWith("\\", System.StringComparison.Ordinal))
                line = line.Substring(0, line.Length - 1).TrimEnd();
            if (line.Length > 0)
                parts.Add(line);
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// What to send for a paste the user wants run line by line: every line ends in Enter, the
    /// last one included — that is what "run" means.
    /// </summary>
    public static string AsCommands(string normalized) => normalized.Replace('\n', '\r') + "\r";

    /// <summary>
    /// The paste wrapped for a shell that asked for bracketed paste. Line breaks stay inside the
    /// brackets as CR, the way xterm sends them; the shell inserts them without running anything.
    /// </summary>
    public static string Bracketed(string normalized) =>
        BracketStart + normalized.Replace('\n', '\r') + BracketEnd;
}
