using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Security;
using MimeKit;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.Infrastructure.Notifications;

// SMTP transport for the e-mail action. Security "auto" follows the common
// convention: port 465 → TLS from the first byte, anything else → STARTTLS when
// the server offers it. The login/password come from IMailCredentialStore
// (Windows Credential Manager on Windows), never from usersettings.json.
internal static class SmtpMailSender
{
    public static async Task SendAsync(OutgoingMail mail, EventActionSettings s, MailCredential? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpHost))
            throw new InvalidOperationException("SMTP server is not set");

        var port = s.SmtpPort is > 0 and <= 65535 ? s.SmtpPort : 587;
        var options = (s.SmtpSecurity ?? "auto").Trim().ToLowerInvariant() switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ => port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
        };

        var hasLogin = credential is not null && !string.IsNullOrWhiteSpace(credential.UserName);
        var from = !string.IsNullOrWhiteSpace(s.SmtpFrom) ? s.SmtpFrom.Trim()
            : hasLogin && credential!.UserName.Contains('@') ? credential.UserName.Trim()
            : throw new InvalidOperationException("Set the sender address (From) or use an e-mail address as the login");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        foreach (var to in mail.To)
            message.To.Add(MailboxAddress.Parse(to));
        message.Subject = mail.Subject;
        message.Body = new TextPart("plain") { Text = mail.Body };

        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000 };
        await client.ConnectAsync(s.SmtpHost.Trim(), port, options, ct).ConfigureAwait(false);
        if (hasLogin)
            await client.AuthenticateAsync(credential!.UserName.Trim(), credential.Password, ct).ConfigureAwait(false);
        await client.SendAsync(message, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }
}
