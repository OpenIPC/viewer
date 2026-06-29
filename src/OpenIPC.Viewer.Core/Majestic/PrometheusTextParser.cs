using System.Collections.Generic;
using System.Globalization;

namespace OpenIPC.Viewer.Core.Majestic;

// Minimal Prometheus text-exposition parser — enough to render a camera's
// /metrics endpoint read-only. We don't aggregate, type-check, or honour
// timestamps; each non-comment line becomes one sample. Unparseable value
// fields are skipped rather than throwing (firmware metrics output drifts).
//
// Line shape: `metric_name{label="v",…} <value> [timestamp]`
// Comment/blank lines (# HELP, # TYPE, empty) are ignored.
public static class PrometheusTextParser
{
    public static IReadOnlyList<MajesticMetricSample> Parse(string text)
    {
        var result = new List<MajesticMetricSample>();
        if (string.IsNullOrEmpty(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // Split metric (name + optional {labels}) from the trailing
            // "value [timestamp]". The metric token can't contain spaces, but
            // a label value could — so cut at the brace boundary first.
            string nameAndLabels;
            string remainder;
            var brace = line.IndexOf('{');
            if (brace >= 0)
            {
                var close = line.IndexOf('}', brace);
                if (close < 0) continue; // malformed
                nameAndLabels = line.Substring(0, close + 1);
                remainder = line.Substring(close + 1).Trim();
            }
            else
            {
                var sp = line.IndexOf(' ');
                if (sp < 0) continue;
                nameAndLabels = line.Substring(0, sp);
                remainder = line.Substring(sp + 1).Trim();
            }

            // remainder = "value [timestamp]" — value is the first token.
            var valueToken = remainder;
            var space = remainder.IndexOf(' ');
            if (space >= 0) valueToken = remainder.Substring(0, space);
            if (!double.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            string name;
            string? labels = null;
            var lb = nameAndLabels.IndexOf('{');
            if (lb >= 0)
            {
                name = nameAndLabels.Substring(0, lb);
                labels = nameAndLabels.Substring(lb);
            }
            else
            {
                name = nameAndLabels;
            }

            if (name.Length == 0) continue;
            result.Add(new MajesticMetricSample(name, labels, value));
        }

        return result;
    }
}
