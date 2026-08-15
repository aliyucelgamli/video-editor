namespace VideoEditor.App.ViewModels;

/// <summary>A category block in the shortcuts editor (File, Edit, …).</summary>
public class ShortcutCategoryViewModel
{
    public ShortcutCategoryViewModel(string name, IReadOnlyList<ShortcutActionViewModel> actions)
    {
        Name = name;
        Actions = actions;
    }

    public string Name { get; }
    public IReadOnlyList<ShortcutActionViewModel> Actions { get; }
}
