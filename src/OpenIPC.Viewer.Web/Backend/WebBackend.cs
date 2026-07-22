using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Settings;
using OpenIPC.Viewer.Devices.Discovery;
using OpenIPC.Viewer.Devices.Majestic;
using OpenIPC.Viewer.Devices.Onvif;
using OpenIPC.Viewer.Devices.Onvif.Discovery;
using OpenIPC.Viewer.Infrastructure.Net;
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
        services.AddSingleton<ILayoutRepository, SqliteLayoutRepository>();
        // The archive the desktop head recorded: listed, played and deleted from
        // the browser. The web server doesn't record yet, it reads the index.
        services.AddSingleton<IRecordingRepository, SqliteRecordingRepository>();
        // Recording started from the browser: its own ffmpeg process per camera,
        // writing into the same folder and table the desktop head uses.
        services.AddSingleton<WebRecorder>();
        // Browser-safe config export/import (never serializes camera passwords).
        services.AddSingleton<IConfigBackupService, SqliteConfigBackupService>();
        // CRUD goes through the directory service so credentials land in the
        // secrets store (never the DB row / API). It resolves ISecretsStore,
        // which the platform host (Desktop) registers alongside this call; the
        // optional settings/layout deps default to null when absent.
        services.AddSingleton<CameraDirectoryService>();
        // Fan-out for live video: one shared ffmpeg session per (camera, mode).
        services.AddSingleton<LiveStreamHub>();
        // ONVIF transport for the PTZ endpoints. The hand-rolled SOAP client is
        // stateless (one short-lived HTTP call per operation), so a singleton is
        // safe and matches the desktop registration in SharedComposition.
        services.AddSingleton<IOnvifClient, SoapOnvifClient>();
        // Discovery (same source set and aggregator as the desktop dialog) plus
        // the ONVIF probe chain the add flow runs on a chosen candidate.
        services.AddSingleton<OnvifProbeService>();
        // The sources read settings (interface pick, timeouts) through this view;
        // the server has no settings UI, so it hands them the shipped defaults.
        services.AddSingleton<IUserSettingsAccessor, ServerSettings>();
        services.AddSingleton<INetworkInterfaceProvider, SystemNetworkInterfaceProvider>();
        services.AddSingleton<IReachabilityProbe, TcpReachabilityProbe>();
        services.AddSingleton<IMajesticClient, MajesticHttpClient>();
        // The camera-settings panel renders whatever knobs the live config.json
        // exposes, so it needs the same schema walk the desktop editor uses.
        services.AddSingleton<IMajesticConfigSchema, MajesticConfigSchema>();
        services.AddSingleton<IDiscoveryService, WsDiscoveryService>();
        services.AddSingleton<IDiscoverySource, OnvifDiscoverySource>();
        services.AddSingleton<IDiscoverySource, MdnsDiscoverySource>();
        services.AddSingleton<IDiscoverySource, SubnetSweepDiscoverySource>();
        services.AddSingleton<IDiscoveryAggregator, DiscoveryAggregator>();
        // Scans outlive the request that starts them, so the runs live here.
        services.AddSingleton<DiscoveryScanStore>();
        return services;
    }
}
