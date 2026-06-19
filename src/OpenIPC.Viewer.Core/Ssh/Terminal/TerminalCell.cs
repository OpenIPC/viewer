namespace OpenIPC.Viewer.Core.Ssh.Terminal;

/// <summary>One character cell in the terminal grid.</summary>
public readonly record struct TerminalCell(char Char, byte Foreground, byte Background, bool Bold)
{
    public static readonly TerminalCell Blank =
        new(' ', TerminalPalette.DefaultForeground, TerminalPalette.DefaultBackground, false);
}

/// <summary>
/// Color indices used by <see cref="TerminalCell"/>. 0–7 are the standard ANSI
/// colors, 8–15 their bright variants; two sentinels mark "use the theme
/// default". The renderer maps these to brushes.
/// </summary>
public static class TerminalPalette
{
    public const byte DefaultForeground = 0xFF;
    public const byte DefaultBackground = 0xFE;
}
