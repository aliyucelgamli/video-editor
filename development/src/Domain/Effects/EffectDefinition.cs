namespace VideoEditor.Domain.Effects;

/// <summary>
/// Describes an effect type: what it is called, what it can attach to,
/// which parameters the user can tweak and which kernels implement it.
/// Built-in effects and imported .vefx effects share this exact shape,
/// so the rest of the application never distinguishes between them.
/// </summary>
public class EffectDefinition
{
    /// <summary>Stable identifier stored in projects (e.g. "grayscale", "helium").</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string? Description { get; set; }
    public EffectTarget Targets { get; set; } = EffectTarget.Visual;

    public List<EffectParameterDefinition> Parameters { get; set; } = new();

    /// <summary>Processing steps, applied in order (a shader-like mini pipeline).</summary>
    public List<EffectStep> Steps { get; set; } = new();

    /// <summary>False for effects imported from .vefx files.</summary>
    public bool IsBuiltIn { get; set; }

    public bool CanApplyTo(MediaType mediaType) =>
        Targets.HasFlag(EffectTargets.ForMediaType(mediaType));

    /// <summary>Creates a new instance of this effect with default parameter values.</summary>
    public EffectInstance CreateInstance()
    {
        var instance = new EffectInstance { Type = Id };
        foreach (var parameter in Parameters)
            instance.Parameters[parameter.Key] = parameter.Default;
        return instance;
    }

    /// <summary>
    /// Resolves the pipeline for a concrete instance: instance parameter values
    /// (falling back to defaults) are substituted into each step's arguments.
    /// </summary>
    public IReadOnlyList<ResolvedEffectStep> ResolveSteps(IReadOnlyDictionary<string, double> instanceParameters)
    {
        var effective = new Dictionary<string, double>();
        foreach (var parameter in Parameters)
        {
            effective[parameter.Key] = instanceParameters.TryGetValue(parameter.Key, out var value)
                ? parameter.Clamp(value)
                : parameter.Default;
        }

        return Steps.Select(s => new ResolvedEffectStep(s.Kernel, s.ResolveArgs(effective))).ToList();
    }
}
