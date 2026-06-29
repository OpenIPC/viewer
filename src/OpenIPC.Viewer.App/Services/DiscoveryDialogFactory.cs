using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.App.Services;

public sealed class DiscoveryDialogFactory
{
    private readonly IDiscoveryAggregator _aggregator;
    private readonly OnvifProbeService _probe;
    private readonly ILoggerFactory _loggerFactory;

    public DiscoveryDialogFactory(
        IDiscoveryAggregator aggregator,
        OnvifProbeService probe,
        ILoggerFactory loggerFactory)
    {
        _aggregator = aggregator;
        _probe = probe;
        _loggerFactory = loggerFactory;
    }

    public DiscoveryDialogViewModel Create() =>
        new(_aggregator, _probe, _loggerFactory.CreateLogger<DiscoveryDialogViewModel>());
}
