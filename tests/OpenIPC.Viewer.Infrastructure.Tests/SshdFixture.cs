using System.Net.Sockets;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Infrastructure.Tests;

// Probes whether the test sshd container is reachable. SSH integration tests
// skip themselves when it isn't (e.g. Windows CI without Docker).
//
// Usage:
//   docker compose -f tools/sshd/docker-compose.yml up -d
//   dotnet test tests/OpenIPC.Viewer.Infrastructure.Tests
public static class SshdFixture
{
    public const string Host = "localhost";
    public const int Port = 2222;
    public const string Username = "tester";
    public const string Password = "testpass";

    public static SshEndpoint Endpoint =>
        new(Host, Port, Username, new SshAuth.Password(Password));

    public static bool IsReachable(int timeoutMs = 500)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(Host, Port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
