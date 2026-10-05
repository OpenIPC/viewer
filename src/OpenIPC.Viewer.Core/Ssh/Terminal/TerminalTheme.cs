using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenIPC.Viewer.Core.Ssh.Terminal;

/// <summary>
/// The colors a terminal is drawn with: default text and background, the cursor, the selection
/// highlight and the 16 ANSI colors. Colors are 0xRRGGBB; <see cref="Selection"/> is 0xAARRGGBB
/// because it is laid over the text. Terminals keep their own palette rather than following the
/// app theme — the remote picks colors assuming a terminal, not our UI.
/// </summary>
public sealed class TerminalTheme
{
    public string Id { get; }
    public uint Foreground { get; }
    public uint Background { get; }
    public uint Cursor { get; }
    public uint Selection { get; }
    public IReadOnlyList<uint> Ansi { get; }

    private TerminalTheme(string id, string fg, string bg, string cursor, uint selection, string[] ansi)
    {
        if (ansi.Length != 16)
            throw new ArgumentException("A terminal theme needs exactly 16 ANSI colors.", nameof(ansi));
        Id = id;
        Foreground = Hex(fg);
        Background = Hex(bg);
        Cursor = Hex(cursor);
        Selection = selection;
        Ansi = ansi.Select(Hex).ToArray();
    }

    /// <summary>0xRRGGBB for a cell color; <paramref name="fallback"/> stands in for the default.</summary>
    public uint Resolve(TerminalColor color, uint fallback)
    {
        if (color.IsRgb)
            return color.Rgb;
        if (color.IsIndexed)
            return color.Index < 16 ? Ansi[color.Index] : TerminalPalette.Xterm256(color.Index);
        return fallback;
    }

    /// <summary>
    /// What a cell actually looks like: its colors resolved against this theme, then inverse,
    /// dim and hidden applied on top.
    /// </summary>
    public (uint Foreground, uint Background) ColorsOf(in TerminalCell cell)
    {
        var fg = Resolve(cell.Foreground, Foreground);
        var bg = Resolve(cell.Background, Background);
        var attrs = cell.Attributes;
        if ((attrs & TerminalAttributes.Inverse) != 0)
            (fg, bg) = (bg, fg);
        if ((attrs & TerminalAttributes.Dim) != 0)
            fg = Halfway(fg, bg);
        if ((attrs & TerminalAttributes.Hidden) != 0)
            fg = bg;
        return (fg, bg);
    }

    // Dim text is the foreground pulled halfway to the background — readable on any theme,
    // light or dark, which a fixed darkening would not be.
    private static uint Halfway(uint a, uint b)
    {
        uint Mix(int shift) => ((((a >> shift) & 0xFF) + ((b >> shift) & 0xFF)) / 2) << shift;
        return Mix(16) | Mix(8) | Mix(0);
    }

    private static uint Hex(string hex) =>
        uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public const string DefaultId = "default";

    public static TerminalTheme Default { get; } = new(DefaultId,
        "#d4d4d4", "#0c0f14", "#d4d4d4", 0x663B8EEA, new[]
        {
            "#1e1e1e", "#cd3131", "#0dbc79", "#e5e510", "#2472c8", "#bc3fbc", "#11a8cd", "#cccccc",
            "#666666", "#f14c4c", "#23d18b", "#f5f543", "#3b8eea", "#d670d6", "#29b8db", "#ffffff",
        });

    /// <summary>The built-in themes, in the order the settings page lists them.</summary>
    public static IReadOnlyList<TerminalTheme> BuiltIn { get; } = new[]
    {
        Default,
        new TerminalTheme("gruvbox-dark",
            "#ebdbb2", "#282828", "#ebdbb2", 0x99665C54, new[]
            {
                "#282828", "#cc241d", "#98971a", "#d79921", "#458588", "#b16286", "#689d6a", "#a89984",
                "#928374", "#fb4934", "#b8bb26", "#fabd2f", "#83a598", "#d3869b", "#8ec07c", "#ebdbb2",
            }),
        new TerminalTheme("dracula",
            "#f8f8f2", "#282a36", "#f8f8f2", 0xCC44475A, new[]
            {
                "#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2",
                "#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff",
            }),
        new TerminalTheme("solarized-dark",
            "#839496", "#002b36", "#93a1a1", 0x99586E75, Solarized),
        new TerminalTheme("solarized-light",
            "#657b83", "#fdf6e3", "#586e75", 0x6693A1A1, Solarized),
        new TerminalTheme("phosphor",
            "#33ff66", "#050a05", "#33ff66", 0x5533FF66, new[]
            {
                "#1a2a1a", "#e06c4c", "#33ff66", "#d7ff5f", "#4cb2a0", "#a8d08d", "#66ffcc", "#b8ffc8",
                "#3d5c3d", "#ff8566", "#66ff8c", "#e8ff8c", "#66d4bf", "#c4e8a8", "#99ffdd", "#e6ffe9",
            }),
    };

    // Shared by the dark and light variants, as in the original scheme. Bright black is base01
    // rather than base03 here: base03 IS the dark background, which made "bright black" text
    // (comments, `ls` for some file types) disappear.
    private static string[] Solarized => new[]
    {
        "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
        "#586e75", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3",
    };

    /// <summary>The built-in theme with this id, or <see cref="Default"/> for an unknown one.</summary>
    public static TerminalTheme ById(string? id) =>
        BuiltIn.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
}

/// <summary>How the cursor is drawn.</summary>
public enum TerminalCursorStyle
{
    /// <summary>A hollow box; the character under it stays as it is.</summary>
    Outline,
    /// <summary>A solid box with the character drawn inverted inside it.</summary>
    Block,
    /// <summary>A thin vertical bar at the left edge of the cell.</summary>
    Bar,
    /// <summary>A line along the bottom of the cell.</summary>
    Underline,
}
