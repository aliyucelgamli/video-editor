using System.Globalization;

namespace VideoEditor.Domain.Effects;

/// <summary>
/// One processing step of an effect: a kernel (built-in processing routine,
/// e.g. "blur", "pitch") plus its arguments. Arguments are either numeric
/// literals ("0.5") or references to a user parameter ("$amount"),
/// which lets .vefx files compose kernels without any code.
/// </summary>
public class EffectStep
{
    public string Kernel { get; set; } = string.Empty;

    /// <summary>Argument map: value is a number literal or "$parameterKey".</summary>
    public Dictionary<string, string> Args { get; set; } = new();

    /// <summary>Resolves argument values against the given user parameter values.</summary>
    public Dictionary<string, double> ResolveArgs(IReadOnlyDictionary<string, double> parameters)
    {
        var resolved = new Dictionary<string, double>();
        foreach (var (key, raw) in Args)
        {
            if (raw.StartsWith('$'))
            {
                if (parameters.TryGetValue(raw[1..], out var value))
                    resolved[key] = value;
            }
            else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal))
            {
                resolved[key] = literal;
            }
        }
        return resolved;
    }
}

/// <summary>An <see cref="EffectStep"/> with its arguments fully resolved to numbers.</summary>
public record ResolvedEffectStep(string Kernel, IReadOnlyDictionary<string, double> Args);
