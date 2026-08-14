using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

public partial class MainWindow : Window
{
    private const string MediaIdFormat = "VideoEditorMediaId";
    private const string EffectIdFormat = "VideoEditorEffectId";
    private const double DragThreshold = 4.0;

    private readonly MainViewModel _viewModel = new();

    // Library / effect list drag-out state
    private Point _dragStart;
    private MediaItemViewModel? _mediaDragCandidate;
    private EffectDefinitionViewModel? _effectDragCandidate;

    // Event move state
    private EventViewModel? _movingEvent;
    private Border? _movingEventBorder;
    private double _movingEventStartX;
    private double _movingEventNewStart;
    private bool _isDraggingEvent;

    // Ruler / lane scrub + yellow range bar drag state
    private enum RulerDragMode { None, Scrub, RangeStart, RangeEnd }
    private RulerDragMode _rulerDrag = RulerDragMode.None;
    private bool _isLaneScrubbing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.ZoomRequested += (_, factor) => ApplyZoom(factor, null);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.ConfirmDiscardChanges())
            e.Cancel = true;
        base.OnClosing(e);
    }

    // ---------- Media library: drag out + drop in ----------

    private void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _mediaDragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as MediaItemViewModel;
    }

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _mediaDragCandidate is null) return;
        if (!PassedDragThreshold(e.GetPosition(null))) return;

        var data = new DataObject(MediaIdFormat, _mediaDragCandidate.Id.ToString());
        _mediaDragCandidate = null;
        DragDrop.DoDragDrop(MediaList, data, DragDropEffects.Copy);
    }

    private void MediaList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is MediaItemViewModel item)
            _viewModel.AddMediaToTimelineEnd(item.Id);
    }

    private void MediaLibrary_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void MediaLibrary_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _viewModel.ImportFiles(paths);
        e.Handled = true;
    }

    // ---------- Effects list: drag out + double-click apply ----------

    private void EffectList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _effectDragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as EffectDefinitionViewModel;
    }

    private void EffectList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _effectDragCandidate is null) return;
        if (!PassedDragThreshold(e.GetPosition(null))) return;

        var data = new DataObject(EffectIdFormat, _effectDragCandidate.Id);
        _effectDragCandidate = null;
        DragDrop.DoDragDrop(EffectList, data, DragDropEffects.Copy);
    }

    private void EffectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is EffectDefinitionViewModel effect)
            _viewModel.Effects.ApplyEffectToSelection(effect.Id);
    }

    private bool PassedDragThreshold(Point position) =>
        Math.Abs(position.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(position.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;

    // ---------- Timeline lanes: drop targets, selection, scrubbing ----------

    private void Lane_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (LaneTrack(sender) is { } track)
        {
            if (e.Data.GetDataPresent(MediaIdFormat) &&
                Guid.TryParse(e.Data.GetData(MediaIdFormat) as string, out var mediaId))
            {
                if (_viewModel.CanDropMediaOnTrack(mediaId, track.Id))
                    e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
        }
        e.Handled = true;
    }

    private void Lane_Drop(object sender, DragEventArgs e)
    {
        if (LaneTrack(sender) is not { } track) return;

        var time = e.GetPosition((IInputElement)sender).X / _viewModel.PixelsPerSecond;

        if (e.Data.GetDataPresent(MediaIdFormat) &&
            Guid.TryParse(e.Data.GetData(MediaIdFormat) as string, out var mediaId))
            _viewModel.DropMediaOnTrack(mediaId, track.Id, time);
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _viewModel.DropFilesOnTrack(track.Id, paths, time);

        e.Handled = true;
    }

    private static TrackViewModel? LaneTrack(object sender) =>
        (sender as FrameworkElement)?.DataContext as TrackViewModel;

    /// <summary>Click on empty lane space: deselect, move the playhead there, allow drag-scrub.</summary>
    private void Lane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectEvent(null);
        if (sender is not FrameworkElement lane) return;

        SeekToPosition(e.GetPosition(lane).X);
        _isLaneScrubbing = lane.CaptureMouse();
        lane.MouseMove += Lane_ScrubMove;
        lane.MouseLeftButtonUp += Lane_ScrubEnd;
        lane.LostMouseCapture += Lane_ScrubLost;
    }

    private void Lane_ScrubMove(object sender, MouseEventArgs e)
    {
        if (!_isLaneScrubbing || e.LeftButton != MouseButtonState.Pressed) return;
        SeekToPosition(e.GetPosition((IInputElement)sender).X);
    }

    private void Lane_ScrubEnd(object sender, MouseButtonEventArgs e)
    {
        (sender as FrameworkElement)?.ReleaseMouseCapture();
    }

    private void Lane_ScrubLost(object sender, MouseEventArgs e)
    {
        _isLaneScrubbing = false;
        if (sender is not FrameworkElement lane) return;
        lane.MouseMove -= Lane_ScrubMove;
        lane.MouseLeftButtonUp -= Lane_ScrubEnd;
        lane.LostMouseCapture -= Lane_ScrubLost;
    }

    private void SeekToPosition(double x) =>
        _viewModel.SeekTo(Math.Max(0, x) / _viewModel.PixelsPerSecond);

    // ---------- Event blocks: select, drag-move, effect drop ----------

    private void EventBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not EventViewModel evt) return;

        _viewModel.SelectEvent(evt.Id);
        _movingEvent = evt;
        _movingEventBorder = border;
        _movingEventStartX = e.GetPosition(LanesScroll).X;
        _movingEventNewStart = evt.StartSeconds;
        _isDraggingEvent = false;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void EventBlock_MouseMove(object sender, MouseEventArgs e)
    {
        if (_movingEvent is null || _movingEventBorder is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var deltaX = e.GetPosition(LanesScroll).X - _movingEventStartX;
        if (!_isDraggingEvent && Math.Abs(deltaX) < DragThreshold) return;
        _isDraggingEvent = true;

        var pps = _viewModel.PixelsPerSecond;
        var desired = _movingEvent.StartSeconds + deltaX / pps;
        _movingEventNewStart = _viewModel.SnapTime(desired, _movingEvent.DurationSeconds, _movingEvent.Id);

        _movingEventBorder.RenderTransform =
            new TranslateTransform((_movingEventNewStart - _movingEvent.StartSeconds) * pps, 0);
        _viewModel.StatusText = $"Move to {_movingEventNewStart:0.##}s";
    }

    private void EventBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border) border.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EventBlock_LostMouseCapture(object sender, MouseEventArgs e)
    {
        var evt = _movingEvent;
        var wasDragging = _isDraggingEvent;
        var newStart = _movingEventNewStart;

        if (_movingEventBorder != null) _movingEventBorder.RenderTransform = null;
        _movingEvent = null;
        _movingEventBorder = null;
        _isDraggingEvent = false;

        if (evt != null && wasDragging)
            _viewModel.MoveEvent(evt.Id, newStart);
    }

    private void EventBlock_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(EffectIdFormat))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        // Other formats (files, media) bubble up to the lane handler.
    }

    private void EventBlock_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(EffectIdFormat)) return;
        if ((sender as FrameworkElement)?.DataContext is not EventViewModel evt) return;

        if (e.Data.GetData(EffectIdFormat) is string effectId)
            _viewModel.ApplyEffectToEvent(effectId, evt.Id);
        e.Handled = true;
    }

    // ---------- Ruler: scrub + yellow range bars ----------

    private void Ruler_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var x = e.GetPosition(RulerSurface).X;

        _rulerDrag = RulerDragMode.Scrub;
        if (_viewModel.HasRange)
        {
            if (Math.Abs(x - _viewModel.RangeStartX) <= 7) _rulerDrag = RulerDragMode.RangeStart;
            else if (Math.Abs(x - _viewModel.RangeEndX) <= 7) _rulerDrag = RulerDragMode.RangeEnd;
        }

        HandleRulerDrag(x);
        RulerSurface.CaptureMouse();
        e.Handled = true;
    }

    private void Ruler_MouseMove(object sender, MouseEventArgs e)
    {
        if (_rulerDrag == RulerDragMode.None || e.LeftButton != MouseButtonState.Pressed) return;
        HandleRulerDrag(e.GetPosition(RulerSurface).X);
    }

    private void Ruler_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RulerSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Ruler_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_rulerDrag is RulerDragMode.RangeStart or RulerDragMode.RangeEnd)
            _viewModel.CommitRangeDrag();
        _rulerDrag = RulerDragMode.None;
    }

    private void HandleRulerDrag(double x)
    {
        var pps = _viewModel.PixelsPerSecond;
        var time = Math.Max(0, x) / pps;

        switch (_rulerDrag)
        {
            case RulerDragMode.Scrub:
                _viewModel.SeekTo(time);
                break;
            case RulerDragMode.RangeStart:
                _viewModel.PreviewRangeDrag(time, _viewModel.RangeEndX / pps);
                break;
            case RulerDragMode.RangeEnd:
                _viewModel.PreviewRangeDrag(_viewModel.RangeStartX / pps, time);
                break;
        }
    }

    // ---------- Volume + effect parameter sliders (undo-friendly commits) ----------

    private void TrackVolume_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        (SliderContext<TrackViewModel>(sender))?.BeginVolumeEdit();

    private void TrackVolume_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        (SliderContext<TrackViewModel>(sender))?.EndVolumeEdit();

    private void TrackVolume_LostMouseCapture(object sender, MouseEventArgs e) =>
        (SliderContext<TrackViewModel>(sender))?.EndVolumeEdit();

    private void SelectedVolume_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        _viewModel.BeginSelectedVolumeEdit();

    private void SelectedVolume_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        _viewModel.EndSelectedVolumeEdit();

    private void SelectedVolume_LostMouseCapture(object sender, MouseEventArgs e) =>
        _viewModel.EndSelectedVolumeEdit();

    private void EffectParam_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        (SliderContext<EffectParameterViewModel>(sender))?.BeginEdit();

    private void EffectParam_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        (SliderContext<EffectParameterViewModel>(sender))?.EndEdit();

    private void EffectParam_LostMouseCapture(object sender, MouseEventArgs e) =>
        (SliderContext<EffectParameterViewModel>(sender))?.EndEdit();

    private static T? SliderContext<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T;

    // ---------- Scroll sync + zoom ----------

    private void LanesScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        HeadersScroll.ScrollToVerticalOffset(e.VerticalOffset);
        RulerScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
    }

    private void LanesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            LanesScroll.ScrollToVerticalOffset(LanesScroll.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
            return;
        }

        ApplyZoom(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(LanesScroll).X);
        e.Handled = true;
    }

    private void RulerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ApplyZoom(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(RulerScroll).X);
        e.Handled = true;
    }

    private void HeadersScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        LanesScroll.ScrollToVerticalOffset(LanesScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void ApplyZoom(double factor, double? anchorX)
    {
        var oldPps = _viewModel.PixelsPerSecond;
        var newPps = Math.Clamp(oldPps * factor,
            MainViewModel.MinPixelsPerSecond, MainViewModel.MaxPixelsPerSecond);
        if (Math.Abs(newPps - oldPps) < 0.001) return;

        var anchor = anchorX ?? LanesScroll.ViewportWidth / 2;
        var timeAtAnchor = (LanesScroll.HorizontalOffset + anchor) / oldPps;

        _viewModel.SetPixelsPerSecond(newPps);
        LanesScroll.ScrollToHorizontalOffset(Math.Max(0, timeAtAnchor * newPps - anchor));
    }
}
