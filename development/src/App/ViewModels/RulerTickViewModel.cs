namespace VideoEditor.App.ViewModels;

/// <summary>A single labelled tick on the timeline ruler.</summary>
public class RulerTickViewModel
{
    public RulerTickViewModel(double x, string label)
    {
        X = x;
        Label = label;
    }

    public double X { get; }
    public string Label { get; }
}
