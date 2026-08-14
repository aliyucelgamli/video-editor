using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

public class MediaItemViewModel
{
    public MediaItemViewModel(MediaItem item)
    {
        Name = item.Name;
        TypeLabel = item.Type.ToString();
        FilePath = item.FilePath;
    }

    public string Name { get; }
    public string TypeLabel { get; }
    public string FilePath { get; }
}
