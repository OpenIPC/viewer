using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Majestic;

namespace OpenIPC.Viewer.App.Services;

public sealed class DiscoveryDialogFactory
{
    private readonly IDiscoveryAggregator _aggregator;
    private readonly IMajesticClient _majestic;
    private readonly IScanTargetProvider _scanTargets;
    private readonly DiscoverySessionCache _cache;
    private readonly ILoggerFactory _loggerFactory;

    public DiscoveryDialogFactory(
        IDiscoveryAggregator aggregator,
        IMajesticClient majestic,
        IScanTargetProvider scanTargets,
        DiscoverySessionCache cache,
        ILoggerFactory loggerFactory)
    {
        _aggregator = aggregator;
        _majestic = majestic;
        _scanTargets = scanTargets;
        _cache = cache;
        _loggerFactory = loggerFactory;
    }

    // Session memory (results, deep-scan choices, last login) shared by every
    // dialog opened this run.
    public DiscoverySessionCache Session => _cache;

    // knownHosts: hosts of cameras already in the library, so rows can carry
    // an "already added" badge during the multi-add loop.
    public DiscoveryDialogViewModel Create(IReadOnlySet<string> knownHosts) =>
        new(_aggregator, _majestic, _scanTargets, _cache, knownHosts,
            _loggerFactory.CreateLogger<DiscoveryDialogViewModel>());
}
