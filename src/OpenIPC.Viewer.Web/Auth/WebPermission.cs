using System;

namespace OpenIPC.Viewer.Web.Auth;

// What a signed-in principal may do. Deliberately the same set (same names,
// same meaning) as the private .ovac access-config's Core Permission flags, so
// that provider can map its users onto these one-for-one later without touching
// a single endpoint — the endpoints only ever ask "may this identity do X".
[Flags]
public enum WebPermission
{
    None = 0,
    ViewLive = 1,
    ViewArchive = 2,
    Ptz = 4,
    Export = 8,
    // Everything that changes the installation: camera/group/layout CRUD,
    // discovery, config import/export, sessions, users.
    Manage = 16,
    // Speaking through the camera's speaker. Its own flag rather than a reuse:
    // it is neither "change the installation" nor PTZ, and it is the one action
    // here that reaches people standing in front of the camera — worth being
    // able to grant and withhold on its own.
    Talk = 32,

    All = ViewLive | ViewArchive | Ptz | Export | Manage | Talk,
}
