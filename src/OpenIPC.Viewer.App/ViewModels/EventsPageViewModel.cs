using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class EventsPageViewModel : ViewModelBase, IDisposable
{
    private const int PageLimit = 200;

    private readonly IEventRepository _repo;
    private readonly EventIngestionService _ingestion;
    private readonly ManualMotionEventSource _manualSource;
    private readonly CameraDirectoryService _cameras;
    private readonly ILogger<EventsPageViewModel> _logger;

    private readonly Dictionary<CameraId, string> _cameraNames = new();
    private readonly IDisposable _liveSub;

    public string Title => Localizer.Instance["Nav.Events"];

    public ObservableCollection<EventRowViewModel> Items { get; } = new();
    public ObservableCollection<CameraOption> CameraOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    public bool IsEmpty => IsLoaded && !IsLoading && Items.Count == 0;

    [ObservableProperty] private CameraOption? _selectedCamera;

    public EventsPageViewModel(
        IEventRepository repo,
        EventIngestionService ingestion,
        ManualMotionEventSource manualSource,
        CameraDirectoryService cameras,
        ILogger<EventsPageViewModel> logger)
    {
        _repo = repo;
        _ingestion = ingestion;
        _manualSource = manualSource;
        _cameras = cameras;
        _logger = logger;

        // Live updates: new events stream into the top of the list as the
        // ingestion service emits them. Both new (open) and finalized events
        // come through Events; we de-dup by Id in MergeOrInsert.
        _liveSub = _ingestion.Events.Subscribe(new EventObserver(this));
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var cams = await _cameras.ListAsync(ct).ConfigureAwait(true);
        _cameraNames.Clear();
        CameraOptions.Clear();
        CameraOptions.Add(new CameraOption(null, Localizer.Instance["Events.AllCameras"]));
        foreach (var c in cams)
        {
            _cameraNames[c.Id] = c.Name;
            CameraOptions.Add(new CameraOption(c.Id, c.Name));
        }
        SelectedCamera ??= CameraOptions[0];

        await ReloadAsync(ct).ConfigureAwait(true);
    }

    partial void OnSelectedCameraChanged(CameraOption? value) =>
        _ = ReloadAsync(CancellationToken.None);

    private async Task ReloadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var events = await _repo.ListAsync(
                cameraId: SelectedCamera?.Id,
                kind: null,
                since: null,
                limit: PageLimit,
                ct).ConfigureAwait(true);

            Items.Clear();
            foreach (var e in events) Items.Add(BuildRow(e));
            IsLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SimulateMotion()
    {
        // Phase 7 §7.1 punt: real per-protocol motion sources aren't built yet
        // (Majestic endpoint TBD; ONVIF PullPoint not in Onvif.Core). This
        // button exercises the full ingestion -> repo -> UI path against the
        // first available camera so the plumbing is testable.
        var target = SelectedCamera?.Id
                     ?? _cameraNames.Keys.FirstOrDefault();
        if (target == default)
        {
            _logger.LogInformation("Simulate motion: no cameras to target");
            return;
        }
        _manualSource.Trigger(target);
    }

    private void OnLiveEvent(CameraEvent ev)
    {
        // Filter: respect the current camera filter for live updates too.
        if (SelectedCamera?.Id is { } selected && ev.CameraId != selected)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var idx = -1;
            for (var i = 0; i < Items.Count; i++)
            {
                if (Items[i].EventId == ev.Id) { idx = i; break; }
            }
            var row = BuildRow(ev);
            if (idx >= 0) Items[idx] = row;
            else Items.Insert(0, row);

            if (Items.Count > PageLimit)
                Items.RemoveAt(Items.Count - 1);
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    private EventRowViewModel BuildRow(CameraEvent e)
    {
        var name = _cameraNames.TryGetValue(e.CameraId, out var n) ? n : Localizer.Instance["Common.Unknown"];
        return new EventRowViewModel(e, name);
    }

    public void Dispose()
    {
        _liveSub.Dispose();
    }

    private sealed class EventObserver : IObserver<CameraEvent>
    {
        private readonly EventsPageViewModel _owner;
        public EventObserver(EventsPageViewModel owner) => _owner = owner;
        public void OnNext(CameraEvent value) => _owner.OnLiveEvent(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

public sealed record CameraOption(CameraId? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class EventRowViewModel
{
    public OpenIPC.Viewer.Core.Events.EventId EventId { get; }
    public string CameraName { get; }
    public string KindLabel { get; }
    public string Source { get; }
    public DateTime OccurredAtLocal { get; }
    public string Duration { get; }

    public EventRowViewModel(CameraEvent ev, string cameraName)
    {
        EventId = ev.Id;
        CameraName = cameraName;
        KindLabel = ev.Kind.ToString();
        Source = ev.Source ?? "?";
        OccurredAtLocal = ev.OccurredAt.ToLocalTime();
        Duration = ev.EndedAt is { } end
            ? $"{(int)(end - ev.OccurredAt).TotalSeconds}s"
            : Localizer.Instance["Events.Open"];
    }
}
