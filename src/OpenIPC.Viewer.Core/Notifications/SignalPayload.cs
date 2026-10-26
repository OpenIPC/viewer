using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenIPC.Viewer.Core.Notifications;

// Turns the (already rendered) signal payload into bytes for the TCP/UDP
// channel. Two forms, so both text protocols and binary alarm panels / PLCs
// can be driven:
//   "hex: 01 A0 FF"      — raw bytes; spaces, ':' and '-' between pairs are ignored;
//   anything else        — UTF-8 text with C-style escapes \r \n \t \0 \\ \xNN.
public static class SignalPayload
{
    public const string HexPrefix = "hex:";

    // Builds the payload from the user's TEMPLATE: escapes are decoded in the
    // template's own text only, and substituted event data goes in as plain
    // UTF-8 with control characters removed — a camera named "Gate\r\nDISARM"
    // or one containing \x00 can't inject bytes into a panel or PLC stream.
    // Placeholders are refused in hex mode (their values aren't hex).
    public static byte[] Build(string? template, EventActionContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return Array.Empty<byte>();
        if (template!.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (EventActionTemplate.HasPlaceholders(template))
                throw new FormatException("Placeholders can't be used in a hex: payload");
            return ParseHex(template.Substring(HexPrefix.Length));
        }

        var output = new MemoryStream(template.Length + 32);
        foreach (var seg in EventActionTemplate.Parse(template))
        {
            var bytes = seg.IsPlaceholder
                ? Encoding.UTF8.GetBytes(EventActionTemplate.StripControlChars(EventActionTemplate.Resolve(seg.Text, ctx)))
                : Unescape(seg.Text);
            output.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    // A literal payload (no placeholders) to bytes.
    public static byte[] Encode(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return Array.Empty<byte>();
        return payload.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase)
            ? ParseHex(payload.Substring(HexPrefix.Length))
            : Unescape(payload);
    }

    private static byte[] ParseHex(string text)
    {
        var digits = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == ':' || ch == '-' || ch == ',') continue;
            if (!Uri.IsHexDigit(ch))
                throw new FormatException($"'{ch}' is not a hex digit in the signal payload");
            digits.Append(ch);
        }
        if (digits.Length % 2 != 0)
            throw new FormatException("Hex signal payload has an odd number of digits");

        var bytes = new byte[digits.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(digits.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    private static byte[] Unescape(string text)
    {
        var output = new MemoryStream(text.Length);
        var pending = new StringBuilder();

        void FlushText()
        {
            if (pending.Length == 0) return;
            var b = Encoding.UTF8.GetBytes(pending.ToString());
            output.Write(b, 0, b.Length);
            pending.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\\' || i + 1 >= text.Length)
            {
                pending.Append(ch);
                continue;
            }

            var next = text[++i];
            switch (next)
            {
                case 'r': pending.Append('\r'); break;
                case 'n': pending.Append('\n'); break;
                case 't': pending.Append('\t'); break;
                case '0': pending.Append('\0'); break;
                case '\\': pending.Append('\\'); break;
                case 'x' when i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]):
                    // A raw byte, not a character — keep it out of the UTF-8 encoder.
                    FlushText();
                    output.WriteByte(byte.Parse(text.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default:
                    pending.Append('\\').Append(next);
                    break;
            }
        }
        FlushText();
        return output.ToArray();
    }
}
