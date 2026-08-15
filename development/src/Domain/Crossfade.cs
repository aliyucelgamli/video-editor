namespace VideoEditor.Domain;

/// <summary>
/// Automatic crossfades: when two events on the same track overlap in time,
/// the earlier one fades out and the later one fades in across the overlap —
/// no manual fade setup needed. This helper computes those implicit fade
/// durations; video compositing and the audio mix both apply them, so the
/// picture and the sound crossfade identically.
/// </summary>
public static class Crossfade
{
    /// <summary>
    /// Implicit fade-in/fade-out durations forced onto an event by same-track
    /// overlaps. Events that start at exactly the same time are treated as
    /// stacked (no crossfade).
    /// </summary>
    public static (double FadeIn, double FadeOut) ImplicitFades(Track track, TimelineEvent evt)
    {
        double fadeIn = 0, fadeOut = 0;
        foreach (var other in track.Events)
        {
            if (other.Id == evt.Id) continue;

            var overlap = Math.Min(evt.End, other.End) - Math.Max(evt.Start, other.Start);
            if (overlap <= 0.001) continue;

            if (other.Start < evt.Start)
                fadeIn = Math.Max(fadeIn, Math.Min(other.End, evt.End) - evt.Start);
            else if (other.Start > evt.Start)
                fadeOut = Math.Max(fadeOut, evt.End - other.Start);
        }
        return (fadeIn, fadeOut);
    }
}
