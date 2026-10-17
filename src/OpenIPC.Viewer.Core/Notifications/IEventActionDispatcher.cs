using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Notifications;

public enum EventActionChannel
{
    Signal,
    Http,
    Email,
    Program,
}

// Runs the external actions configured in EventActionSettings. The coordinator
// decides WHEN (thresholds, cooldown, quiet hours); this decides HOW. The
// network/mail/process implementation lives in Infrastructure.
public interface IEventActionDispatcher
{
    // Runs every enabled channel concurrently. A failing channel is logged and
    // does not stop the others; never throws.
    Task DispatchAsync(EventActionContext context, EventActionSettings settings, CancellationToken ct);

    // Runs a single channel regardless of its Enabled flag — the Settings "Test"
    // button. Throws on failure so the UI can show the reason.
    Task TestAsync(EventActionChannel channel, EventActionContext context, EventActionSettings settings, CancellationToken ct);
}

// Login/password for the SMTP transport. Kept out of usersettings.json: on
// Windows it lives in Credential Manager (editable there), elsewhere in the
// platform secrets store.
public sealed record MailCredential(string UserName, string Password);

public interface IMailCredentialStore
{
    // Human-readable name of where the secret is kept, for the Settings page.
    string StorageName { get; }

    Task<MailCredential?> GetAsync(CancellationToken ct);
    Task SetAsync(MailCredential credential, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}
