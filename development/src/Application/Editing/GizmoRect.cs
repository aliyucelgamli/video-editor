namespace VideoEditor.Application.Editing;

/// <summary>A layer's bounds in project-pixel space (axis-aligned).</summary>
public readonly record struct GizmoRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
}
