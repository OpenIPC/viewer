using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIPC.Viewer.Core.Majestic;

namespace OpenIPC.Viewer.App.ViewModels.Majestic;

// One editable row in the schema-driven editor. Wraps a parsed
// MajesticConfigField, holds the live edited Value as a string, tracks whether
// it differs from the loaded baseline (for highlight + revert), and exposes
// kind flags so the view can pick a control without a DataTemplateSelector.
public sealed partial class MajesticFieldRowViewModel : ObservableObject
{
    private readonly MajesticConfigField _field;
    // The value last seen on the camera. Updated on Commit() after a successful
    // apply that doesn't rebuild the rows (e.g. live ISP), so a second apply
    // diffs correctly and the "modified" marker clears.
    private string _original;

    public MajesticFieldRowViewModel(MajesticConfigField field)
    {
        _field = field;
        _original = field.Value;
        _value = field.Value;
        Options = field.Options;
    }

    public string Section => _field.Section;
    public string Key => _field.Key;
    public string Path => _field.Path;
    public string Label => _field.Key;
    public bool RequiresRestart => _field.RequiresRestart;
    public IReadOnlyList<string>? Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoolValue))]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private string _value;

    // Set by the page's field filter; the view binds row visibility to this.
    [ObservableProperty]
    private bool _matchesFilter = true;

    // ToggleSwitch binds here; maps onto the string Value as "true"/"false".
    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    // True when the edited value differs from the camera baseline (kind-aware,
    // matching the diff that Apply uses).
    public bool IsModified => !MajesticConfigModel.ValuesEqual(_field.Kind, _original, Value);

    [RelayCommand]
    private void Revert() => Value = _original;

    // Re-baseline to the current value (call after a successful apply that keeps
    // the rows alive).
    public void Commit()
    {
        _original = Value;
        OnPropertyChanged(nameof(IsModified));
    }

    public bool Matches(string filter) =>
        filter.Length == 0
        || Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Section.Contains(filter, StringComparison.OrdinalIgnoreCase);

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
