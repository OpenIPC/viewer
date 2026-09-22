using System;
using System.Collections.Generic;

namespace OpenIPC.Viewer.Devices.Onvif;

/// <summary>
/// Where a camera's Media or PTZ service might actually live, most trusted first.
/// GetCapabilities is supposed to say, but firmwares get it wrong: YooSee-family
/// cameras (A'Gold CAM-10, issue #67) shift every XAddr one section down, so
/// Media points at ptz_service and PTZ at deviceio_service; others advertise an
/// address the camera no longer has. Nothing here is trusted on its own —
/// <see cref="SoapOnvifClient"/> proves each candidate with a read-only call.
/// </summary>
public static class OnvifServiceCandidates
{
    public static IReadOnlyList<Uri> Media(Uri deviceService, Uri? advertised, IEnumerable<Uri> allAdvertised) =>
        Build(deviceService, advertised, allAdvertised, "media", "/onvif/media_service", "/onvif/Media");

    public static IReadOnlyList<Uri> Ptz(Uri deviceService, Uri? advertised, IEnumerable<Uri> allAdvertised) =>
        Build(deviceService, advertised, allAdvertised, "ptz", "/onvif/ptz_service", "/onvif/PTZ");

    // 1. the XAddr advertised for the service, as given;
    // 2. the same path at the address we actually reach the camera on;
    // 3. any other advertised XAddr whose path names the service (a shifted
    //    table still lists the right path, just under the wrong section);
    // 4. the conventional paths — gSOAP/Dahua-style first, then Hikvision-style;
    // 5. the device service itself, which some servers answer everything on.
    private static IReadOnlyList<Uri> Build(Uri deviceService, Uri? advertised, IEnumerable<Uri> allAdvertised,
        string pathHint, params string[] conventionalPaths)
    {
        var list = new List<Uri>();
        void Add(Uri uri)
        {
            if (!list.Contains(uri))
                list.Add(uri);
        }

        if (advertised is not null)
        {
            Add(advertised);
            Add(OnDevice(deviceService, advertised.AbsolutePath, advertised.Query));
        }
        foreach (var uri in allAdvertised)
        {
            if (uri.AbsolutePath.Contains(pathHint, StringComparison.OrdinalIgnoreCase))
                Add(OnDevice(deviceService, uri.AbsolutePath, uri.Query));
        }
        foreach (var path in conventionalPaths)
            Add(OnDevice(deviceService, path, string.Empty));
        Add(deviceService);
        return list;
    }

    private static Uri OnDevice(Uri deviceService, string path, string query) =>
        new UriBuilder(deviceService) { Path = path, Query = query.TrimStart('?') }.Uri;
}
