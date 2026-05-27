using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

public sealed partial class ManageGroupsViewModel : ViewModelBase
{
    private readonly CameraDirectoryService _directory;
    private readonly ILogger<ManageGroupsViewModel> _logger;

    public ObservableCollection<GroupRowViewModel> Groups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _newGroupName = "";

    [ObservableProperty] private string? _errorMessage;

    public bool CanAdd => !string.IsNullOrWhiteSpace(NewGroupName);

    public ManageGroupsViewModel(CameraDirectoryService directory, ILogger<ManageGroupsViewModel> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var groups = await _directory.ListGroupsAsync(ct).ConfigureAwait(true);
        Groups.Clear();
        foreach (var g in groups)
            Groups.Add(new GroupRowViewModel(g, this));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewGroupName.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            await _directory.AddGroupAsync(name, CancellationToken.None).ConfigureAwait(true);
            NewGroupName = "";
            ErrorMessage = null;
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add group {Name}", name);
            ErrorMessage = ex.Message;
        }
    }

    public async Task RenameAsync(GroupId id, string newName)
    {
        try
        {
            await _directory.RenameGroupAsync(id, newName.Trim(), CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rename group {Id}", id);
            ErrorMessage = ex.Message;
        }
    }

    public async Task RemoveAsync(GroupId id)
    {
        try
        {
            await _directory.RemoveGroupAsync(id, CancellationToken.None).ConfigureAwait(true);
            ErrorMessage = null;
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // FK constraint trips when cameras still reference this group;
            // surface the message instead of swallowing.
            _logger.LogInformation(ex, "Remove group {Id} blocked", id);
            ErrorMessage = Localizer.Instance["Groups.Error.CamerasReferenced"];
        }
    }
}

public sealed partial class GroupRowViewModel : ViewModelBase
{
    private readonly ManageGroupsViewModel _owner;

    public GroupId Id { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isEditing;

    public GroupRowViewModel(CameraGroup g, ManageGroupsViewModel owner)
    {
        Id = g.Id;
        Name = g.Name;
        _owner = owner;
    }

    [RelayCommand]
    private void BeginRename() => IsEditing = true;

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        IsEditing = false;
        if (!string.IsNullOrWhiteSpace(Name))
            await _owner.RenameAsync(Id, Name).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task DeleteAsync() => _owner.RemoveAsync(Id);
}
