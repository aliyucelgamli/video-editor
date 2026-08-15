using VideoEditor.Domain.Effects;

namespace VideoEditor.App.ViewModels;

/// <summary>One entry in the Effects panel (built-in or imported .vefx).</summary>
public class EffectDefinitionViewModel
{
    public EffectDefinitionViewModel(EffectDefinition definition)
    {
        Id = definition.Id;
        Name = definition.Name;
        Category = definition.Category;
        Description = definition.Description ?? string.Empty;

        var targets = new List<string>();
        if (definition.Targets.HasFlag(EffectTarget.Video)) targets.Add("Video");
        if (definition.Targets.HasFlag(EffectTarget.Audio)) targets.Add("Audio");
        if (definition.Targets.HasFlag(EffectTarget.Image)) targets.Add("Image");
        TargetLabel = string.Join(" · ", targets);

        // Segoe MDL2: movie / volume / picture.
        Glyph = definition.Targets.HasFlag(EffectTarget.Audio) && targets.Count == 1 ? "\uE767" : "\uE714";
        SourceLabel = definition.IsBuiltIn ? string.Empty : ".vefx";
        ToolTip = string.IsNullOrEmpty(Description)
            ? $"{Name} — {TargetLabel}"
            : $"{Name} — {TargetLabel}\n{Description}\n\nDrag it onto a clip, or click to preview it.";
    }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string Description { get; }
    public string TargetLabel { get; }
    public string SourceLabel { get; }
    public string Glyph { get; }
    public string ToolTip { get; }
}
