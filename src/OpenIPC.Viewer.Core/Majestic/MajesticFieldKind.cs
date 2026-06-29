namespace OpenIPC.Viewer.Core.Majestic;

// How a schema-driven config field should be presented and round-tripped.
// We keep the value as a canonical string everywhere (see MajesticConfigField)
// and use the kind to pick a control (toggle / numeric / combo / text) and to
// coerce the string back to the right JSON type on write-back.
public enum MajesticFieldKind
{
    String,
    Bool,
    Int,
    Number,
    // String with a known candidate set (rendered as a combo). Options live on
    // the field. Still serialized as a JSON string.
    Enum,
}
