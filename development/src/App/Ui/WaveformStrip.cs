using System.Windows;
using System.Windows.Media;
using VideoEditor.Domain.Sound;

namespace VideoEditor.App.Ui;

/// <summary>
/// Draws a <see cref="SoundEditSession"/> as a waveform in OUTPUT time: the
/// pieces laid end to end, each one shaded with its own level and fade
/// envelope, plus the piece boundaries, the yellow selection and the playhead.
/// Direct <see cref="OnRender"/> drawing rather than a tree of shapes, so a
/// 100 000-sample file still repaints in one pass while a selection is dragged.
/// The host sets the properties and calls <see cref="Refresh"/>.
/// </summary>
public sealed class WaveformStrip : FrameworkElement
{
    /// <summary>Vertical breathing room so the loudest peak is not clipped.</summary>
    private const double VerticalPadding = 8.0;

    private static readonly Brush Background = Frozen(0xFF17181D);
    private static readonly Brush Wave = Frozen(0xFF43A06A);
    private static readonly Brush WaveSelected = Frozen(0xFF62D394);
    private static readonly Brush WaveMuted = Frozen(0xFF3A4148);
    private static readonly Brush SelectionFill = Frozen(0x33FFD54F);
    private static readonly Pen CenterPen = FrozenPen(0xFF2C303A, 1);
    private static readonly Pen BoundaryPen = FrozenPen(0xFF8F96A3, 1, dashed: true);
    private static readonly Pen SelectionPen = FrozenPen(0xFFFFD54F, 1);
    private static readonly Pen PlayheadPen = FrozenPen(0xFFE05252, 1.4);
    private static readonly Pen FadePen = FrozenPen(0xAAFFD54F, 1);

    private SoundEditSession? _session;
    private float[]? _peaks;
    private int _peaksPerSecond = 50;
    private double _selectionStart;
    private double _selectionEnd;
    private double _playheadTime;
    private Guid? _selectedSegmentId;

    /// <summary>The clip being drawn (null paints an empty strip).</summary>
    public SoundEditSession? Session
    {
        get => _session;
        set { _session = value; Refresh(); }
    }

    /// <summary>Peak samples of the SOURCE file, <see cref="PeaksPerSecond"/> per second.</summary>
    public float[]? Peaks
    {
        get => _peaks;
        set { _peaks = value; Refresh(); }
    }

    public int PeaksPerSecond
    {
        get => _peaksPerSecond;
        set { _peaksPerSecond = Math.Max(1, value); Refresh(); }
    }

    /// <summary>Selected span in output seconds; equal values mean no selection.</summary>
    public double SelectionStart
    {
        get => _selectionStart;
        set { _selectionStart = value; Refresh(); }
    }

    public double SelectionEnd
    {
        get => _selectionEnd;
        set { _selectionEnd = value; Refresh(); }
    }

    public double PlayheadTime
    {
        get => _playheadTime;
        set { _playheadTime = value; Refresh(); }
    }

    /// <summary>The piece highlighted because it is being edited.</summary>
    public Guid? SelectedSegmentId
    {
        get => _selectedSegmentId;
        set { _selectedSegmentId = value; Refresh(); }
    }

    /// <summary>Repaints on the next layout pass.</summary>
    public void Refresh() => InvalidateVisual();

    /// <summary>Output seconds at a horizontal pixel offset (mouse → time).</summary>
    public double TimeAt(double x)
    {
        var total = _session?.OutputDuration ?? 0;
        if (total <= 0 || ActualWidth <= 0) return 0;
        return Math.Clamp(x / ActualWidth * total, 0, total);
    }

    /// <summary>Horizontal pixel offset of an output time (time → mouse).</summary>
    public double XAt(double outputTime)
    {
        var total = _session?.OutputDuration ?? 0;
        if (total <= 0) return 0;
        return Math.Clamp(outputTime / total, 0, 1) * ActualWidth;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Also what makes the element hit-testable, so the host gets mouse events.
        drawingContext.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        var middle = height / 2;
        drawingContext.DrawLine(CenterPen, new Point(0, middle), new Point(width, middle));

        if (_session is not { Segments.Count: > 0 } session) return;
        var total = session.OutputDuration;
        if (total <= 0) return;

        var amplitude = Math.Max(2, middle - VerticalPadding);
        var cursor = 0.0;

        foreach (var segment in session.Segments)
        {
            var left = cursor / total * width;
            var right = (cursor + segment.Duration) / total * width;
            cursor += segment.Duration;

            DrawSegment(drawingContext, segment, left, right, middle, amplitude);

            // Boundaries between pieces, but not the outer edges of the clip.
            if (cursor < total - 1e-6)
                drawingContext.DrawLine(BoundaryPen, new Point(right, 0), new Point(right, height));
        }

        DrawSelection(drawingContext, width, height);

        var playheadX = Math.Clamp(_playheadTime / total, 0, 1) * width;
        drawingContext.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, height));
    }

    private void DrawSegment(
        DrawingContext drawingContext, SoundSegment segment,
        double left, double right, double middle, double amplitude)
    {
        var span = right - left;
        if (span < 0.5) return;

        var fill = segment.Muted
            ? WaveMuted
            : segment.Id == _selectedSegmentId ? WaveSelected : Wave;

        var gain = Math.Min(1.0, segment.Muted ? 0.15 : Math.Max(0.02, segment.Gain));
        var steps = Math.Clamp((int)span, 2, 4000);
        var duration = segment.Duration;

        // One closed shape: top edge left→right, bottom edge right→left. The
        // fade envelope is traced alongside it so the drawn shape and the
        // exported afade curve are visibly the same thing.
        var geometry = new StreamGeometry();
        var envelope = new List<Point>(steps + 1);
        using (var context = geometry.Open())
        {
            var top = new Point[steps + 1];
            var bottom = new Point[steps + 1];

            for (var i = 0; i <= steps; i++)
            {
                var fraction = (double)i / steps;
                var localTime = duration * fraction;
                var peak = PeakAt(segment.SourceIn + localTime);
                var envelopeFactor = segment.FadeFactorAt(localTime);
                var y = Math.Max(0.6, peak * amplitude * gain * envelopeFactor);

                var x = left + span * fraction;
                top[i] = new Point(x, middle - y);
                bottom[steps - i] = new Point(x, middle + y);
                if (segment.FadeIn > 0 || segment.FadeOut > 0)
                    envelope.Add(new Point(x, middle - amplitude * gain * envelopeFactor));
            }

            context.BeginFigure(top[0], isFilled: true, isClosed: true);
            context.PolyLineTo(top, isStroked: false, isSmoothJoin: false);
            context.PolyLineTo(bottom, isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(fill, null, geometry);

        if (envelope.Count > 1) DrawPolyline(drawingContext, envelope, FadePen);
    }

    private void DrawSelection(DrawingContext drawingContext, double width, double height)
    {
        var from = Math.Min(_selectionStart, _selectionEnd);
        var to = Math.Max(_selectionStart, _selectionEnd);
        if (to - from <= 1e-6) return;

        var left = XAt(from);
        var right = XAt(to);
        if (right - left < 1) right = left + 1;

        drawingContext.DrawRectangle(SelectionFill, null, new Rect(left, 0, right - left, height));
        drawingContext.DrawLine(SelectionPen, new Point(left, 0), new Point(left, height));
        drawingContext.DrawLine(SelectionPen, new Point(right, 0), new Point(right, height));
    }

    /// <summary>Normalized peak at a source offset; 0 when the file has no peaks yet.</summary>
    private float PeakAt(double sourceSeconds)
    {
        if (_peaks is not { Length: > 0 } peaks) return 0f;
        var index = (int)(sourceSeconds * _peaksPerSecond);
        return index >= 0 && index < peaks.Length ? peaks[index] : 0f;
    }

    private static void DrawPolyline(DrawingContext drawingContext, List<Point> points, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            context.PolyLineTo(points.GetRange(1, points.Count - 1), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Brush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(uint argb, double thickness, bool dashed = false)
    {
        var pen = new Pen(Frozen(argb), thickness);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        pen.Freeze();
        return pen;
    }
}
