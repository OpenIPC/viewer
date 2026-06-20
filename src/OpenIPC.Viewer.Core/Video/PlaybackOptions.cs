namespace OpenIPC.Viewer.Core.Video;

// Options for opening a recorded file for playback (Phase 16). Unlike
// VideoSessionOptions this carries no network/RTSP knobs — it points at a
// local segment on disk and lets the engine pace presentation off the file's
// own timestamps.
public sealed record PlaybackOptions(
    string FilePath,
    HwAccelHint HwAccel)
{
    public static PlaybackOptions Default(string filePath) =>
        new(filePath, HwAccelHint.Auto);
}
