using VideoEditor.Domain.Effects;

namespace VideoEditor.Application.Effects;

/// <summary>
/// The effects that ship with the editor. Each is data only — the actual
/// processing lives in kernels (MediaEngine for video, FFmpeg filters for audio),
/// so a built-in effect and an imported .vefx effect are interchangeable.
/// </summary>
public static class BuiltInEffects
{
    public static IReadOnlyList<EffectDefinition> CreateAll() => new List<EffectDefinition>
    {
        // ---------- Video / image ----------
        Visual("grayscale", "Black & White", "Color",
            "Removes color; amount blends between original and full black & white.",
            Param("amount", "Amount", 0, 1, 1)),

        Visual("sepia", "Sepia", "Color",
            "Warm brown vintage tone.",
            Param("amount", "Amount", 0, 1, 1)),

        Visual("temperature", "Warm / Cold", "Color",
            "Shifts colors warmer (positive) or colder (negative).",
            Param("amount", "Temperature", -1, 1, 0.3)),

        Visual("brightness", "Brightness", "Color",
            "Lightens or darkens the image.",
            Param("amount", "Brightness", -1, 1, 0.15)),

        Visual("contrast", "Contrast", "Color",
            "Increases or decreases contrast.",
            Param("amount", "Contrast", -1, 1, 0.2)),

        Visual("saturation", "Saturation", "Color",
            "0 = grayscale, 1 = original, 2 = oversaturated.",
            Param("amount", "Saturation", 0, 2, 1.3)),

        Visual("invert", "Invert", "Stylize",
            "Inverts all colors (negative).",
            Param("amount", "Amount", 0, 1, 1)),

        Visual("blur", "Blur", "Stylize",
            "Softens the image with a gaussian-like blur.",
            Param("radius", "Radius", 0, 20, 4, "px")),

        Visual("vignette", "Vignette", "Stylize",
            "Darkens the corners for a cinematic frame.",
            Param("amount", "Amount", 0, 1, 0.5)),

        Visual("glitch", "Glitch", "Stylize",
            "Digital glitch: jumping band displacement with RGB splitting.",
            Param("amount", "Amount", 0, 1, 0.5),
            Param("speed", "Speed", 1, 30, 12, "hz")),

        // ---------- Audio ----------
        Audio("helium", "pitch", "Helium Voice", "Voice",
            "Raises the pitch like inhaled helium.",
            Param("pitch", "Pitch", 1.1, 2.5, 1.6, "x")),

        Audio("deep-voice", "pitch", "Deep Voice", "Voice",
            "Lowers the pitch for a deep, heavy voice.",
            Param("pitch", "Pitch", 0.4, 0.95, 0.7, "x")),

        Audio("echo", "echo", "Echo", "Space",
            "Adds a delayed reflection of the sound.",
            Param("delay", "Delay", 50, 1500, 350, "ms"),
            Param("decay", "Decay", 0.1, 0.9, 0.45)),

        Audio("gain", "gain", "Gain", "Level",
            "Boosts or cuts the signal level.",
            Param("amount", "Gain", 0, 4, 1.5, "x"))
    };

    private static EffectDefinition Visual(
        string id, string name, string category, string description,
        params EffectParameterDefinition[] parameters) =>
        Create(id, id, name, category, description, EffectTarget.Visual, parameters);

    private static EffectDefinition Audio(
        string id, string kernel, string name, string category, string description,
        params EffectParameterDefinition[] parameters) =>
        Create(id, kernel, name, category, description, EffectTarget.Audio, parameters);

    private static EffectDefinition Create(
        string id, string kernel, string name, string category, string description,
        EffectTarget targets, EffectParameterDefinition[] parameters)
    {
        // Built-in = a single kernel; every user parameter is passed straight
        // through to the kernel ("$key" reference).
        var step = new EffectStep { Kernel = kernel };
        foreach (var parameter in parameters)
            step.Args[parameter.Key] = "$" + parameter.Key;

        return new EffectDefinition
        {
            Id = id,
            Name = name,
            Category = category,
            Description = description,
            Targets = targets,
            Parameters = parameters.ToList(),
            Steps = { step },
            IsBuiltIn = true
        };
    }

    private static EffectParameterDefinition Param(
        string key, string label, double min, double max, double @default, string? unit = null) =>
        new() { Key = key, Label = label, Min = min, Max = max, Default = @default, Unit = unit };
}
