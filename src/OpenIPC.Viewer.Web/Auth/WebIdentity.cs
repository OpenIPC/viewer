using System.Collections.Generic;

namespace OpenIPC.Viewer.Web.Auth;

// Who a web request is acting as, once authenticated. Roles are kept as a set
// (not a bool) so the richer RBAC provider (private .ovac) can populate real
// roles later without changing this shape or the endpoints that read it.
public sealed record WebIdentity(string Name, IReadOnlySet<string> Roles)
{
    public bool IsInRole(string role) => Roles.Contains(role);

    public bool IsAdmin => Roles.Contains(WebRoles.Admin);
}

// Role names. The minimal provider only issues Admin; the RBAC provider adds
// the rest against the same constants.
public static class WebRoles
{
    public const string Admin = "admin";
}
