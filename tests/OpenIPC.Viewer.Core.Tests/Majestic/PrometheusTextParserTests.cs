using System.Linq;
using OpenIPC.Viewer.Core.Majestic;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Majestic;

public class PrometheusTextParserTests
{
    [Fact]
    public void Parse_skips_comments_and_blanks()
    {
        const string text = "# HELP foo bar\n# TYPE foo gauge\n\nfoo 1\n";
        var samples = PrometheusTextParser.Parse(text);
        var s = Assert.Single(samples);
        Assert.Equal("foo", s.Name);
        Assert.Null(s.Labels);
        Assert.Equal(1d, s.Value);
    }

    [Fact]
    public void Parse_reads_labels_and_value()
    {
        var samples = PrometheusTextParser.Parse("http_requests_total{method=\"post\",code=\"200\"} 1027\n");
        var s = Assert.Single(samples);
        Assert.Equal("http_requests_total", s.Name);
        Assert.Equal("{method=\"post\",code=\"200\"}", s.Labels);
        Assert.Equal(1027d, s.Value);
        Assert.Equal("http_requests_total{method=\"post\",code=\"200\"}", s.Display);
    }

    [Fact]
    public void Parse_ignores_trailing_timestamp()
    {
        var samples = PrometheusTextParser.Parse("temp 42.5 1700000000000\n");
        Assert.Equal(42.5d, Assert.Single(samples).Value);
    }

    [Fact]
    public void Parse_skips_non_numeric_and_malformed()
    {
        var samples = PrometheusTextParser.Parse("ok 3\nbad notanumber\nnospace\nopen{x=\"1\" 5\n");
        Assert.Equal("ok", Assert.Single(samples).Name);
    }

    [Fact]
    public void Parse_handles_label_value_with_space()
    {
        var samples = PrometheusTextParser.Parse("build{ver=\"1 0\"} 1\n");
        var s = Assert.Single(samples);
        Assert.Equal("{ver=\"1 0\"}", s.Labels);
        Assert.Equal(1d, s.Value);
    }
}
