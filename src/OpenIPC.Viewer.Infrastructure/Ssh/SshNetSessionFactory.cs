using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Settings;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Infrastructure.Ssh;

/// <summary>Creates SSH.NET-backed sessions. Registered as a DI singleton.</summary>
public sealed class SshNetSessionFactory : ISshSessionFactory
{
    private readonly ISshHostKeyStore _hostKeys;
    private readonly IUserSettingsAccessor _settings;
    private readonly ISshHostKeyPrompt _prompt;
    private readonly ILoggerFactory _loggerFactory;

    public SshNetSessionFactory(ISshHostKeyStore hostKeys, IUserSettingsAccessor settings, ISshHostKeyPrompt prompt, ILoggerFactory loggerFactory)
    {
        _hostKeys = hostKeys;
        _settings = settings;
        _prompt = prompt;
        _loggerFactory = loggerFactory;
    }

    public ISshSession Create() =>
        new SshNetSession(_hostKeys, _settings, _prompt, _loggerFactory.CreateLogger<SshNetSession>());
}
