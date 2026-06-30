namespace OpenIPC.Viewer.Core.Majestic;

// A single field the user changed, ready to be written back into config.json.
// Kind drives JSON coercion in the Devices writer (Bool -> json bool, Int ->
// json number, etc.); Value is the new canonical string.
public sealed record MajesticConfigFieldEdit(
    string Section,
    string Key,
    MajesticFieldKind Kind,
    string Value);
