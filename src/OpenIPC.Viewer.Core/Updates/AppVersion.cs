using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenIPC.Viewer.Core.Updates;

/// <summary>
/// A release version as the build and the feeds spell it: <c>0.3.19</c>, <c>v0.3.10-rc2</c>,
/// <c>0.3.17-vendor1</c>, or a git-describe build after a tag (<c>0.3.19-4-gabc1234-dirty</c>).
/// </summary>
/// <remarks>
/// Ordering: the numeric core first, then the label. alpha/beta/preview/rc are pre-releases and
/// sort before the plain version; any other label (vendor) is a build on top of that version and
/// sorts after it. A git-describe build sorts after the tag it was made from, so a dev build is
/// never offered the release it already contains.
/// </remarks>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)" +
        @"(?:-(?<label>[A-Za-z]+)\.?(?<n>\d*))?" +
        @"(?:-(?<ahead>\d+)-g[0-9a-f]+)?" +
        @"(?:-dirty)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Lower-cased label without its number ("rc", "vendor"), or empty.</summary>
    public string Label { get; }
    public int LabelNumber { get; }

    /// <summary>Commits on top of the tag in a git-describe version; 0 for a tagged build.</summary>
    public int CommitsAhead { get; }

    private AppVersion(int major, int minor, int patch, string label, int labelNumber, int commitsAhead)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Label = label;
        LabelNumber = labelNumber;
        CommitsAhead = commitsAhead;
    }

    public bool IsPreRelease => LabelRank(Label) < 0;

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern.Match(text!.Trim());
        if (!m.Success) return false;

        version = new AppVersion(
            Int(m.Groups["major"].Value),
            Int(m.Groups["minor"].Value),
            Int(m.Groups["patch"].Value),
            m.Groups["label"].Value.ToLowerInvariant(),
            m.Groups["n"].Value.Length == 0 ? 0 : Int(m.Groups["n"].Value),
            m.Groups["ahead"].Success ? Int(m.Groups["ahead"].Value) : 0);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c == 0) c = LabelRank(Label).CompareTo(LabelRank(other.Label));
        if (c == 0) c = string.CompareOrdinal(Label, other.Label);
        if (c == 0) c = LabelNumber.CompareTo(other.LabelNumber);
        if (c == 0) c = CommitsAhead.CompareTo(other.CommitsAhead);
        return c;
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is AppVersion v && Equals(v);
    public override int GetHashCode() =>
        ((Major * 397 ^ Minor) * 397 ^ Patch) * 397 ^ StringComparer.Ordinal.GetHashCode(Label) ^ LabelNumber ^ CommitsAhead;

    public static bool operator ==(AppVersion? a, AppVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(AppVersion? a, AppVersion? b) => !(a == b);
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    /// <summary>The release as a tag would name it, without the git-describe tail.</summary>
    public override string ToString() =>
        Label.Length == 0
            ? FormattableString.Invariant($"{Major}.{Minor}.{Patch}")
            : FormattableString.Invariant($"{Major}.{Minor}.{Patch}-{Label}{(LabelNumber > 0 ? LabelNumber.ToString(CultureInfo.InvariantCulture) : "")}");

    private static int LabelRank(string label) => label switch
    {
        "alpha" => -4,
        "beta" => -3,
        "preview" => -2,
        "rc" => -1,
        "" => 0,
        _ => 1,
    };

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
}
