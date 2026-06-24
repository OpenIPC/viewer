using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using OpenIPC.Viewer.App.Services;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class RawConfigEditorContent : UserControl
{
    private readonly TaskCompletionSource<string?> _tcs = new();
    private readonly TextEditor _editor;
    private bool _validateJson = true;

    public Task<string?> Completion => _tcs.Task;

    public RawConfigEditorContent()
    {
        InitializeComponent();

        _editor = this.FindControl<TextEditor>("Editor")!;
        _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("Json");
        var apply = this.FindControl<Button>("ApplyButton")!;
        var cancel = this.FindControl<Button>("CancelButton")!;
        var error = this.FindControl<TextBlock>("ErrorBlock")!;

        cancel.Click += (_, _) => _tcs.TrySetResult(null);
        apply.Click += (_, _) =>
        {
            // The SSH path edits majestic.yaml (YAML, not JSON); only gate on
            // JSON well-formedness for the HTTP config.json editor. The camera
            // validates the YAML itself on reload.
            if (_validateJson)
            {
                try
                {
                    using var _ = JsonDocument.Parse(_editor.Text);
                }
                catch (JsonException ex)
                {
                    error.Text = string.Format(Localizer.Instance["RawConfigEditor.InvalidJsonFormat"], ex.Message);
                    error.IsVisible = true;
                    return;
                }
            }
            _tcs.TrySetResult(_editor.Text);
        };
    }

    public void SetInitialText(string text) => _editor.Text = text;

    // When false, the editor holds YAML (majestic.yaml over SSH): skip the JSON
    // validation gate and drop the JSON syntax highlighting.
    public void SetValidateJson(bool validate)
    {
        _validateJson = validate;
        if (!validate)
            _editor.SyntaxHighlighting = null;
    }
}
