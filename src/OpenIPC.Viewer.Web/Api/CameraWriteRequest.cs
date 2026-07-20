using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Web.Api;

// The browser's create/update payload. Loose (all-optional) on the wire so bad
// input produces a 400 with reasons rather than a bind failure; TryValidate
// turns it into the strongly-typed Core request or a list of errors.
public sealed record CameraWriteRequest(
    string? Name,
    string? Host,
    int? HttpPort,
    int? OnvifPort,
    string? RtspMain,
    string? RtspSub,
    int? GroupId,
    string? Username,
    string? Password,
    string? StreamQuality)
{
    public bool TryValidate(out ValidatedCamera result, out List<string> errors)
    {
        errors = new List<string>();
        result = default!;

        var name = Name?.Trim() ?? "";
        if (name.Length == 0) errors.Add("name is required");

        var host = Host?.Trim() ?? "";
        if (host.Length == 0) errors.Add("host is required");

        var httpPort = HttpPort ?? 80;
        if (httpPort is < 1 or > 65535) errors.Add("httpPort must be 1..65535");

        if (OnvifPort is { } op && op is < 1 or > 65535)
            errors.Add("onvifPort must be 1..65535");

        Uri? rtspMain = null;
        if (string.IsNullOrWhiteSpace(RtspMain))
            errors.Add("rtspMain is required");
        else if (!Uri.TryCreate(RtspMain, UriKind.Absolute, out rtspMain))
            errors.Add("rtspMain must be an absolute URI");

        Uri? rtspSub = null;
        if (!string.IsNullOrWhiteSpace(RtspSub) && !Uri.TryCreate(RtspSub, UriKind.Absolute, out rtspSub))
            errors.Add("rtspSub must be an absolute URI");

        var quality = StreamQualityOverride.Auto;
        if (!string.IsNullOrWhiteSpace(StreamQuality)
            && !Enum.TryParse(StreamQuality, ignoreCase: true, out quality))
            errors.Add("streamQuality must be Auto, AlwaysHd, or AlwaysSd");

        // Credentials are all-or-nothing. Both empty => no credentials set/kept.
        CameraCredentials? credentials = null;
        var hasUser = !string.IsNullOrEmpty(Username);
        var hasPass = !string.IsNullOrEmpty(Password);
        if (hasUser ^ hasPass)
            errors.Add("username and password must be provided together");
        else if (hasUser && hasPass)
            credentials = new CameraCredentials(Username!, Password!);

        if (errors.Count > 0)
            return false;

        result = new ValidatedCamera(
            name, host, httpPort, OnvifPort, rtspMain!, rtspSub,
            GroupId is { } g ? new GroupId(g) : null, credentials, quality);
        return true;
    }
}

// Validated, strongly-typed form ready to become a NewCameraRequest /
// UpdateCameraRequest.
public sealed record ValidatedCamera(
    string Name,
    string Host,
    int HttpPort,
    int? OnvifPort,
    Uri RtspMain,
    Uri? RtspSub,
    GroupId? GroupId,
    CameraCredentials? Credentials,
    StreamQualityOverride StreamQuality)
{
    public NewCameraRequest ToNew() => new(
        Name, Host, HttpPort, OnvifPort, RtspMain, RtspSub, Credentials,
        GroupId, StreamQuality);

    public UpdateCameraRequest ToUpdate() => new(
        Name, Host, HttpPort, OnvifPort, RtspMain, RtspSub, Credentials,
        GroupId, StreamQuality);
}
