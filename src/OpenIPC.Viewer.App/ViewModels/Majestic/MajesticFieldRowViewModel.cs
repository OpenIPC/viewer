using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenIPC.Viewer.Core.Majestic;

namespace OpenIPC.Viewer.App.ViewModels.Majestic;

// One editable row in the schema-driven "All settings" editor. Wraps a parsed
// MajesticConfigField, holds the live edited Value as a string, and exposes
// kind flags so the view can pick a control (toggle / combo / numeric / text)
// without a DataTemplateSelector.
public sealed partial class MajesticFieldRowViewModel : ObservableObject
{
    private readonly MajesticConfigField _field;

    public MajesticFieldRowViewModel(MajesticConfigField field, IReadOnlyList<string>? optionOverride = null)
    {
        _field = field;
        _value = field.Value;
        Options = optionOverride ?? field.Options;
    }

    public string Section => _field.Section;
    public string Key => _field.Key;
    public string Path => _field.Path;
    public string Label => _field.Key;
    public bool RequiresRestart => _field.RequiresRestart;
    public IReadOnlyList<string>? Options { get; }

    // The current edited value, canonical string form. ComputeEdits compares
    // this against the original to decide what to write back.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoolValue))]
    private string _value;

    // ToggleSwitch binds here; maps onto the string Value as "true"/"false".
    public bool BoolValue
    {
        get => string.Equals(Value, "true", System.StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    // Control selection — exactly one is true.
    public bool IsBool => _field.Kind == MajesticFieldKind.Bool;
    public bool IsCombo => !IsBool && Options is { Count: > 0 };
    public bool IsNumeric => !IsBool && !IsCombo &&
        (_field.Kind == MajesticFieldKind.Int || _field.Kind == MajesticFieldKind.Number);
    public bool IsText => !IsBool && !IsCombo && !IsNumeric;

    // Numeric and free-text both render as a TextBox (the value round-trips as a
    // string either way); coercion to the right JSON type happens on write-back.
    public bool IsTextual => IsNumeric || IsText;
}
