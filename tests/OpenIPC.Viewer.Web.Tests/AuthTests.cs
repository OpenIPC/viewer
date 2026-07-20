using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Web.Auth;

namespace OpenIPC.Viewer.Web.Tests;

// Auth primitives: credential validation (PBKDF2 via the provider) and the
// opaque-token session store.
public sealed class AuthTests
{
    private static WebIdentity Admin() =>
        new("admin", new HashSet<string>(StringComparer.Ordinal) { WebRoles.Admin });

    [Fact]
    public async Task Provider_AcceptsCorrectCredentials_RejectsWrong()
    {
        var provider = new PasswordAuthProvider(
            new WebAuthOptions { AdminUser = "admin", AdminPassword = "hunter2" },
            NullLogger<PasswordAuthProvider>.Instance);

        Assert.NotNull(await provider.ValidateCredentialsAsync("admin", "hunter2", CancellationToken.None));
        Assert.Null(await provider.ValidateCredentialsAsync("admin", "wrong", CancellationToken.None));
        Assert.Null(await provider.ValidateCredentialsAsync("root", "hunter2", CancellationToken.None));
    }

    [Fact]
    public void Session_CreateThenValidate_RoundTrips()
    {
        var store = new SessionStore(new WebAuthOptions());
        var (token, _) = store.Create(Admin());

        var identity = store.Validate(token);
        Assert.NotNull(identity);
        Assert.Equal("admin", identity!.Name);
        Assert.True(identity.IsAdmin);
    }

    [Fact]
    public void Session_UnknownToken_IsRejected()
    {
        var store = new SessionStore(new WebAuthOptions());
        Assert.Null(store.Validate("not-a-real-token"));
        Assert.Null(store.Validate(""));
    }

    [Fact]
    public void Session_Revoke_InvalidatesToken()
    {
        var store = new SessionStore(new WebAuthOptions());
        var (token, _) = store.Create(Admin());

        Assert.True(store.Revoke(token));
        Assert.Null(store.Validate(token));
    }

    [Fact]
    public void Session_RevokeAll_ClearsEverything()
    {
        var store = new SessionStore(new WebAuthOptions());
        store.Create(Admin());
        store.Create(Admin());

        Assert.Equal(2, store.ActiveCount);
        Assert.Equal(2, store.RevokeAll());
        Assert.Equal(0, store.ActiveCount);
    }
}
