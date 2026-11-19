using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenIPC.Viewer.Core.Events;

namespace OpenIPC.Viewer.Core.Notifications;

// One piece of a parsed template: literal text, or (IsPlaceholder) the name
// of a known placeholder such as "camera".
public readonly record struct TemplateSegment(string Text, bool IsPlaceholder);

// Everything an external action may want to say about one event. CameraName
// falls back to the id when the camera is unknown (deleted mid-episode).
public sealed record EventActionContext(
    CameraEvent Event,
    string CameraName,
    string Title,
    DateTime NowUtc)
{
    public string KindToken => Event.Kind switch
    {
        EventKind.Detection => "detection",
        EventKind.Connection => "connection",
        EventKind.Snapshot => "snapshot",
        _ => "motion",
    };

    // Whole seconds the activity has lasted (to the episode's end, or to the
    // latest observed tick while it is still open).
    public int DurationSeconds
    {
        get
        {
            var end = Event.EndedAt ?? Event.LastSeenAt ?? NowUtc;
            var seconds = (end.ToUniversalTime() - Event.OccurredAt.ToUniversalTime()).TotalSeconds;
            return seconds > 0 ? (int)seconds : 0;
        }
    }

    // A ready-made context for the Settings "Test" buttons.
    public static EventActionContext Sample(DateTime nowUtc) => new(
        new CameraEvent(
            EventId.New(), new Entities.CameraId(Guid.Empty), EventKind.Detection, EventSeverity.Info,
            nowUtc.AddSeconds(-3), null, "test", "person ×1", 0.9f, 1),
        "Test camera", "Test event", nowUtc);
}

// Expands {placeholders} in a user template. Only the known names below are
// touched, so literal braces (e.g. a JSON body) pass through unchanged. encode,
// when given, is applied to every substituted value — the HTTP channel passes a
// JSON string escaper so a camera called `Gate "A"` can't break the body.
public static class EventActionTemplate
{
    public static readonly IReadOnlyList<string> Placeholders = new[]
    {
        "title", "camera", "cameraId", "kind", "summary", "confidence",
        "objects", "duration", "time", "timeUtc", "eventId",
    };

    private static readonly HashSet<string> Known = new(Placeholders, StringComparer.Ordinal);

    public static string Render(string? template, EventActionContext ctx, Func<string, string>? encode = null)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var sb = new StringBuilder(template!.Length + 64);
        foreach (var seg in Parse(template))
        {
            if (!seg.IsPlaceholder)
            {
                sb.Append(seg.Text);
                continue;
            }
            var value = Resolve(seg.Text, ctx);
            sb.Append(encode is null ? value : encode(value));
        }
        return sb.ToString();
    }

    // Splits a template into literal text and known {placeholders} (the
    // segment's Text is then the placeholder name), so callers can treat the
    // template's own text and the substituted event data differently — e.g.
    // decode escapes only in the former. Unknown braces stay literal, so
    // "{\"a\":\"{kind}\"}" still yields the inner {kind}.
    public static IReadOnlyList<TemplateSegment> Parse(string? template)
    {
        var segments = new List<TemplateSegment>();
        if (string.IsNullOrEmpty(template)) return segments;

        var literal = new StringBuilder();
        var i = 0;
        while (i < template!.Length)
        {
            var open = template.IndexOf('{', i);
            var close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                literal.Append(template, i, template.Length - i);
                break;
            }

            literal.Append(template, i, open - i);
            var name = template.Substring(open + 1, close - open - 1);
            if (!Known.Contains(name))
            {
                literal.Append('{');
                i = open + 1;
                continue;
            }

            if (literal.Length > 0)
            {
                segments.Add(new TemplateSegment(literal.ToString(), false));
                literal.Clear();
            }
            segments.Add(new TemplateSegment(name, true));
            i = close + 1;
        }
        if (literal.Length > 0)
            segments.Add(new TemplateSegment(literal.ToString(), false));
        return segments;
    }

    public static bool HasPlaceholders(string? template)
    {
        foreach (var seg in Parse(template))
            if (seg.IsPlaceholder) return true;
        return false;
    }

    // Splits a program-argument template into arguments BEFORE placeholders are
    // expanded: whitespace separates, "double quotes" group (and are dropped).
    // Each argument is then rendered on its own and passed through
    // ProcessStartInfo.ArgumentList, so a value with quotes or spaces in it can
    // never turn into extra arguments.
    public static IReadOnlyList<string> SplitArguments(string? template)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(template)) return args;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var ch in template!)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true; // "" is a real (empty) argument
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (hasToken) args.Add(current.ToString());
                current.Clear();
                hasToken = false;
                continue;
            }
            current.Append(ch);
            hasToken = true;
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }

    // Event data is plain text: control characters (line breaks, NUL…) are
    // dropped before it goes into a signal payload or a program argument.
    public static string StripControlChars(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (!char.IsControl(ch)) sb.Append(ch);
        return sb.ToString();
    }

    // For .bat/.cmd targets: cmd.exe re-parses the whole command line — it
    // expands %VAR% even inside quotes and ends quoting at the next ", after
    // which & | < > ^ run commands. Those characters are dropped from event data.
    public static string StripCmdMetacharacters(string value)
    {
        var clean = StripControlChars(value);
        var sb = new StringBuilder(clean.Length);
        foreach (var ch in clean)
            if (ch is not ('"' or '%' or '&' or '|' or '<' or '>' or '^' or '!')) sb.Append(ch);
        return sb.ToString();
    }

    public static string Resolve(string name, EventActionContext ctx) =>
        ResolveKnown(name, ctx) ?? "";

    private static string? ResolveKnown(string name, EventActionContext ctx)
    {
        var e = ctx.Event;
        switch (name)
        {
            case "title": return ctx.Title;
            case "camera": return ctx.CameraName;
            case "cameraId": return e.CameraId.ToString();
            case "kind": return ctx.KindToken;
            case "summary": return e.Summary ?? "";
            case "confidence":
                return e.PeakConfidence is { } c
                    ? ((int)Math.Round(c * 100)).ToString(CultureInfo.InvariantCulture)
                    : "";
            case "objects": return e.PeakObjects.ToString(CultureInfo.InvariantCulture);
            case "duration": return ctx.DurationSeconds.ToString(CultureInfo.InvariantCulture);
            case "time":
                return e.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case "timeUtc":
                return e.OccurredAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            case "eventId": return e.Id.ToString();
            default: return null;
        }
    }

    // Splits "a@x.ru; b@y.com, c@z.org" into trimmed, non-empty addresses.
    public static IReadOnlyList<string> SplitAddresses(string list)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(list)) return result;
        foreach (var part in list.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var a = part.Trim();
            if (a.Length > 0) result.Add(a);
        }
        return result;
    }

    // Minimal JSON string-content escaper for the webhook body.
    public static string JsonEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
