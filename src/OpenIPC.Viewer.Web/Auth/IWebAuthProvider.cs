using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Web.Auth;

// The authentication seam. Returns the identity for valid credentials, or null
// to reject. The public build ships PasswordAuthProvider (single admin); the
// private .ovac RBAC plugs in here as another implementation over the real
// user/role store — no endpoint or session-store changes.
public interface IWebAuthProvider
{
    ValueTask<WebIdentity?> ValidateCredentialsAsync(string user, string password, CancellationToken ct);
}
