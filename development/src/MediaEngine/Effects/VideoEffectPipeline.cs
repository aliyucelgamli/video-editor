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
        new VignetteKernel()
    };

    public bool HasKernel(string key) => _kernels.ContainsKey(key);

    /// <summary>Applies every enabled effect of the chain to the frame, in order.</summary>
    public void Apply(byte[] bgra, int width, int height, IEnumerable<EffectInstance> chain)
    {
        foreach (var instance in chain)
        {
            if (!instance.Enabled) continue;
            if (_catalog.Find(instance.Type) is not { } definition) continue;

            foreach (var step in definition.ResolveSteps(instance.Parameters))
            {
                if (_kernels.TryGetValue(step.Kernel, out var kernel))
                    kernel.Apply(bgra, width, height, step.Args);
                // Unknown kernels (e.g. audio kernels on a visual chain) are skipped.
            }
        }
    }
}
