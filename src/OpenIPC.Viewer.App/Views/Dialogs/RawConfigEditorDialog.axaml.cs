using System.Text.Json;
using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using OpenIPC.Viewer.App.Services;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class RawConfigEditorDialog : Window
{
    public RawConfigEditorDialog()
    {
        InitializeComponent();

        var editor = this.FindControl<TextEditor>("Editor")!;
        editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("Json");
        var apply = this.FindControl<Button>("ApplyButton")!;
        var cancel = this.FindControl<Button>("CancelButton")!;
        var error = this.FindControl<TextBlock>("ErrorBlock")!;

        cancel.Click += (_, _) => Close((string?)null);
        apply.Click += (_, _) =>
        {
            // Sanity-check parses before handing off — caller will also
            // validate at the HTTP layer but failing here keeps the dialog
            // open with an inline error instead of erroring out post-close.
            try
            {
                using var _ = JsonDocument.Parse(editor.Text);
            }
            catch (JsonException ex)
            {
                error.Text = string.Format(Localizer.Instance["RawConfigEditor.InvalidJsonFormat"], ex.Message);
                error.IsVisible = true;
                return;
            }
            Close(editor.Text);
        };
    }

    public void SetInitialText(string text)
    {
        var editor = this.FindControl<TextEditor>("Editor")!;
        editor.Text = text;
    }
}
