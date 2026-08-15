using VideoEditor.App.Mvvm;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>One clip row in the Layers window (top of the stack first).</summary>
public class LayerItemViewModel : ObservableObject
{
    private int _layer;

    public LayerItemViewModel(TimelineEvent evt, Track track)
    {
        EventId = evt.Id;
        Name = evt.Name;
        TrackName = track.Name;
        StartSeconds = evt.Start;
        _layer = evt.Layer;
        TrackLayer = track.Layer;

        Kind = evt.Text != null ? "Text" : track.Type == TrackType.Overlay ? "Overlay" : "Media";
        // Segoe MDL2: text / picture-ish glyphs.
        Glyph = evt.Text != null ? "\uE8D2" : "\uE714";
        TimeLabel = $"{evt.Start:0.##}s – {evt.End:0.##}s";
    }

    public Guid EventId { get; }
    public string Name { get; }
    public string TrackName { get; }
    public string Kind { get; }
    public string Glyph { get; }
    public string TimeLabel { get; }
    public double StartSeconds { get; }
    public int TrackLayer { get; }

    /// <summary>The clip's own layer (what the user edits here).</summary>
    public int Layer
    {
        get => _layer;
        set
        {
            if (SetProperty(ref _layer, value))
            {
                OnPropertyChanged(nameof(EffectiveLayer));
                OnPropertyChanged(nameof(LayerLabel));
            }
        }
    }

    /// <summary>What actually decides the stacking: track layer + clip layer.</summary>
    public int EffectiveLayer => TrackLayer + _layer;

    public string LayerLabel => TrackLayer == 0
        ? _layer.ToString()
        : $"{_layer}  (+{TrackLayer} track = {EffectiveLayer})";
}
