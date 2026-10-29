using OpenIPC.Viewer.Core.Updates;

namespace OpenIPC.Viewer.Core.Tests.Updates;

public sealed class AppVersionTests
{
    private static AppVersion V(string s)
    {
        Assert.True(AppVersion.TryParse(s, out var v), s);
        return v;
    }

    [Theory]
    [InlineData("0.3.19", "0.3.19")]
    [InlineData("v0.3.10-rc2", "0.3.10-rc2")]
    [InlineData("0.3.17-vendor1", "0.3.17-vendor1")]
    [InlineData("0.3.19-4-gabc1234", "0.3.19")]
    [InlineData("0.3.17-vendor1-5-gabc1234-dirty", "0.3.17-vendor1")]
    [InlineData("0.3.10-rc.2", "0.3.10-rc2")]
    public void Parses_tags_and_git_describe(string text, string expected) =>
        Assert.Equal(expected, V(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("abc1234")]
    [InlineData("1.2")]
    [InlineData("0.3.19-dirty-x")]
    public void Rejects_what_is_not_a_release(string text) =>
        Assert.False(AppVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.3.18", "0.3.19")]
    [InlineData("0.3.9", "0.3.10")]
    [InlineData("0.3.10-rc1", "0.3.10-rc2")]
    [InlineData("0.3.10-rc2", "0.3.10")]
    [InlineData("0.3.10-beta1", "0.3.10-rc1")]
    [InlineData("0.3.17", "0.3.17-vendor1")]
    [InlineData("0.3.17-vendor1", "0.3.17-vendor2")]
    [InlineData("0.3.17-vendor2", "0.3.18-vendor1")]
    [InlineData("0.3.19", "0.3.19-3-gabc1234")]
    public void Orders_older_before_newer(string older, string newer)
    {
        Assert.True(V(older) < V(newer));
        Assert.True(V(newer) > V(older));
    }

    [Fact]
    public void A_dev_build_is_not_offered_its_own_tag() =>
        Assert.False(V("0.3.19") > V("0.3.19-12-g0123abc-dirty"));

    [Fact]
    public void Equal_versions_compare_equal() =>
        Assert.True(V("v0.3.19") == V("0.3.19"));
}
