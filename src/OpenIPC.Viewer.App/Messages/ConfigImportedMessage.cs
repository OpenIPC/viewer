namespace OpenIPC.Viewer.App.Messages;

// Broadcast after a config import (Phase 19.2) so the grid and library reload
// to reflect the freshly imported cameras / layouts without an app restart.
public sealed record ConfigImportedMessage;
