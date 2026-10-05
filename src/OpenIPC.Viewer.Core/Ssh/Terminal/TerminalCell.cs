using System;

namespace OpenIPC.Viewer.Core.Ssh.Terminal;

/// <summary>One character cell in the terminal grid.</summary>
public readonly record struct TerminalCell(
    char Char, TerminalColor Foreground, TerminalColor Background, TerminalAttributes Attributes)
{
    public static readonly TerminalCell Blank =
        new(' ', TerminalColor.Default, TerminalColor.Default, TerminalAttributes.None);

    public bool Bold => (Attributes & TerminalAttributes.Bold) != 0;
}

/// <summary>SGR rendition flags carried by a <see cref="TerminalCell"/>.</summary>
[Flags]
public enum TerminalAttributes : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,
    Strikethrough = 64,
}

/// <summary>
/// A cell color as the remote asked for it: the theme default, a palette index (0–15 are the
/// theme's ANSI colors, 16–255 the fixed xterm cube and gray ramp), or a 24-bit RGB value.
/// Turning it into a pixel is the theme's job (<see cref="TerminalTheme.Resolve"/>), so a
/// screen keeps its meaning when the user switches themes.
/// </summary>
public readonly record struct TerminalColor
{
    private const uint KindMask = 0xFF00_0000;
    private const uint IndexedKind = 0x0100_0000;
    private const uint RgbKind = 0x0200_0000;

    private readonly uint _value;

    private TerminalColor(uint value) => _value = value;

    public static TerminalColor Default => default;

    public static TerminalColor FromIndex(int index) => new(IndexedKind | (uint)(index & 0xFF));

    public static TerminalColor FromRgb(int r, int g, int b) =>
        new(RgbKind | (Channel(r) << 16) | (Channel(g) << 8) | Channel(b));

    public bool IsDefault => _value == 0;
    public bool IsIndexed => (_value & KindMask) == IndexedKind;
    public bool IsRgb => (_value & KindMask) == RgbKind;

    /// <summary>Palette index; meaningful when <see cref="IsIndexed"/>.</summary>
    public int Index => (int)(_value & 0xFF);

    /// <summary>0xRRGGBB; meaningful when <see cref="IsRgb"/>.</summary>
    public uint Rgb => _value & 0x00FF_FFFF;

    private static uint Channel(int v) => (uint)Math.Clamp(v, 0, 255);
}

/// <summary>The fixed part of the xterm 256-color palette.</summary>
public static class TerminalPalette
{
    private static readonly int[] CubeSteps = { 0, 95, 135, 175, 215, 255 };

    /// <summary>
    /// 0xRRGGBB for palette indices 16–255: a 6×6×6 color cube followed by a 24-step gray ramp.
    /// Indices 0–15 are theme colors and are not answered here.
    /// </summary>
    public static uint Xterm256(int index)
    {
        if (index is < 16 or > 255)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (index >= 232)
        {
            var gray = (uint)(8 + (index - 232) * 10);
            return (gray << 16) | (gray << 8) | gray;
        }

        var cube = index - 16;
        var r = (uint)CubeSteps[cube / 36];
        var g = (uint)CubeSteps[cube / 6 % 6];
        var b = (uint)CubeSteps[cube % 6];
        return (r << 16) | (g << 8) | b;
    }
}
