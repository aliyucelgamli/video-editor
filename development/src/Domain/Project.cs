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

    [JsonIgnore]
    public double Duration => Tracks.SelectMany(t => t.Events).Select(e => e.End).DefaultIfEmpty(0).Max();

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
