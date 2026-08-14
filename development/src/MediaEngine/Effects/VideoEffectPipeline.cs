using VideoEditor.Domain;
using VideoEditor.Domain.Effects;

namespace VideoEditor.MediaEngine.Effects;

/// <summary>
/// Applies effect chains to raw BGRA frames by resolving each effect's kernel
/// pipeline through the catalog. Used identically by preview and export, so
/// what you see is what you render.
/// </summary>
public class VideoEffectPipeline
{
    /// <summary>
    /// Argument auto-injected into every kernel call: seconds since the clip
    /// started. Lets kernels animate (glitch, flicker…) while staying
    /// deterministic — the same time always renders the same frame.
    /// </summary>
    public const string TimeArg = "__time";

    private readonly Dictionary<string, IVideoKernel> _kernels;
    private readonly IEffectCatalog _catalog;

    public VideoEffectPipeline(IEffectCatalog catalog, IEnumerable<IVideoKernel>? kernels = null)
    {
        _catalog = catalog;
        _kernels = (kernels ?? CreateDefaultKernels())
            .ToDictionary(k => k.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<IVideoKernel> CreateDefaultKernels() => new IVideoKernel[]
    {
        new GrayscaleKernel(),
        new SepiaKernel(),
        new TemperatureKernel(),
        new BrightnessKernel(),
        new ContrastKernel(),
        new SaturationKernel(),
        new InvertKernel(),
        new BlurKernel(),
        new VignetteKernel(),
        new GlitchKernel()
    };

    public bool HasKernel(string key) => _kernels.ContainsKey(key);

    /// <summary>
    /// Applies every enabled effect of the chain to the frame, in order.
    /// <paramref name="timeSeconds"/> is injected as <see cref="TimeArg"/> so
    /// time-varying kernels can animate.
    /// </summary>
    public void Apply(
        byte[] bgra, int width, int height, IEnumerable<EffectInstance> chain, double timeSeconds = 0)
    {
        foreach (var instance in chain)
        {
            if (!instance.Enabled) continue;
            if (_catalog.Find(instance.Type) is not { } definition) continue;

            foreach (var step in definition.ResolveSteps(instance.Parameters))
            {
                if (!_kernels.TryGetValue(step.Kernel, out var kernel)) continue;
                // Unknown kernels (e.g. audio kernels on a visual chain) are skipped.

                var args = new Dictionary<string, double>(step.Args) { [TimeArg] = timeSeconds };
                kernel.Apply(bgra, width, height, args);
            }
        }
    }
}
