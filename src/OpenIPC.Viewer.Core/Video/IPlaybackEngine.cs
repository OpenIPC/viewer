namespace OpenIPC.Viewer.Core.Video;

// Opens recorded files for playback (Phase 16). Kept separate from
// IVideoEngine so live-only heads aren't forced to implement file playback;
// the desktop FfmpegVideoEngine implements both.
public interface IPlaybackEngine
{
    IPlaybackSession OpenFile(PlaybackOptions options);
}
