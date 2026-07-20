using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenIPC.Viewer.Web.Auth;

// The minimal public-build auth provider: one admin account. The password comes
// from WebAuthOptions; when absent, a random one is generated for this run and
// logged once (so a fresh server is protected without shipping a default). The
// hash lives only in memory here — persisting it is part of the backend slice.
public sealed class PasswordAuthProvider : IWebAuthProvider
{
    private readonly string _adminUser;
    private readonly string _passwordHash;

    public PasswordAuthProvider(WebAuthOptions options, ILogger<PasswordAuthProvider> logger)
    {
        _adminUser = options.AdminUser;

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
        // Verify the password hash regardless of the username outcome so a wrong
        // username and a wrong password cost the same time (no user enumeration
        // via timing). Username itself is not a secret, so an ordinal compare is
        // fine.
        var passwordOk = PasswordHash.Verify(password, _passwordHash);
        var userOk = string.Equals(user, _adminUser, StringComparison.Ordinal);

        WebIdentity? identity = userOk && passwordOk
            ? new WebIdentity(_adminUser, new HashSet<string>(StringComparer.Ordinal) { WebRoles.Admin })
            : null;

        return new ValueTask<WebIdentity?>(identity);
    }

    private static string GeneratePassword() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12));
}
