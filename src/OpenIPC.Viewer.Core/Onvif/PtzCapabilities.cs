using System;
using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Onvif;

// What a specific camera's PTZ node can actually do, read once from
// GetConfigurationOptions and GetServiceCapabilities.
//
// PTZ devices differ far more than the spec suggests: some implement only
// continuous velocity moves, some only relative steps; ranges are per-device;
// MoveStatus is optional and several popular brands report it but never update
// it. The UI asks this record what to show rather than offering buttons that
// quietly do nothing.
public sealed record PtzCapabilities(
    // Continuous and relative support, per axis pair: the spaces are declared
    // separately, and plenty of cameras have one without the other — a
    // pan/tilt-only dome, or a zoom-only box camera.
    bool SupportsContinuousPanTilt,
    bool SupportsContinuousZoom,
    bool SupportsRelativePanTilt,
    bool SupportsRelativeZoom,
    bool SupportsAbsolute,
    // Home, as the PTZ node itself reports it (GetNode/HomeSupported) — never
    // assumed. SetHome additionally requires the home position not to be
    // fixed-by-hardware.
    bool SupportsHome,
    bool SupportsSetHome,
    // GetStatus returns a MoveStatus this camera actually maintains. When
    // false, "has the move finished" can only be answered by waiting.
    bool SupportsMoveStatus,
    // Relative pan/tilt expressed as a fraction of the current field of view
    // (TranslationSpaceFov). This is what makes a fixed step feel the same at
    // wide and at full zoom — without it a step is in device units and gets
    // wilder the further in you are.
    bool RelativeIsFieldOfView,
    PtzRange RelativePan,
    PtzRange RelativeTilt,
    PtzRange RelativeZoom,
    PtzRange AbsoluteZoom,
    IReadOnlyList<string> AuxiliaryCommands)
{
    // What a camera that answered nothing useful gets: continuous only, the one
    // operation nearly every PTZ device implements, and what this app assumed
    // of every camera before it started asking.
    public static PtzCapabilities ContinuousOnly { get; } = new(
        SupportsContinuousPanTilt: true,
        SupportsContinuousZoom: true,
        SupportsRelativePanTilt: false,
        SupportsRelativeZoom: false,
        SupportsAbsolute: false,
        SupportsHome: false,
        SupportsSetHome: false,
        SupportsMoveStatus: false,
        RelativeIsFieldOfView: false,
        RelativePan: PtzRange.Normalized,
        RelativeTilt: PtzRange.Normalized,
        RelativeZoom: PtzRange.Normalized,
        AbsoluteZoom: PtzRange.Normalized,
        AuxiliaryCommands: Array.Empty<string>());

    // Whether a step on the axis pair can be served at all — by RelativeMove,
    // or by the timed continuous fallback. What the UI gates its keys on.
    public bool CanStepPanTilt => SupportsRelativePanTilt || SupportsContinuousPanTilt;

    public bool CanStepZoom => SupportsRelativeZoom || SupportsContinuousZoom;
}
