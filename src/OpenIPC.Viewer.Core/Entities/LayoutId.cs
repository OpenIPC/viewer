namespace OpenIPC.Viewer.Core.Entities;

// INTEGER PRIMARY KEY AUTOINCREMENT, same convention as GroupId (Phase 19.1).
public readonly record struct LayoutId(int Value)
{
    public override string ToString() => Value.ToString();
}
