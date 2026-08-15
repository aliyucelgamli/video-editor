using System.Text.Json.Serialization;

namespace VideoEditor.Domain;

/// <summary>
/// An instance of a media item placed on the timeline (VEGAS "Event").
/// A media file can appear as many events; editing an event never touches the source file.
/// </summary>
public class TimelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Source media reference. Guid.Empty for generated content (e.g. text).</summary>
    public Guid MediaId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Timeline start position in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Duration on the timeline in seconds.</summary>
    public double Duration { get; set; }

    [JsonIgnore]
    public double End => Start + Duration;

    /// <summary>Where playback begins inside the source media (seconds).</summary>
    public double SourceIn { get; set; }

    /// <summary>Where playback ends inside the source media (seconds).</summary>
    public double SourceOut { get; set; }

    public double PlaybackRate { get; set; } = 1.0;

    public double FadeInDuration { get; set; }
    public double FadeOutDuration { get; set; }
    public EasingType FadeInEasing { get; set; } = EasingType.InOutSine;
    public EasingType FadeOutEasing { get; set; } = EasingType.InOutSine;

    /// <summary>Audio gain, 0.0–2.0 (0%–200%). 1.0 = original level.</summary>
    public double Volume { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public bool Muted { get; set; }

    /// <summary>Set for generated text (title) events; null for media events.</summary>
    public TextStyle? Text { get; set; }

    public Transform2D Transform { get; set; } = new();
    public List<EffectInstance> Effects { get; set; } = new();
    public List<KeyframeTrack> Keyframes { get; set; } = new();

    /// <summary>
    /// Linked companion event (e.g. the audio event of a video clip).
    /// Linked events move/split together until unlinked (T shortcut).
    /// </summary>
    public Guid? LinkedEventId { get; set; }

    public bool Contains(double time) => time >= Start && time < End;
}
