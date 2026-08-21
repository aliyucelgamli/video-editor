using VideoEditor.Domain;

namespace VideoEditor.App.Ui;

/// <summary>
/// The easing curves offered in the UI, in one place: the clip fade menus read
/// them as menu items, the sound editor as combo box entries.
/// </summary>
public static class EasingOptions
{
    public static readonly IReadOnlyList<(EasingType Type, string Label)> All = new[]
    {
        (EasingType.InOutSine, "Smooth (sine)"),
        (EasingType.Linear, "Linear"),
        (EasingType.InSine, "Ease in (sine)"),
        (EasingType.OutSine, "Ease out (sine)"),
        (EasingType.InOutQuad, "Smooth (quad)"),
        (EasingType.InOutCubic, "Smooth (cubic)"),
        (EasingType.InBack, "Back in (overshoot)"),
        (EasingType.OutBack, "Back out (overshoot)")
    };

    /// <summary>Labels only, for a plain string-bound ComboBox.</summary>
    public static IReadOnlyList<string> Labels { get; } = All.Select(o => o.Label).ToList();

    /// <summary>Position of a curve in the list; 0 (Linear) for anything unlisted.</summary>
    public static int IndexOf(EasingType type)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i].Type == type) return i;
        return 0;
    }

    public static EasingType At(int index) =>
        index >= 0 && index < All.Count ? All[index].Type : EasingType.Linear;
}
