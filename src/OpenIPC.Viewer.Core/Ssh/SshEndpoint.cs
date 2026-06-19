namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// Connection target for an SSH session. Credentials are passed in here rather
/// than resolved inside the session so the Core contract stays free of the
/// secrets store.
/// </summary>
public sealed record SshEndpoint(string Host, int Port, string Username, SshAuth Auth)
{
    public const int DefaultPort = 22;
}

/// <summary>SSH authentication material — password or private key.</summary>
public abstract record SshAuth
{
    public sealed record Password(string Value) : SshAuth;

    /// <summary>Path to a private key file, with an optional passphrase.</summary>
    public sealed record PrivateKey(string KeyPath, string? Passphrase) : SshAuth;
}
