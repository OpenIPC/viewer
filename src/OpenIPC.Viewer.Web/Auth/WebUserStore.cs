using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OpenIPC.Viewer.Web.Auth;

// The server's user roster: who may sign in to the web console, what they may
// do, and which cameras they see.
//
// A plain JSON file next to the database rather than a DB table, for two
// reasons: it stays readable and hand-fixable when an admin locks themselves
// out, and it keeps the schema out of the shared migrations that the desktop
// heads also run — the roster is a server-only concept.
//
// Only PBKDF2 hashes are stored (Auth/PasswordHash), never passwords. The file
// is written atomically (temp + replace) so a crash mid-save cannot leave a
// truncated roster that locks everyone out.
public sealed class WebUserStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _path;
    private readonly ILogger<WebUserStore> _logger;
    private readonly object _gate = new();
    private List<WebUserRecord> _users = new();

    public WebUserStore(WebAuthOptions options, ILogger<WebUserStore> logger)
    {
        _path = options.UsersFilePath;
        _logger = logger;
        Reload();
    }

    // False when the host didn't give us a path (tests, auth-only runs): then
    // the bootstrap admin is the only principal and nothing can be edited.
    public bool IsAvailable => _path is not null;

    public IReadOnlyList<WebUserRecord> Users
    {
        get { lock (_gate) return _users.ToList(); }
    }

    public WebUserRecord? Find(string name)
    {
        lock (_gate)
            return _users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public WebUserRecord Add(string name, string password, WebPermission permissions, IReadOnlyList<string>? cameras)
    {
        var record = new WebUserRecord
        {
            Name = name,
            PasswordHash = PasswordHash.Create(password),
            Permissions = permissions,
            Cameras = cameras?.ToList(),
        };
        lock (_gate)
        {
            if (_users.Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("user exists");
            _users.Add(record);
            Save();
        }
        return record;
    }

    // A null password leaves the current one alone — editing permissions must
    // not force the admin to know (or reset) the user's password.
    public WebUserRecord? Update(
        string name, string? password, WebPermission? permissions, IReadOnlyList<string>? cameras, bool clearCameras)
    {
        lock (_gate)
        {
            var existing = _users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return null;

            if (!string.IsNullOrEmpty(password)) existing.PasswordHash = PasswordHash.Create(password);
            if (permissions is { } p) existing.Permissions = p;
            if (clearCameras) existing.Cameras = null;
            else if (cameras is not null) existing.Cameras = cameras.ToList();

            Save();
            return existing;
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var removed = _users.RemoveAll(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private void Reload()
    {
        if (_path is null || !File.Exists(_path))
            return;
        try
        {
            var roster = JsonSerializer.Deserialize<RosterFile>(File.ReadAllText(_path), Json);
            _users = roster?.Users ?? new List<WebUserRecord>();
        }
        catch (Exception ex)
        {
            // Refuse to silently start with an empty roster — that would hand
            // everyone the bootstrap-admin-only fallback without saying why.
            _logger.LogError(ex, "Could not read the web user roster at {Path}; no roster users will be able to sign in", _path);
        }
    }

    private void Save()
    {
        if (_path is null)
            return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new RosterFile { Users = _users }, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class RosterFile
    {
        public List<WebUserRecord> Users { get; set; } = new();
    }
}

// One roster entry. Cameras null = every camera; an empty list = none (a user
// parked without access rather than deleted).
public sealed class WebUserRecord
{
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public WebPermission Permissions { get; set; } = WebPermission.ViewLive;
    public List<string>? Cameras { get; set; }
}
