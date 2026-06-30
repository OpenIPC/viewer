using System.Collections.Generic;
using System.Linq;
using OpenIPC.Viewer.Core.Majestic;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Majestic;

public class MajesticConfigModelTests
{
    private static MajesticConfigModel Model(params MajesticConfigField[] fields) =>
        new("{}", fields);

    [Fact]
    public void ComputeEdits_returns_only_changed_fields()
    {
        var model = Model(
            new MajesticConfigField("video0", "codec", MajesticFieldKind.String, "h264"),
            new MajesticConfigField("video0", "fps", MajesticFieldKind.Int, "30"),
            new MajesticConfigField("isp", "drc", MajesticFieldKind.Int, "350"));

        var edits = model.ComputeEdits(new Dictionary<string, string>
        {
            ["video0.codec"] = "h265",   // changed
            ["video0.fps"] = "30",       // unchanged
            ["isp.drc"] = "350",          // unchanged
        });

        var edit = Assert.Single(edits);
        Assert.Equal("video0", edit.Section);
        Assert.Equal("codec", edit.Key);
        Assert.Equal("h265", edit.Value);
    }

    [Fact]
    public void ComputeEdits_ignores_unknown_paths()
    {
        var model = Model(new MajesticConfigField("video0", "fps", MajesticFieldKind.Int, "30"));

        var edits = model.ComputeEdits(new Dictionary<string, string>
        {
            ["video0.bogus"] = "999",
        });

        Assert.Empty(edits);
    }

    [Theory]
    [InlineData(MajesticFieldKind.Int, "30", "30.0")]   // numeric equality, not string
    [InlineData(MajesticFieldKind.Bool, "true", "True")] // case-insensitive
    [InlineData(MajesticFieldKind.Number, "1.5", "1.50")]
    public void ComputeEdits_treats_equivalent_values_as_unchanged(MajesticFieldKind kind, string original, string edited)
    {
        var model = Model(new MajesticConfigField("s", "k", kind, original));

        var edits = model.ComputeEdits(new Dictionary<string, string> { ["s.k"] = edited });

        Assert.Empty(edits);
    }

    [Fact]
    public void ComputeEdits_trims_whitespace_on_stored_value()
    {
        var model = Model(new MajesticConfigField("rtmp", "url", MajesticFieldKind.String, "old"));

        var edits = model.ComputeEdits(new Dictionary<string, string> { ["rtmp.url"] = "  new  " });

        Assert.Equal("new", Assert.Single(edits).Value);
    }

    [Theory]
    [InlineData(MajesticFieldKind.Enum, "h264", "h264", true)]
    [InlineData(MajesticFieldKind.Enum, "h264", "h265", false)]
    [InlineData(MajesticFieldKind.Int, "30", " 30 ", true)]
    public void ValuesEqual_is_kind_aware(MajesticFieldKind kind, string a, string b, bool equal)
    {
        Assert.Equal(equal, MajesticConfigModel.ValuesEqual(kind, a, b));
    }

    [Fact]
    public void Sections_group_fields_in_first_seen_order()
    {
        var model = Model(
            new MajesticConfigField("system", "logLevel", MajesticFieldKind.String, "info"),
            new MajesticConfigField("video0", "fps", MajesticFieldKind.Int, "30"),
            new MajesticConfigField("video0", "codec", MajesticFieldKind.String, "h264"));

        var sections = model.Sections;

        Assert.Equal(new[] { "system", "video0" }, sections.Select(s => s.Name).ToArray());
        Assert.Equal(2, sections.First(s => s.Name == "video0").Fields.Count);
    }
}
