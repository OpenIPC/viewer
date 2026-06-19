namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>Outcome of a one-shot remote command (<see cref="ISshSession.ExecAsync"/>).</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}
