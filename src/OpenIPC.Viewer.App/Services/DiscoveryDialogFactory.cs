using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.App.Services;

public sealed class DiscoveryDialogFactory
{
    private readonly IDiscoveryAggregator _aggregator;
    private readonly OnvifProbeService _probe;
    private readonly IMajesticClient _majestic;
    private readonly DiscoverySessionCache _cache;
    private readonly ILoggerFactory _loggerFactory;

    public DiscoveryDialogFactory(
        IDiscoveryAggregator aggregator,
        OnvifProbeService probe,
        IMajesticClient majestic,
        DiscoverySessionCache cache,
        ILoggerFactory loggerFactory)
    {
        _aggregator = aggregator;
        _probe = probe;
        _majestic = majestic;
        _cache = cache;
        _loggerFactory = loggerFactory;
    }

    // knownHosts: hosts of cameras already in the library, so rows can carry
    // an "already added" badge during the multi-add loop.
    public DiscoveryDialogViewModel Create(IReadOnlySet<string> knownHosts) =>
        new(_aggregator, _probe, _majestic, _cache, knownHosts,
            _loggerFactory.CreateLogger<DiscoveryDialogViewModel>());
}
