namespace VideoEditor.Domain;

/// <summary>2D transform applied to a visual event (video / image / text).</summary>
public class Transform2D
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double ScaleX { get; set; } = 1.0;
    public double ScaleY { get; set; } = 1.0;

    /// <summary>Rotation in degrees around the anchor point.</summary>
    public double Rotation { get; set; }

    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;

    public double CropLeft { get; set; }
    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }

    public Transform2D Clone() => (Transform2D)MemberwiseClone();
}
