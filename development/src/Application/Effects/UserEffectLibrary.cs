using VideoEditor.Domain.Effects;

namespace VideoEditor.Application.Effects;

/// <summary>
/// Loads user effects (.vefx) from a folder (user/effects) into the catalog.
/// Import copies the file into that folder so effects survive restarts.
/// </summary>
public class UserEffectLibrary
{
    private readonly EffectCatalog _catalog;
    private readonly IEffectFileReader _reader;
    private readonly string _folder;

    public UserEffectLibrary(EffectCatalog catalog, IEffectFileReader reader, string folder)
    {
        _catalog = catalog;
        _reader = reader;
        _folder = folder;
    }

    public string Folder => _folder;

    /// <summary>Loads every .vefx in the user effects folder. Returns (loaded, failed file names).</summary>
    public (int Loaded, List<string> Failed) LoadAll()
    {
        var failed = new List<string>();
        if (!Directory.Exists(_folder)) return (0, failed);

        var loaded = 0;
        foreach (var path in Directory.EnumerateFiles(_folder, "*" + _reader.DefaultExtension))
        {
            if (TryLoad(path)) loaded++;
            else failed.Add(Path.GetFileName(path));
        }
        return (loaded, failed);
    }

    /// <summary>
    /// Imports an external .vefx file: validates it, copies it into the
    /// user effects folder and registers it in the catalog.
    /// </summary>
    public EffectDefinition Import(string sourcePath)
    {
        var definition = _reader.Load(sourcePath);

        Directory.CreateDirectory(_folder);
        var target = Path.Combine(_folder, Path.GetFileName(sourcePath));
        var isSameFile = string.Equals(
            Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        if (!isSameFile) File.Copy(sourcePath, target, overwrite: true);

        _catalog.RegisterUserEffect(definition);
        return definition;
    }

    private bool TryLoad(string path)
    {
        try
        {
            _catalog.RegisterUserEffect(_reader.Load(path));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
