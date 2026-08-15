using VideoEditor.Application.Commands;
using VideoEditor.Domain;

namespace VideoEditor.Application.Editing;

/// <summary>
/// Pure math behind edge trimming and slipping: clamps the requested timeline
/// geometry to the media's bounds and produces the matching undoable command.
/// The playback rate stays fixed; only the visible source range moves.
/// </summary>
public static class EdgeTrim
{
    /// <summary>Trim one edge; <paramref name="mediaDuration"/> null = no source limit.</summary>
    public static TrimEventCommand BuildTrim(
        TimelineEvent evt, double? mediaDuration, bool fromLeftEdge, double newStart, double newDuration)
    {
        var rate = evt.PlaybackRate <= 0 ? 1 : evt.PlaybackRate;

        if (fromLeftEdge)
        {
            var end = evt.End;
            // The left edge cannot extend past the source's first frame.
            var earliestStart = evt.Start - evt.SourceIn / rate;
            newStart = Math.Clamp(
                newStart, Math.Max(0, earliestStart), end - TrimEventCommand.MinDuration);
            return new TrimEventCommand(
                evt, newStart, end - newStart,
                evt.SourceIn + (newStart - evt.Start) * rate, evt.SourceOut);
        }

        newDuration = Math.Max(TrimEventCommand.MinDuration, newDuration);
        if (mediaDuration is { } limit)
            newDuration = Math.Min(
                newDuration, Math.Max(TrimEventCommand.MinDuration, (limit - evt.SourceIn) / rate));
        return new TrimEventCommand(
            evt, evt.Start, newDuration,
            evt.SourceIn, evt.SourceIn + newDuration * rate);
    }

    /// <summary>
    /// Slip by a timeline delta (drag right = show earlier footage). Null when
    /// the clamp leaves nothing to change.
    /// </summary>
    public static SlipEventCommand? BuildSlip(
        TimelineEvent evt, double? mediaDuration, double deltaSeconds)
    {
        var rate = evt.PlaybackRate <= 0 ? 1 : evt.PlaybackRate;
        var span = Math.Max(0, evt.SourceOut - evt.SourceIn);
        var newSourceIn = Math.Max(0, evt.SourceIn - deltaSeconds * rate);
        if (mediaDuration is { } limit)
            newSourceIn = Math.Min(newSourceIn, Math.Max(0, limit - span));

        return Math.Abs(newSourceIn - evt.SourceIn) < 0.001
            ? null
            : new SlipEventCommand(evt, newSourceIn, newSourceIn + span);
    }
}
