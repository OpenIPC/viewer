using System;

namespace OpenIPC.Viewer.Core.Video;

public sealed class TooManySessionsException : Exception
{
    public int Limit { get; }

    public TooManySessionsException(int limit)
        : base($"Active session limit ({limit}) reached.")
    {
        Limit = limit;
    }
}
