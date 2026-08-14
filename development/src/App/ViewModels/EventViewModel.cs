using System.Windows.Media;
using VideoEditor.App.Mvvm;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>Visual projection of a timeline event.</summary>
public class EventViewModel : ObservableObject
{
    private bool _isSelected;

    public EventViewModel(TimelineEvent evt, double pixelsPerSecond, Brush brush)
    {
        Id = evt.Id;
        Name = evt.Name;
        X = evt.Start * pixelsPerSecond;
        Width = Math.Max(6, evt.Duration * pixelsPerSecond);
        Brush = brush;
        DurationLabel = $"{evt.Duration:0.#}s";
        ToolTip = $"{evt.Name}\n{evt.Start:0.##}s – {evt.End:0.##}s  ({evt.Duration:0.##}s)";
    }

    public Guid Id { get; }
    public string Name { get; }
    public double X { get; }
    public double Width { get; }
    public Brush Brush { get; }
    public string DurationLabel { get; }
    public string ToolTip { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
