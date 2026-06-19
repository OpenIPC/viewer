using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Infrastructure.Ssh;

/// <summary>Creates SSH.NET-backed sessions. Registered as a DI singleton.</summary>
public sealed class SshNetSessionFactory : ISshSessionFactory
{
    private readonly ISecretsStore _secrets;
    private readonly ILoggerFactory _loggerFactory;

    public SshNetSessionFactory(ISecretsStore secrets, ILoggerFactory loggerFactory)
    {
        _secrets = secrets;
        _loggerFactory = loggerFactory;
    }

    public ISshSession Create() =>
        new SshNetSession(_secrets, _loggerFactory.CreateLogger<SshNetSession>());
}
