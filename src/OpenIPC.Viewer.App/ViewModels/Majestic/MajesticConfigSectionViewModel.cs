using System.Collections.Generic;

namespace OpenIPC.Viewer.App.ViewModels.Majestic;

// A config section ("video0", "isp", "system", …) with its editable rows.
// Section name "" (top-level scalars) is shown under a "general" header.
public sealed class MajesticConfigSectionViewModel
{
    public string Name { get; }
    public string Header { get; }
    public IReadOnlyList<MajesticFieldRowViewModel> Fields { get; }

    public MajesticConfigSectionViewModel(string name, IReadOnlyList<MajesticFieldRowViewModel> fields)
    {
        Name = name;
        Header = string.IsNullOrEmpty(name) ? "general" : name;
        Fields = fields;
    }
}
