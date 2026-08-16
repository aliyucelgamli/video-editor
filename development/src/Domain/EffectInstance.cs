namespace VideoEditor.Domain;

/// <summary>
/// An effect applied at event, track or output level.
/// Plugin-ready: identified by a string type key, parameters are generic.
/// </summary>
public class EffectInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Effect type key, e.g. "brightness", "blur", "gain".</summary>
    public string Type { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Parameters { get; set; } = new();
    public List<KeyframeTrack> Keyframes { get; set; } = new();

    /// <summary>Independent copy with a new identity (clip copy/paste).</summary>
    public EffectInstance Clone() => new()
    {
        Type = Type,
        Enabled = Enabled,
        Parameters = new Dictionary<string, double>(Parameters),
        Keyframes = Keyframes.Select(track => track.Clone()).ToList()
    };
}
