using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Snapshots;

public enum AnnotationKind
{
    Arrow = 0,
    Rectangle = 1,
    Text = 2,
}

/// <summary>
/// One annotation drawn over a snapshot. Coordinates are normalized to the
/// full (pre-crop) image: 0..1 on each axis, so the edit is resolution-independent.
/// <see cref="Thickness"/> is also normalized (fraction of the image's longest
/// side); the renderer scales it to pixels. <see cref="Text"/> is set only for
/// <see cref="AnnotationKind.Text"/>.
/// </summary>
public sealed record SnapshotAnnotation(
    AnnotationKind Kind,
    double X1, double Y1, double X2, double Y2,
    uint ColorArgb,
    double Thickness,
    string? Text);

/// <summary>
/// A non-destructive edit applied to a snapshot to produce a saved copy. An
/// optional normalized crop rectangle (0..1) plus a list of annotations. The
/// renderer draws annotations onto the full image, then crops.
/// </summary>
public sealed record SnapshotEdit(
    double? CropX,
    double? CropY,
    double? CropW,
    double? CropH,
    IReadOnlyList<SnapshotAnnotation> Annotations);
