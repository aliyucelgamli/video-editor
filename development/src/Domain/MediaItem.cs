namespace VideoEditor.Domain;

/// <summary>
/// A source media asset referenced by the project.
/// Editing NEVER modifies the file on disk (non-destructive editing).
/// Removing an item from the library only removes the reference.
/// </summary>
public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public MediaType Type { get; set; }

    /// <summary>Duration in seconds. Null when not probed yet (images have none).</summary>
    public double? DurationSeconds { get; set; }
    public long? FileSizeBytes { get; set; }

    /// <summary>Optional content hash, used later to relink missing media.</summary>
    public string? Hash { get; set; }

    public List<string> Tags { get; set; } = new();

    /// <summary>Optional bin/folder name inside the media library.</summary>
    public string? Bin { get; set; }
}
