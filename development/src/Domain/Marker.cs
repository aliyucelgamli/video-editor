namespace VideoEditor.Domain;

/// <summary>A named point on the timeline.</summary>
public class Marker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public double Time { get; set; }
    public string Color { get; set; } = "#FFC107";
    public string? Comment { get; set; }
}
