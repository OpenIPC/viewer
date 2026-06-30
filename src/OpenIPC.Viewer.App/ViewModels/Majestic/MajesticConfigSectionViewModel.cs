using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenIPC.Viewer.App.ViewModels.Majestic;

// A config section ("video0", "isp", "system", …) with its editable rows.
// Section name "" (top-level scalars) is shown under a "general" header.
public sealed partial class MajesticConfigSectionViewModel : ObservableObject
{
    public string Name { get; }
    public string Header { get; }
    public IReadOnlyList<MajesticFieldRowViewModel> Fields { get; }

    // Hidden by the page's field filter when none of its rows match.
    [ObservableProperty]
    private bool _isVisibleInFilter = true;

    public MajesticConfigSectionViewModel(string name, IReadOnlyList<MajesticFieldRowViewModel> fields)
    {
        Name = name;
        Header = string.IsNullOrEmpty(name) ? "general" : name;
        Fields = fields;
    }

    // Revert every row in this section back to the camera baseline.
    [RelayCommand]
    private void RevertSection()
    {
        foreach (var row in Fields) row.RevertCommand.Execute(null);
    }
}
