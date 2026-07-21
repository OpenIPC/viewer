using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenIPC.Viewer.Web.Auth;

// Authenticates against the user roster (WebUserStore), plus one bootstrap
// admin account whose password comes from WebAuthOptions — when absent, a
// random one is generated for this run and logged once, so a fresh server is
// protected without shipping a default.
//
// The bootstrap admin always works, even once a roster exists. That is
// deliberate: it is the recovery account for an install whose only Manage user
// was deleted or forgot their password, mirroring the desktop's synthetic
// administrator principal in the access config.
public sealed class PasswordAuthProvider : IWebAuthProvider
{
    private readonly string _adminUser;
    private readonly string _passwordHash;
    private readonly WebUserStore _users;

    public PasswordAuthProvider(WebAuthOptions options, WebUserStore users, ILogger<PasswordAuthProvider> logger)
    {
        _adminUser = options.AdminUser;
        _users = users;

        var password = options.AdminPassword;
        if (string.IsNullOrEmpty(password))
        {
            password = GeneratePassword();
            logger.LogWarning(
                "No admin password configured — generated one for this run. " +
                "User '{User}', password: {Password}. Set an admin password to make it stable.",
                _adminUser, password);
        }

        _passwordHash = PasswordHash.Create(password);
    }

    public ValueTask<WebIdentity?> ValidateCredentialsAsync(string user, string password, CancellationToken ct)
    {
        // Verify the bootstrap hash regardless of the username outcome so a wrong
        // username and a wrong password cost the same time (no user enumeration
        // via timing). Username itself is not a secret, so an ordinal compare is
        // fine.
        var bootstrapPasswordOk = PasswordHash.Verify(password, _passwordHash);
        var isBootstrapUser = string.Equals(user, _adminUser, StringComparison.Ordinal);
        if (isBootstrapUser && bootstrapPasswordOk)
            return new ValueTask<WebIdentity?>(WebIdentity.Administrator(_adminUser));

        var record = _users.Find(user);
        if (record is null || !PasswordHash.Verify(password, record.PasswordHash))
            return new ValueTask<WebIdentity?>((WebIdentity?)null);

        return new ValueTask<WebIdentity?>(new WebIdentity(
            record.Name,
            new HashSet<string>(StringComparer.Ordinal) { WebRoles.ForPermissions(record.Permissions) },
            record.Permissions,
            // Null stays null (unrestricted); a list — even an empty one — is a
            // real restriction and must be honoured as written.
            record.Cameras is null
                ? null
                : new HashSet<string>(record.Cameras, StringComparer.OrdinalIgnoreCase)));
    }

    private static string GeneratePassword() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12));
}
