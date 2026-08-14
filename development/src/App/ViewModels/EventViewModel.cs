using System.Windows.Media;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>Read-only visual projection of a timeline event.</summary>
public class EventViewModel
{
    public EventViewModel(TimelineEvent evt, double pixelsPerSecond, Brush brush)
    {
        Name = evt.Name;
        X = evt.Start * pixelsPerSecond;
        Width = Math.Max(4, evt.Duration * pixelsPerSecond);
        Brush = brush;
        ToolTip = $"{evt.Name}\n{evt.Start:0.##}s – {evt.End:0.##}s";
    }

    public string Name { get; }
    public double X { get; }
    public double Width { get; }
    public Brush Brush { get; }
    public string ToolTip { get; }
}
