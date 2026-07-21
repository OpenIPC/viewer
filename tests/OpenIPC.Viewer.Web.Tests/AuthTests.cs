using System;
using System.IO;
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

    // No UsersFilePath: the roster is unavailable, so only the bootstrap admin
    // can sign in — the shape a fresh install starts in.
    private static PasswordAuthProvider Provider(WebAuthOptions options, WebUserStore? users = null) =>
        new(options,
            users ?? new WebUserStore(options, NullLogger<WebUserStore>.Instance),
            NullLogger<PasswordAuthProvider>.Instance);

    [Fact]
    public async Task Provider_AcceptsCorrectCredentials_RejectsWrong()
    {
        var provider = Provider(new WebAuthOptions { AdminUser = "admin", AdminPassword = "hunter2" });

        var admin = await provider.ValidateCredentialsAsync("admin", "hunter2", CancellationToken.None);
        Assert.NotNull(admin);
        Assert.True(admin!.Can(WebPermission.Manage));
        Assert.Null(await provider.ValidateCredentialsAsync("admin", "wrong", CancellationToken.None));
        Assert.Null(await provider.ValidateCredentialsAsync("root", "hunter2", CancellationToken.None));
    }

    // A roster user gets exactly the permissions and camera subset stored for
    // them — and nothing more, however the endpoints later ask.
    [Fact]
    public async Task Provider_RosterUser_CarriesPermissionsAndCameraSubset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openipc-roster-{Guid.NewGuid():n}.json");
        var options = new WebAuthOptions { AdminUser = "admin", AdminPassword = "hunter2", UsersFilePath = path };
        try
        {
            var store = new WebUserStore(options, NullLogger<WebUserStore>.Instance);
            store.Add("watcher", "pw", WebPermission.ViewLive, new[] { "cam-1" });
            var provider = Provider(options, store);

            var identity = await provider.ValidateCredentialsAsync("watcher", "pw", CancellationToken.None);
            Assert.NotNull(identity);
            Assert.True(identity!.Can(WebPermission.ViewLive));
            Assert.False(identity.Can(WebPermission.Ptz));
            Assert.False(identity.Can(WebPermission.Manage));
            Assert.True(identity.CanSee("cam-1"));
            Assert.False(identity.CanSee("cam-2"));

            Assert.Null(await provider.ValidateCredentialsAsync("watcher", "nope", CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
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
