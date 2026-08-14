namespace VideoEditor.Domain;

/// <summary>A horizontal lane on the timeline holding events.</summary>
public class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public TrackType Type { get; set; }
    public List<TimelineEvent> Events { get; set; } = new();

    public double Volume { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public List<EffectInstance> Effects { get; set; } = new();

    /// <summary>Optional parent track for grouped motion (later phase).</summary>
    public Guid? ParentId { get; set; }

    public TimelineEvent? FindEvent(Guid id) => Events.FirstOrDefault(e => e.Id == id);

    public void SortEvents() => Events.Sort((a, b) => a.Start.CompareTo(b.Start));
}
