using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Security;
using MimeKit;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.Infrastructure.Notifications;

// SMTP transport for the e-mail action. Security "auto" follows the common
// convention: port 465 → TLS from the first byte, anything else → STARTTLS —
// mandatory when a login is set, opportunistic only for anonymous relays. The
// login/password come from IMailCredentialStore (Windows Credential Manager on
// Windows), never from usersettings.json.
internal static class SmtpMailSender
{
    public static async Task SendAsync(OutgoingMail mail, EventActionSettings s, MailCredential? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpHost))
            throw new InvalidOperationException("SMTP server is not set");

        var port = s.SmtpPort is > 0 and <= 65535 ? s.SmtpPort : 587;
        var hasLogin = credential is not null && !string.IsNullOrWhiteSpace(credential.UserName);
        var mode = (s.SmtpSecurity ?? "auto").Trim().ToLowerInvariant();
        var options = mode switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            // With a login configured STARTTLS is REQUIRED: someone on the path
            // could strip an opportunistic offer and read the password.
            _ => port == 465 ? SecureSocketOptions.SslOnConnect
                : hasLogin ? SecureSocketOptions.StartTls
                : SecureSocketOptions.StartTlsWhenAvailable,
        };
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
        {
            // Only an explicit "none" may send the password over a plain connection.
            if (!client.IsSecure && mode != "none")
                throw new InvalidOperationException(
                    "The SMTP connection is not encrypted, so the password was not sent. Use SSL/TLS or STARTTLS.");
            await client.AuthenticateAsync(credential!.UserName.Trim(), credential.Password, ct).ConfigureAwait(false);
        }
        await client.SendAsync(message, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }
}
