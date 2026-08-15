namespace VideoEditor.Application.Editing;

/// <summary>
/// Pure math behind the visual transform editor (Unity-style gizmo): hit
/// testing the handles and turning mouse drags into scale/position values.
/// Everything works in project-pixel space; the view converts to screen
/// pixels. Matches FrameCompositor semantics: a layer is the full project
/// frame scaled around its center, then offset by Position (project pixels).
/// </summary>
public static class TransformGizmo
{
    public const double MinScale = 0.05;
    public const double MaxScale = 8.0;

    /// <summary>The layer rectangle a transform produces on the project canvas.</summary>
    public static GizmoRect RectFor(
        double scaleX, double scaleY, double positionX, double positionY,
        double projectWidth, double projectHeight)
    {
        var width = projectWidth * scaleX;
        var height = projectHeight * scaleY;
        var centerX = projectWidth / 2 + positionX;
        var centerY = projectHeight / 2 + positionY;
        return new GizmoRect(centerX - width / 2, centerY - height / 2, width, height);
    }

    /// <summary>
    /// What the pointer at (x, y) would grab: corners beat edges, edges beat
    /// the inner move area; null when outside the gizmo entirely.
    /// </summary>
    public static GizmoHandle? HitTest(GizmoRect rect, double x, double y, double radius)
    {
        bool Near(double px, double py) =>
            Math.Abs(x - px) <= radius && Math.Abs(y - py) <= radius;

        if (Near(rect.Left, rect.Top)) return GizmoHandle.TopLeft;
        if (Near(rect.Right, rect.Top)) return GizmoHandle.TopRight;
        if (Near(rect.Right, rect.Bottom)) return GizmoHandle.BottomRight;
        if (Near(rect.Left, rect.Bottom)) return GizmoHandle.BottomLeft;

        var withinX = x >= rect.Left - radius && x <= rect.Right + radius;
        var withinY = y >= rect.Top - radius && y <= rect.Bottom + radius;
        if (withinX && Math.Abs(y - rect.Top) <= radius) return GizmoHandle.Top;
        if (withinX && Math.Abs(y - rect.Bottom) <= radius) return GizmoHandle.Bottom;
        if (withinY && Math.Abs(x - rect.Left) <= radius) return GizmoHandle.Left;
        if (withinY && Math.Abs(x - rect.Right) <= radius) return GizmoHandle.Right;

        if (x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom)
            return GizmoHandle.Move;
        return null;
    }

    /// <summary>
    /// Applies a drag that started at the given transform. Scale handles keep
    /// the opposite edge/corner anchored (Unity-style); corner drags keep the
    /// current aspect ratio when <paramref name="keepAspect"/> is set.
    /// </summary>
    public static (double ScaleX, double ScaleY, double PositionX, double PositionY) Drag(
        double startScaleX, double startScaleY, double startPositionX, double startPositionY,
        GizmoHandle handle, double deltaX, double deltaY,
        double projectWidth, double projectHeight, bool keepAspect)
    {
        if (handle == GizmoHandle.Move)
            return (startScaleX, startScaleY,
                Math.Clamp(startPositionX + deltaX, -projectWidth, projectWidth),
                Math.Clamp(startPositionY + deltaY, -projectHeight, projectHeight));

        var rect = RectFor(startScaleX, startScaleY, startPositionX, startPositionY,
            projectWidth, projectHeight);
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        var movesLeft = handle is GizmoHandle.TopLeft or GizmoHandle.Left or GizmoHandle.BottomLeft;
        var movesRight = handle is GizmoHandle.TopRight or GizmoHandle.Right or GizmoHandle.BottomRight;
        var movesTop = handle is GizmoHandle.TopLeft or GizmoHandle.Top or GizmoHandle.TopRight;
        var movesBottom = handle is GizmoHandle.BottomLeft or GizmoHandle.Bottom or GizmoHandle.BottomRight;

        var minWidth = projectWidth * MinScale;
        var maxWidth = projectWidth * MaxScale;
        var minHeight = projectHeight * MinScale;
        var maxHeight = projectHeight * MaxScale;

        // The dragged edge moves; the opposite edge stays anchored.
        if (movesLeft) left = Math.Clamp(left + deltaX, right - maxWidth, right - minWidth);
        if (movesRight) right = Math.Clamp(right + deltaX, left + minWidth, left + maxWidth);
        if (movesTop) top = Math.Clamp(top + deltaY, bottom - maxHeight, bottom - minHeight);
        if (movesBottom) bottom = Math.Clamp(bottom + deltaY, top + minHeight, top + maxHeight);

        var isCorner = handle is GizmoHandle.TopLeft or GizmoHandle.TopRight
            or GizmoHandle.BottomLeft or GizmoHandle.BottomRight;
        if (keepAspect && isCorner && rect.Width > 0.001 && rect.Height > 0.001)
        {
            // The axis the user changed the most wins; both follow its factor.
            var widthFactor = (right - left) / rect.Width;
            var heightFactor = (bottom - top) / rect.Height;
            var factor = Math.Abs(widthFactor - 1) >= Math.Abs(heightFactor - 1)
                ? widthFactor
                : heightFactor;

            var safeStartX = Math.Max(0.01, startScaleX);
            var safeStartY = Math.Max(0.01, startScaleY);
            factor = Math.Clamp(factor,
                Math.Max(MinScale / safeStartX, MinScale / safeStartY),
                Math.Min(MaxScale / safeStartX, MaxScale / safeStartY));

            var width = rect.Width * factor;
            var height = rect.Height * factor;
            if (movesLeft) left = right - width;
            else right = left + width;
            if (movesTop) top = bottom - height;
            else bottom = top + height;
        }

        var positionX = (left + right) / 2 - projectWidth / 2;
        var positionY = (top + bottom) / 2 - projectHeight / 2;
        return ((right - left) / projectWidth,
            (bottom - top) / projectHeight,
            Math.Clamp(positionX, -projectWidth, projectWidth),
            Math.Clamp(positionY, -projectHeight, projectHeight));
    }

    /// <summary>Snaps an offset to 0 when close — centers the layer during move drags.</summary>
    public static double SnapOffset(double value, double threshold) =>
        Math.Abs(value) <= threshold ? 0 : value;

    /// <summary>
    /// Ctrl-snapping during a move drag: pulls the layer onto the project
    /// frame's edges and center lines. Per axis, the layer's near edge, center
    /// and far edge are matched against frame start / middle / end and the
    /// closest hit within <paramref name="threshold"/> wins. The result also
    /// says where to draw the alignment guides.
    /// </summary>
    public static SnapResult SnapToFrame(
        double scaleX, double scaleY, double positionX, double positionY,
        double projectWidth, double projectHeight, double threshold)
    {
        var rect = RectFor(scaleX, scaleY, positionX, positionY, projectWidth, projectHeight);

        var (deltaX, guideX) = BestSnap(threshold,
            (0 - rect.Left, 0),
            (projectWidth / 2 - rect.CenterX, projectWidth / 2),
            (projectWidth - rect.Right, projectWidth));
        var (deltaY, guideY) = BestSnap(threshold,
            (0 - rect.Top, 0),
            (projectHeight / 2 - rect.CenterY, projectHeight / 2),
            (projectHeight - rect.Bottom, projectHeight));

        return new SnapResult(positionX + deltaX, positionY + deltaY, guideX, guideY);
    }

    private static (double Delta, double? Guide) BestSnap(
        double threshold, params (double Delta, double Guide)[] candidates)
    {
        var best = 0.0;
        double? guide = null;
        var bestDistance = threshold;
        foreach (var (delta, line) in candidates)
        {
            if (Math.Abs(delta) > bestDistance) continue;
            bestDistance = Math.Abs(delta);
            best = delta;
            guide = line;
        }
        return (best, guide);
    }
}
