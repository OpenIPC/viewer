using System;

namespace OpenIPC.Viewer.Web.Auth;

// Auth knobs for the minimal (single-admin) provider. When AdminPassword is
// null, a random one is generated at startup and logged once — so a fresh
// --server-only run is never silently wide open, and never ships a default
// password either.
public sealed record WebAuthOptions
{
    public string AdminUser { get; init; } = "admin";

    public string? AdminPassword { get; init; }

    // Where the user roster lives (JSON, next to the database). Null disables
    // the roster entirely — then the bootstrap admin above is the only account.
    public string? UsersFilePath { get; init; }

    public TimeSpan SessionTtl { get; init; } = TimeSpan.FromHours(12);
}
