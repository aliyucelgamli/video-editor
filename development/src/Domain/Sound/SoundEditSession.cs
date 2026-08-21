namespace VideoEditor.Domain.Sound;

/// <summary>
/// A sound-editor working set: one source file cut into ordered
/// <see cref="SoundSegment"/> pieces, plus a master effect chain and level.
/// Pure model — the source file is never touched, and nothing here knows
/// about FFmpeg or WPF, so every edit operation is unit-testable.
///
/// Two clocks matter and the API keeps them apart:
/// <list type="bullet">
///   <item>SOURCE time — an offset inside the file (what a segment stores).</item>
///   <item>OUTPUT time — an offset inside the edited result, i.e. the pieces
///         laid end to end (what the waveform view and the playhead use).</item>
/// </list>
/// </summary>
public sealed class SoundEditSession
{
    /// <summary>Splits and range edits closer together than this are ignored.</summary>
    public const double MinSegmentDuration = 0.01;

    public string SourcePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Full length of the source file in seconds (0 when unknown).</summary>
    public double SourceDuration { get; set; }

    public List<SoundSegment> Segments { get; set; } = new();

    /// <summary>Master audio effect chain, applied after the pieces are joined.</summary>
    public List<EffectInstance> Effects { get; set; } = new();

    /// <summary>Master level multiplier applied after the effect chain.</summary>
    public double MasterGain { get; set; } = VolumeLimits.Default;

    /// <summary>Length of the edited result.</summary>
    public double OutputDuration => Segments.Sum(s => s.Duration);

    public bool IsEmpty => Segments.Count == 0 || OutputDuration <= MinSegmentDuration;

    /// <summary>A fresh session holding the whole file as one piece.</summary>
    public static SoundEditSession ForFile(string path, string name, double durationSeconds)
    {
        var duration = Math.Max(MinSegmentDuration, durationSeconds);
        return new SoundEditSession
        {
            SourcePath = path,
            Name = name,
            SourceDuration = duration,
            Segments = { new SoundSegment { SourceIn = 0, SourceOut = duration } }
        };
    }

    // ---------- Output ↔ source mapping ----------

    /// <summary>Output-time offset at which <paramref name="segment"/> starts.</summary>
    public double StartOf(SoundSegment segment)
    {
        var start = 0.0;
        foreach (var candidate in Segments)
        {
            if (ReferenceEquals(candidate, segment)) return start;
            start += candidate.Duration;
        }
        return start;
    }

    /// <summary>
    /// The piece playing at <paramref name="outputTime"/> and how far into it we
    /// are. Null past the end. The boundary belongs to the later piece, so
    /// splitting twice at the same spot cannot produce an empty segment.
    /// </summary>
    public (SoundSegment Segment, double LocalTime)? Locate(double outputTime)
    {
        if (outputTime < 0) return null;
        var start = 0.0;
        foreach (var segment in Segments)
        {
            var end = start + segment.Duration;
            if (outputTime < end) return (segment, outputTime - start);
            start = end;
        }
        return null;
    }

    /// <summary>
    /// Source-time offset that <paramref name="outputTime"/> maps to. Past the
    /// end that is where the LAST PIECE stops, not where the file stops — after
    /// a trim those are different points.
    /// </summary>
    public double ToSourceTime(double outputTime)
    {
        if (Locate(outputTime) is { } hit) return hit.Segment.SourceIn + hit.LocalTime;
        return Segments.Count > 0 ? Segments[^1].SourceOut : SourceDuration;
    }

    // ---------- Edits ----------

    /// <summary>
    /// Cuts the piece under <paramref name="outputTime"/> in two. False when the
    /// split would leave a sliver on either side (or lands past the end).
    /// </summary>
    public bool SplitAt(double outputTime)
    {
        if (Locate(outputTime) is not { } hit) return false;
        var (segment, localTime) = hit;
        if (localTime < MinSegmentDuration || segment.Duration - localTime < MinSegmentDuration) return false;

        var boundary = segment.SourceIn + localTime;
        var tail = segment.Copy();
        tail.Id = Guid.NewGuid();
        tail.SourceIn = boundary;
        tail.FadeIn = 0; // the cut is not a fade — each half keeps the outer one

        segment.SourceOut = boundary;
        segment.FadeOut = 0;
        segment.ClampFades();
        tail.ClampFades();

        Segments.Insert(Segments.IndexOf(segment) + 1, tail);
        return true;
    }

    /// <summary>Drops one piece. False when the id is unknown.</summary>
    public bool RemoveSegment(Guid id)
    {
        var segment = Segments.FirstOrDefault(s => s.Id == id);
        return segment != null && Segments.Remove(segment);
    }

    /// <summary>
    /// Cuts [start, end) out of the result and closes the gap — the sound
    /// editor's "delete selection". Splits at both edges first, so a range in
    /// the middle of a piece works without any special cases.
    /// </summary>
    public bool RemoveRange(double outputStart, double outputEnd)
    {
        if (!NormalizeRange(ref outputStart, ref outputEnd)) return false;

        SplitAt(outputEnd);
        SplitAt(outputStart);

        var kept = new List<SoundSegment>();
        var cursor = 0.0;
        var removed = false;
        foreach (var segment in Segments)
        {
            var end = cursor + segment.Duration;
            // Compare midpoints: after the two splits every piece is either
            // fully inside the range or fully outside it.
            var middle = (cursor + end) / 2;
            if (middle > outputStart && middle < outputEnd) removed = true;
            else kept.Add(segment);
            cursor = end;
        }

        if (!removed) return false;
        Segments = kept;
        return true;
    }

    /// <summary>Keeps only [start, end) — the "trim to selection" operation.</summary>
    public bool TrimTo(double outputStart, double outputEnd)
    {
        if (!NormalizeRange(ref outputStart, ref outputEnd)) return false;

        var total = OutputDuration;
        var trimmedTail = outputEnd < total - MinSegmentDuration && RemoveRange(outputEnd, total);
        var trimmedHead = outputStart > MinSegmentDuration && RemoveRange(0, outputStart);
        return trimmedTail || trimmedHead;
    }

    /// <summary>Moves a piece up or down the running order.</summary>
    public bool MoveSegment(Guid id, int delta)
    {
        var segment = Segments.FirstOrDefault(s => s.Id == id);
        if (segment is null || delta == 0) return false;

        var from = Segments.IndexOf(segment);
        var to = Math.Clamp(from + delta, 0, Segments.Count - 1);
        if (to == from) return false;

        Segments.RemoveAt(from);
        Segments.Insert(to, segment);
        return true;
    }

    /// <summary>Back to the whole file as one untouched piece.</summary>
    public void Reset()
    {
        Segments = new List<SoundSegment>
        {
            new() { SourceIn = 0, SourceOut = Math.Max(MinSegmentDuration, SourceDuration) }
        };
    }

    /// <summary>Deep copy with identities preserved — the undo snapshot.</summary>
    public SoundEditSession Copy() => new()
    {
        SourcePath = SourcePath,
        Name = Name,
        SourceDuration = SourceDuration,
        MasterGain = MasterGain,
        Segments = Segments.Select(s => s.Copy()).ToList(),
        Effects = Effects.Select(CopyEffect).ToList()
    };

    /// <summary>
    /// <see cref="EffectInstance.Clone"/> re-identifies, which would break the
    /// selected-effect binding across an undo, so snapshots copy in place.
    /// </summary>
    private static EffectInstance CopyEffect(EffectInstance instance) => new()
    {
        Id = instance.Id,
        Type = instance.Type,
        Enabled = instance.Enabled,
        Parameters = new Dictionary<string, double>(instance.Parameters)
    };

    /// <summary>Orders and clamps a range to the result; false when it is empty.</summary>
    private bool NormalizeRange(ref double start, ref double end)
    {
        if (end < start) (start, end) = (end, start);
        var total = OutputDuration;
        start = Math.Clamp(start, 0, total);
        end = Math.Clamp(end, 0, total);
        return end - start >= MinSegmentDuration;
    }
}
