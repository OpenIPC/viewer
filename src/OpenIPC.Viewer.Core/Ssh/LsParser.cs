using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// Parses busybox <c>ls -la</c> output into <see cref="RemoteEntry"/> rows.
/// Tolerant by design — OpenIPC firmwares ship different busybox builds, so a
/// line that doesn't match the expected layout is skipped, not fatal
/// (phase-13 §13.6, risk "разнобой busybox").
/// </summary>
public static class LsParser
{
    private static readonly string[] Months =
    {
        "jan", "feb", "mar", "apr", "may", "jun",
        "jul", "aug", "sep", "oct", "nov", "dec",
    };

    /// <summary>
    /// Parses listing text. The <c>.</c> and <c>..</c> pseudo-entries and the
    /// leading <c>total N</c> line are filtered out.
    /// </summary>
    public static IReadOnlyList<RemoteEntry> Parse(string output)
    {
        var entries = new List<RemoteEntry>();
        if (string.IsNullOrEmpty(output))
            return entries;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
                continue;

            var entry = ParseLine(line);
            if (entry is not null && entry.Name is not ("." or ".."))
                entries.Add(entry);
        }

        return entries;
    }

    private static RemoteEntry? ParseLine(string line)
    {
        // perms links owner group size  mon day time|year  name[ -> target]
        var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 9)
            return null;

        var perms = tokens[0];
        var kind = perms[0] switch
        {
            'd' => RemoteEntryKind.Directory,
            'l' => RemoteEntryKind.SymbolicLink,
            '-' => RemoteEntryKind.File,
            _ => RemoteEntryKind.Other,
        };

        _ = long.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        var modified = TryParseDate(tokens[5], tokens[6], tokens[7]);

        // Name is everything after the 8 fixed columns; rejoin so names with
        // spaces survive. Symlinks render as "name -> target" — keep the name.
        var name = string.Join(' ', tokens, 8, tokens.Length - 8);
        if (kind == RemoteEntryKind.SymbolicLink)
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                name = name[..arrow];
        }

        if (name.Length == 0)
            return null;

        return new RemoteEntry(name, kind, size, modified, perms);
    }

    // "Jan 1 2020" -> a date; "Jan 1 14:30" omits the year (recent file) — we
    // don't guess it, so Modified stays null rather than reporting a wrong year.
    private static DateTimeOffset? TryParseDate(string month, string day, string yearOrTime)
    {
        var m = Array.IndexOf(Months, month.ToLowerInvariant()) + 1;
        if (m == 0 || !int.TryParse(day, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            return null;

        if (yearOrTime.Contains(':'))
            return null;

        if (!int.TryParse(yearOrTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            return null;

        try
        {
            return new DateTimeOffset(year, m, d, 0, 0, 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
