using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.App.Services;

/// <summary>Builds <see cref="ImageViewerViewModel"/>s with their DI dependencies.</summary>
public sealed class ImageViewerFactory
{
    private readonly ISnapshotRepository _repo;
    private readonly IDialogService _dialogs;
    private readonly ILoggerFactory _loggerFactory;

    public ImageViewerFactory(ISnapshotRepository repo, IDialogService dialogs, ILoggerFactory loggerFactory)
    {
        _repo = repo;
        _dialogs = dialogs;
        _loggerFactory = loggerFactory;
    }

    public ImageViewerViewModel Create(IReadOnlyList<SnapshotViewEntry> items, int startIndex) =>
        new(items, startIndex, _repo, _dialogs, _loggerFactory.CreateLogger<ImageViewerViewModel>());
}
