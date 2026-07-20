using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Infrastructure.Persistence;
using OpenIPC.Viewer.Web.Api;

namespace OpenIPC.Viewer.Web.Backend;

// The lean, UI-free backend the web API composes: SQLite persistence only.
//
// Deliberately does NOT go through SharedComposition — that registers the
// Avalonia App layer (ViewModels, dialog factories) the headless server has no
// use for and shouldn't drag in. Video/ONVIF/Majestic join here in later slices
// as their endpoints arrive, each a small explicit registration.
public static class WebBackend
{
    public static IServiceCollection AddWebBackend(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(databasePath));
        services.AddSingleton<IMigrationRunner, MigrationRunner>();
        services.AddSingleton<ICameraRepository, SqliteCameraRepository>();
        services.AddSingleton<IGroupRepository, SqliteGroupRepository>();
        // CRUD goes through the directory service so credentials land in the
        // secrets store (never the DB row / API). It resolves ISecretsStore,
        // which the platform host (Desktop) registers alongside this call; the
        // optional settings/layout deps default to null when absent.
        services.AddSingleton<CameraDirectoryService>();
        // Fan-out for live video: one shared ffmpeg session per (camera, mode).
        services.AddSingleton<LiveStreamHub>();
        return services;
    }
}
