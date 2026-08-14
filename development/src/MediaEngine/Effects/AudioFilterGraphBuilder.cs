using System.Globalization;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;

namespace VideoEditor.MediaEngine.Effects;

/// <summary>
/// Translates audio effect chains and volume into an FFmpeg filter string.
/// Audio kernels map to FFmpeg filters: "pitch" → asetrate+atempo,
/// "echo" → aecho, "gain" → volume. Used by preview snippets and export alike.
/// </summary>
public static class AudioFilterGraphBuilder
{
    /// <summary>
    /// Builds a comma-separated FFmpeg audio filter chain for one event.
    /// Returns an empty string when nothing needs processing.
    /// </summary>
    public static string BuildEventFilter(
        TimelineEvent evt, IEffectCatalog catalog, double trackVolume, int sampleRate)
    {
        var filters = new List<string>();

        if (Math.Abs(evt.PlaybackRate - 1.0) > 0.001)
            filters.AddRange(TempoChain(evt.PlaybackRate));

        foreach (var instance in evt.Effects)
        {
            if (!instance.Enabled) continue;
            if (catalog.Find(instance.Type) is not { } definition) continue;

            foreach (var step in definition.ResolveSteps(instance.Parameters))
                filters.AddRange(FiltersForKernel(step, sampleRate));
        }

        var volume = VolumeLimits.Clamp(evt.Muted ? 0 : evt.Volume) * VolumeLimits.Clamp(trackVolume);
        if (Math.Abs(volume - 1.0) > 0.001)
            filters.Add($"volume={Num(volume)}");

        if (evt.FadeInDuration > 0)
            filters.Add($"afade=t=in:st=0:d={Num(evt.FadeInDuration)}");
        if (evt.FadeOutDuration > 0)
        {
            var start = Math.Max(0, evt.Duration - evt.FadeOutDuration);
            filters.Add($"afade=t=out:st={Num(start)}:d={Num(evt.FadeOutDuration)}");
        }

        return string.Join(",", filters);
    }

    private static IEnumerable<string> FiltersForKernel(ResolvedEffectStep step, int sampleRate)
    {
        switch (step.Kernel.ToLowerInvariant())
        {
            case "pitch":
                var factor = Math.Clamp(step.Args.Get("pitch", 1.0), 0.25, 4.0);
                if (Math.Abs(factor - 1.0) < 0.001) yield break;
                // Raise/lower pitch by resampling, then restore the original speed.
                yield return $"asetrate={sampleRate}*{Num(factor)}";
                yield return $"aresample={sampleRate}";
                foreach (var tempo in TempoChain(1.0 / factor))
                    yield return tempo;
                break;

            case "echo":
                var delay = Math.Clamp(step.Args.Get("delay", 350), 1, 5000);
                var decay = Math.Clamp(step.Args.Get("decay", 0.45), 0.01, 0.99);
                yield return $"aecho=0.8:0.9:{Num(delay)}:{Num(decay)}";
                break;

            case "gain":
                yield return $"volume={Num(Math.Max(0, step.Args.Get("amount", 1.0)))}";
                break;

            // Visual kernels that appear in a mixed-target .vefx are simply skipped.
        }
    }

    /// <summary>
    /// FFmpeg's atempo only accepts 0.5–2.0, so larger changes are chained.
    /// </summary>
    public static IReadOnlyList<string> TempoChain(double tempo)
    {
        var filters = new List<string>();
        tempo = Math.Clamp(tempo, 0.05, 20.0);

        while (tempo > 2.0 + 1e-9)
        {
            filters.Add("atempo=2.0");
            tempo /= 2.0;
        }
        while (tempo < 0.5 - 1e-9)
        {
            filters.Add("atempo=0.5");
            tempo /= 0.5;
        }
        if (Math.Abs(tempo - 1.0) > 0.001)
            filters.Add($"atempo={Num(tempo)}");
        return filters;
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
