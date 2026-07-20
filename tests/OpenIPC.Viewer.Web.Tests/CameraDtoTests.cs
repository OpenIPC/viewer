using System.Text.Json;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Web.Api;

namespace OpenIPC.Viewer.Web.Tests;

// The "golden" §20.3 guard: the camera DTO must never carry a secret across the
// API boundary — no camera password, no secret-store key refs, no RTSP URL with
// inline credentials.
public sealed class CameraDtoTests
{
    private static Camera SampleCamera() => new(
        Id: CameraId.Parse("11111111-1111-1111-1111-111111111111"),
        GroupId: new GroupId(3),
        Name: "Front door",
        Host: "10.0.0.9",
        OnvifPort: 8899,
        HttpPort: 80,
        RtspMainUri: new Uri("rtsp://admin:s3cr3tPass@10.0.0.9:554/stream0"),
        RtspSubUri: new Uri("rtsp://admin:s3cr3tPass@10.0.0.9:554/stream1"),
        UsernameRef: "cam:11111111-1111-1111-1111-111111111111:username",
        PasswordRef: "cam:11111111-1111-1111-1111-111111111111:password",
        OnvifEnabled: true,
        OnvifProfileToken: "Profile_1",
        ChipModel: "SSC338Q",
        FirmwareVersion: "fw1",
        IncludedInGrid: true,
        HasPtz: false,
        IsMajestic: true,
        SortOrder: 0,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    [Fact]
    public void From_StripsRtspCredentials()
    {
        var dto = CameraDto.From(SampleCamera(), groupName: "Outdoor");

        Assert.Equal("rtsp://10.0.0.9:554/stream0", dto.RtspMain);
        Assert.Equal("rtsp://10.0.0.9:554/stream1", dto.RtspSub);
        Assert.DoesNotContain("@", dto.RtspMain);
        Assert.DoesNotContain("s3cr3tPass", dto.RtspMain);
    }

    [Fact]
    public void From_ExposesHasCredentialsInsteadOfSecretRefs()
    {
        var dto = CameraDto.From(SampleCamera(), groupName: null);
        Assert.True(dto.HasCredentials);
    }

    [Fact]
    public void SerializedDto_LeaksNoSecret()
    {
        var dto = CameraDto.From(SampleCamera(), groupName: "Outdoor");
        var json = JsonSerializer.Serialize(dto);

        // No RTSP-embedded password, no secret-store key refs, no ref property names.
        Assert.DoesNotContain("s3cr3tPass", json);
        Assert.DoesNotContain("cam:", json);
        Assert.DoesNotContain("UsernameRef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordRef", json, StringComparison.OrdinalIgnoreCase);
    }
}
