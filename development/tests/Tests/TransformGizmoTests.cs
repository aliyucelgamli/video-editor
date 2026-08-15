using VideoEditor.Application.Editing;

namespace VideoEditor.Tests;

/// <summary>Math behind the visual transform editor (stage gizmo).</summary>
public static class TransformGizmoTests
{
    private const double W = 1920;
    private const double H = 1080;

    public static void Register()
    {
        TestRunner.Add("Gizmo: rect follows scale and position", () =>
        {
            var rect = TransformGizmo.RectFor(1, 1, 0, 0, W, H);
            Assert.Close(0, rect.Left, "identity left");
            Assert.Close(W, rect.Width, "identity width");

            rect = TransformGizmo.RectFor(0.5, 0.5, 100, -50, W, H);
            Assert.Close(W * 0.5, rect.Width, "half width");
            Assert.Close(W / 2 + 100, rect.CenterX, "center offset x");
            Assert.Close(H / 2 - 50, rect.CenterY, "center offset y");
        });

        TestRunner.Add("Gizmo: hit test finds corners, edges, move area and misses", () =>
        {
            var rect = TransformGizmo.RectFor(0.5, 0.5, 0, 0, W, H);

            Assert.Equal(GizmoHandle.TopLeft,
                TransformGizmo.HitTest(rect, rect.Left + 2, rect.Top - 2, 8), "corner grab");
            Assert.Equal(GizmoHandle.Right,
                TransformGizmo.HitTest(rect, rect.Right + 3, rect.CenterY, 8), "edge grab");
            Assert.Equal(GizmoHandle.Move,
                TransformGizmo.HitTest(rect, rect.CenterX, rect.CenterY, 8), "inside is move");
            Assert.True(
                TransformGizmo.HitTest(rect, rect.Left - 50, rect.Top - 50, 8) is null, "outside misses");
        });

        TestRunner.Add("Gizmo: move drag shifts position and clamps to bounds", () =>
        {
            var (sx, sy, px, py) = TransformGizmo.Drag(
                1, 1, 0, 0, GizmoHandle.Move, 120, -40, W, H, keepAspect: true);
            Assert.Close(1, sx, "move keeps scale x");
            Assert.Close(1, sy, "move keeps scale y");
            Assert.Close(120, px, "moved right");
            Assert.Close(-40, py, "moved up");

            var (_, _, clampedX, _) = TransformGizmo.Drag(
                1, 1, 0, 0, GizmoHandle.Move, 99999, 0, W, H, keepAspect: true);
            Assert.Close(W, clampedX, "position clamps to project width");
        });

        TestRunner.Add("Gizmo: edge drag stretches one axis, anchors the opposite edge", () =>
        {
            var before = TransformGizmo.RectFor(1, 1, 0, 0, W, H);
            var (sx, sy, px, py) = TransformGizmo.Drag(
                1, 1, 0, 0, GizmoHandle.Right, -W / 4, 0, W, H, keepAspect: true);

            Assert.Close(0.75, sx, "width shrank by a quarter", 1e-6);
            Assert.Close(1.0, sy, "height untouched by edge drag", 1e-6);
            var after = TransformGizmo.RectFor(sx, sy, px, py, W, H);
            Assert.Close(before.Left, after.Left, "left edge anchored", 1e-6);
        });

        TestRunner.Add("Gizmo: corner drag keeps aspect and anchors the opposite corner", () =>
        {
            var before = TransformGizmo.RectFor(1, 1, 0, 0, W, H);
            var (sx, sy, px, py) = TransformGizmo.Drag(
                1, 1, 0, 0, GizmoHandle.TopLeft, W / 4, 10, W, H, keepAspect: true);

            Assert.Close(sx, sy, "aspect locked", 1e-6);
            Assert.Close(0.75, sx, "dominant axis wins", 1e-6);
            var after = TransformGizmo.RectFor(sx, sy, px, py, W, H);
            Assert.Close(before.Right, after.Right, "right edge anchored", 1e-6);
            Assert.Close(before.Bottom, after.Bottom, "bottom edge anchored", 1e-6);

            // Without the lock the two axes scale independently.
            var (freeX, freeY, _, _) = TransformGizmo.Drag(
                1, 1, 0, 0, GizmoHandle.TopLeft, W / 4, 0, W, H, keepAspect: false);
            Assert.Close(0.75, freeX, "free corner: x follows", 1e-6);
            Assert.Close(1.0, freeY, "free corner: y stays", 1e-6);
        });

        TestRunner.Add("Gizmo: scaling clamps to min/max and snap centers small offsets", () =>
        {
            var (sx, _, _, _) = TransformGizmo.Drag(
                1, 1, 0, 0, GizmoHandle.Right, -W * 2, 0, W, H, keepAspect: false);
            Assert.Close(TransformGizmo.MinScale, sx, "cannot collapse below min scale", 1e-6);

            var (bigX, _, _, _) = TransformGizmo.Drag(
                1, 1, 0, 0, GizmoHandle.Right, W * 50, 0, W, H, keepAspect: false);
            Assert.Close(TransformGizmo.MaxScale, bigX, "cannot blow past max scale", 1e-6);

            Assert.Close(0, TransformGizmo.SnapOffset(5, 8), "snaps near center");
            Assert.Close(42, TransformGizmo.SnapOffset(42, 8), "keeps real offsets");
        });
    }
}
