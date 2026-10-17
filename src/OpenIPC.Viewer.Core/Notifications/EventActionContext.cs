using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenIPC.Viewer.Core.Events;

namespace OpenIPC.Viewer.Core.Notifications;

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

    // Whole seconds the episode has lasted (to its end, or to now while open).
    public int DurationSeconds
    {
        get
        {
            var end = Event.EndedAt ?? NowUtc;
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

    public static string Render(string template, EventActionContext ctx, Func<string, string>? encode = null)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";

        var sb = new StringBuilder(template.Length + 64);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, open - i);
            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(template, open, template.Length - open);
                break;
            }

            var name = template.Substring(open + 1, close - open - 1);
            var value = Resolve(name, ctx);
            if (value is null)
            {
                // Not one of ours — emit the brace and keep scanning after it,
                // so "{\"a\":\"{kind}\"}" still expands the inner {kind}.
                sb.Append('{');
                i = open + 1;
                continue;
            }
            sb.Append(encode is null ? value : encode(value));
            i = close + 1;
        }
        return sb.ToString();
    }

    private static string? Resolve(string name, EventActionContext ctx)
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
