using VideoEditor.Domain;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>
/// An effect rendered as if it were attached to an event, without touching
/// the project model — what the Effects panel shows while you browse the
/// catalog. Passed explicitly into the render call, so export can never pick
/// it up.
/// </summary>
/// <param name="EventId">The event to preview the effect on.</param>
/// <param name="Effect">The candidate effect instance (never stored in the model).</param>
public sealed record EffectPreview(Guid EventId, EffectInstance Effect);
