namespace VideoEditor.Application.Editing;

/// <summary>
/// Outcome of snapping a layer to the project frame: the adjusted position
/// plus the guide lines to draw (project-pixel coordinates, null = no snap
/// on that axis).
/// </summary>
public readonly record struct SnapResult(
    double PositionX, double PositionY, double? GuideX, double? GuideY);
