using System;
using System.Collections.Generic;

namespace OpenIPC.Viewer.Web.Auth;

// Who a web request is acting as, once authenticated: what they may do
// (Permissions) and which cameras they may see (Cameras; null = all of them).
// Roles stay a set so a richer provider can carry its own vocabulary, but
// authorization decisions are made on Permissions alone.
public sealed record WebIdentity(
    string Name,
    IReadOnlySet<string> Roles,
    WebPermission Permissions = WebPermission.All,
    IReadOnlySet<string>? Cameras = null)
{
    public bool IsInRole(string role) => Roles.Contains(role);

    public bool IsAdmin => Roles.Contains(WebRoles.Admin);

    public bool Can(WebPermission permission) => (Permissions & permission) == permission;

    // A null camera set means unrestricted — the same "no access config, no
    // limits" default the desktop applies when no .ovac is deployed.
    public bool CanSee(string cameraId) => Cameras is null || Cameras.Contains(cameraId);

    public static WebIdentity Administrator(string name) => new(
        name,
        new HashSet<string>(StringComparer.Ordinal) { WebRoles.Admin },
        WebPermission.All);
}

// Role names. Derived from permissions for display; the flags remain the source
// of truth for every decision.
public static class WebRoles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";

    public static string ForPermissions(WebPermission permissions) =>
        (permissions & WebPermission.Manage) != 0 ? Admin
        : (permissions & WebPermission.Ptz) != 0 ? Operator
        : Viewer;
}
