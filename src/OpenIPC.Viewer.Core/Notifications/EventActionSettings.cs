namespace OpenIPC.Viewer.Core.Notifications;

// External reactions to a camera event — the "hooks" that let the app drive
// something outside itself: a TCP/UDP datagram to an alarm panel or PLC, an HTTP
// webhook, an e-mail, or a local program/script. Persisted as one nested object
// in usersettings.json. Strings rather than enums keep that file readable and
// forward-compatible (an unknown value falls back to the default).
//
// Placeholders such as {camera} or {summary} in the payload/body/subject/
// arguments are expanded by EventActionTemplate.
public sealed record EventActionSettings(
    bool Enabled = false,
    // An alarm matters most at night, so by default actions keep firing during
    // the quiet hours that silence the on-screen notifications.
    bool IgnoreQuietHours = true,
    // Per camera, separate from the on-screen notification cooldown.
    int CooldownSeconds = 60,

    // Raw network signal: "tcp" (connect, send, close) or "udp" (one datagram).
    bool SignalEnabled = false,
    string SignalProtocol = EventActionSettings.ProtocolTcp,
    string SignalHost = "",
    int SignalPort = 0,
    string SignalPayload = "OPENIPC {kind} {camera}",

    // HTTP webhook. GET sends no body; POST/PUT send HttpBody as JSON.
    bool HttpEnabled = false,
    string HttpMethod = "POST",
    string HttpUrl = "",
    string HttpBody = EventActionSettings.DefaultHttpBody,

    // E-mail. Transport "smtp" talks to SmtpHost directly (login/password are
    // kept in the OS credential store, never in this file); "mapi" hands the
    // message to the default Windows mail client (Simple MAPI); "outlook"
    // automates an installed classic Outlook. EmailTo takes ; or , separated
    // addresses.
    bool EmailEnabled = false,
    string EmailTransport = EventActionSettings.TransportSmtp,
    string EmailTo = "",
    string EmailSubject = "{title}: {camera}",
    string EmailBody = EventActionSettings.DefaultEmailBody,
    string SmtpHost = "",
    int SmtpPort = 587,
    // "auto" (465 → TLS on connect, otherwise STARTTLS when offered), "ssl",
    // "starttls" or "none".
    string SmtpSecurity = "auto",
    string SmtpFrom = "",

    // Local program or script, started without a window and not waited for.
    bool ProgramEnabled = false,
    string ProgramPath = "",
    string ProgramArguments = "\"{camera}\" {kind} \"{summary}\"")
{
    public const string ProtocolTcp = "tcp";
    public const string ProtocolUdp = "udp";

    public const string TransportSmtp = "smtp";
    public const string TransportMapi = "mapi";
    public const string TransportOutlook = "outlook";

    public const string DefaultHttpBody =
        "{\"event\":\"{kind}\",\"camera\":\"{camera}\",\"cameraId\":\"{cameraId}\"," +
        "\"summary\":\"{summary}\",\"confidence\":\"{confidence}\",\"objects\":\"{objects}\"," +
        "\"time\":\"{timeUtc}\",\"eventId\":\"{eventId}\"}";

    public const string DefaultEmailBody =
        "{title}\n\nCamera: {camera}\nTime: {time}\nDetails: {summary}\nConfidence: {confidence}%\nObjects: {objects}\n\n— OpenIPC Viewer";

    public static EventActionSettings Default { get; } = new();

    // True when at least one channel is switched on and has enough configuration
    // to attempt a send.
    public bool AnyChannelEnabled =>
        (SignalEnabled && !string.IsNullOrWhiteSpace(SignalHost) && SignalPort > 0) ||
        (HttpEnabled && !string.IsNullOrWhiteSpace(HttpUrl)) ||
        (EmailEnabled && !string.IsNullOrWhiteSpace(EmailTo)) ||
        (ProgramEnabled && !string.IsNullOrWhiteSpace(ProgramPath));
}
