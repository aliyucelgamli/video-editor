namespace VideoEditor.Domain;

/// <summary>All media referenced by the project (VEGAS "Project Media" equivalent).</summary>
public class MediaLibrary
{
    public List<MediaItem> Items { get; set; } = new();

    public MediaItem? FindById(Guid id) => Items.FirstOrDefault(m => m.Id == id);
}
