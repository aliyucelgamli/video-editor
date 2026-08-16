using System.Text.Json.Serialization;

namespace VideoEditor.Domain;

/// <summary>
/// Root of the project model. Stores media references and edit state only —
/// source media files are never modified or copied (non-destructive editing).
/// </summary>
public class Project
{
    public ProjectSettings Settings { get; set; } = new();
    public MediaLibrary Media { get; set; } = new();
    public List<Track> Tracks { get; set; } = new();
    public List<Marker> Markers { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Export/loop region selected with the yellow start/end bars.
    /// Null means "no region": exports cover the whole project.
    /// </summary>
    public TimeRange? ExportRange { get; set; }

    [JsonIgnore]
    public double Duration => Tracks.SelectMany(t => t.Events).Select(e => e.End).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The span the clips actually occupy: from the earliest start to the latest
    /// end, across every track. Unlike <see cref="Duration"/> this also reports
    /// where the content BEGINS, which is what "fit the view to the content" and
    /// "select everything" both need. Null when the timeline is empty.
    /// </summary>
    public TimeRange? ContentExtent()
    {
        var events = Tracks.SelectMany(track => track.Events).ToList();
        if (events.Count == 0) return null;

        var start = events.Min(e => e.Start);
        var end = events.Max(e => e.End);
        return end - start <= 0 ? null : new TimeRange { Start = start, End = end };
    }

    public Track? FindTrack(Guid id) => Tracks.FirstOrDefault(t => t.Id == id);

    public (Track Track, TimelineEvent Event)? FindEvent(Guid eventId)
    {
        foreach (var track in Tracks)
        {
            var evt = track.FindEvent(eventId);
            if (evt != null) return (track, evt);
        }
        return null;
    }
}
