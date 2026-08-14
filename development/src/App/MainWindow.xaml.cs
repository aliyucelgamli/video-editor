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

    // Event move / stretch state
    private enum EventDragMode { None, Move, StretchLeft, StretchRight }
    private const double EdgeZonePx = 8.0;
    private EventDragMode _eventDragMode = EventDragMode.None;
    private EventViewModel? _movingEvent;
    private Border? _movingEventBorder;
    private Border? _linkedEventBorder;
    private double _movingEventStartX;
    private double _movingEventNewStart;
    private double _stretchNewDuration;
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

        // Move the playhead to the clicked frame so the preview shows the clip
        // you are standing on (clicking empty lane space already did this).
        var clickedTime = evt.StartSeconds + e.GetPosition(border).X / _viewModel.PixelsPerSecond;
        _viewModel.SeekTo(clickedTime);

        _movingEvent = evt;
        _movingEventBorder = border;
        _linkedEventBorder = evt.LinkedEventId is Guid linkedId ? FindEventBorder(linkedId) : null;
        _movingEventStartX = e.GetPosition(LanesScroll).X;
        _movingEventNewStart = evt.StartSeconds;
        _stretchNewDuration = evt.DurationSeconds;
        _isDraggingEvent = false;
        _eventDragMode = DetectDragMode(border, e.GetPosition(border).X);

        border.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// Finds the on-screen block of another event (the linked A/V partner), so
    /// drag feedback can move the pair together instead of only the grabbed clip.
    /// </summary>
    private Border? FindEventBorder(Guid eventId)
    {
        return FindInTree(LanesScroll);

        Border? FindInTree(DependencyObject parent)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Border border &&
                    border.DataContext is EventViewModel evt &&
                    evt.Id == eventId)
                    return border;
                if (FindInTree(child) is { } found) return found;
            }
            return null;
        }
    }

    /// <summary>Shift near an edge = time stretch (VEGAS-style); anywhere else = move.</summary>
    private static EventDragMode DetectDragMode(Border border, double localX)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return EventDragMode.Move;
        if (localX <= EdgeZonePx) return EventDragMode.StretchLeft;
        if (localX >= border.ActualWidth - EdgeZonePx) return EventDragMode.StretchRight;
        return EventDragMode.Move;
    }

    private void EventBlock_MouseMove(object sender, MouseEventArgs e)
    {
        if (_movingEvent is null || _movingEventBorder is null)
        {
            UpdateEventCursor(sender, e);
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var deltaX = e.GetPosition(LanesScroll).X - _movingEventStartX;
        if (!_isDraggingEvent && Math.Abs(deltaX) < DragThreshold) return;
        _isDraggingEvent = true;

        switch (_eventDragMode)
        {
            case EventDragMode.Move: DragMove(deltaX); break;
            case EventDragMode.StretchRight: DragStretch(deltaX, fromLeftEdge: false); break;
            case EventDragMode.StretchLeft: DragStretch(deltaX, fromLeftEdge: true); break;
        }
    }

    private void DragMove(double deltaX)
    {
        var pps = _viewModel.PixelsPerSecond;
        var desired = _movingEvent!.StartSeconds + deltaX / pps;
        _movingEventNewStart = _viewModel.SnapTime(desired, _movingEvent.DurationSeconds, _movingEvent.Id);

        var transform = new TranslateTransform((_movingEventNewStart - _movingEvent.StartSeconds) * pps, 0);
        _movingEventBorder!.RenderTransform = transform;
        // The linked audio/video partner follows live, not only after the drop.
        if (_linkedEventBorder != null) _linkedEventBorder.RenderTransform = transform;
        _viewModel.StatusText = $"Move to {_movingEventNewStart:0.##}s";
    }

    /// <summary>
    /// Live stretch feedback: the block is scaled (and shifted for the left
    /// edge) without touching layout; the model changes once on release.
    /// </summary>
    private void DragStretch(double deltaX, bool fromLeftEdge)
    {
        var evt = _movingEvent!;
        var pps = _viewModel.PixelsPerSecond;
        var deltaSeconds = deltaX / pps;

        if (fromLeftEdge)
        {
            var end = evt.StartSeconds + evt.DurationSeconds;
            _movingEventNewStart = Math.Clamp(evt.StartSeconds + deltaSeconds, 0, end - 0.1);
            _stretchNewDuration = end - _movingEventNewStart;
        }
        else
        {
            _movingEventNewStart = evt.StartSeconds;
            _stretchNewDuration = Math.Max(0.1, evt.DurationSeconds + deltaSeconds);
        }

        var scale = _stretchNewDuration / Math.Max(0.001, evt.DurationSeconds);
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, 1));
        if (fromLeftEdge)
            transform.Children.Add(new TranslateTransform((_movingEventNewStart - evt.StartSeconds) * pps, 0));
        _movingEventBorder!.RenderTransform = transform;

        var sourceSpan = evt.DurationSeconds; // rate preview relative to the current rate
        var speedFactor = sourceSpan / _stretchNewDuration;
        _viewModel.StatusText =
            $"Stretch to {_stretchNewDuration:0.##}s ({speedFactor:0.##}x of current speed)";
    }

    private static void UpdateEventCursor(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        var localX = e.GetPosition(border).X;
        var onEdge = localX <= EdgeZonePx || localX >= border.ActualWidth - EdgeZonePx;
        border.Cursor = onEdge && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? Cursors.SizeWE
            : Cursors.Hand;
    }

    private void EventBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border) border.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EventBlock_LostMouseCapture(object sender, MouseEventArgs e)
    {
        var evt = _movingEvent;
        var mode = _eventDragMode;
        var wasDragging = _isDraggingEvent;
        var newStart = _movingEventNewStart;
        var newDuration = _stretchNewDuration;

        if (_movingEventBorder != null) _movingEventBorder.RenderTransform = null;
        if (_linkedEventBorder != null) _linkedEventBorder.RenderTransform = null;
        _movingEvent = null;
        _movingEventBorder = null;
        _linkedEventBorder = null;
        _isDraggingEvent = false;
        _eventDragMode = EventDragMode.None;

        if (evt is null || !wasDragging) return;

        if (mode == EventDragMode.Move)
            _viewModel.MoveEvent(evt.Id, newStart);
        else
            _viewModel.StretchEvent(evt.Id, newStart, newDuration);
    }

    // ---------- fx button (Event FX window) + right-click menu ----------

    private EventFxWindow? _fxWindow;

    private void EventFx_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventViewModel evt) return;
        OpenFxWindow(evt);
        e.Handled = true;
    }

    /// <summary>Opens the per-clip Event FX window (one at a time).</summary>
    private void OpenFxWindow(EventViewModel evt)
    {
        _fxWindow?.Close();
        _fxWindow = new EventFxWindow(_viewModel, evt.Id, evt.Name) { Owner = this };
        _fxWindow.Closed += (_, _) => _fxWindow = null;
        _fxWindow.Show();
    }

    private void EventBlock_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventViewModel evt) return;
        _viewModel.SelectEvent(evt.Id);
        ShowEventMenu((UIElement)sender, evt, includeClipActions: true);
        e.Handled = true;
    }

    /// <summary>
    /// Builds the per-clip menu: compatible effects (fx button shows just these),
    /// plus clip actions (remove effects / delete) for the right-click variant.
    /// </summary>
    private void ShowEventMenu(UIElement target, EventViewModel evt, bool includeClipActions)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        var effectItems = BuildEffectMenuItems(evt.Id);
        if (includeClipActions)
        {
            var openFx = new MenuItem { Header = "Event FX…" };
            openFx.Click += (_, _) => OpenFxWindow(evt);
            menu.Items.Add(openFx);

            var addEffect = new MenuItem { Header = "Add Effect" };
            foreach (var item in effectItems) addEffect.Items.Add(item);
            addEffect.IsEnabled = effectItems.Count > 0;
            menu.Items.Add(addEffect);

            var removeEffects = new MenuItem
            {
                Header = "Remove All Effects",
                IsEnabled = _viewModel.EventHasEffects(evt.Id)
            };
            removeEffects.Click += (_, _) => _viewModel.RemoveAllEffects(evt.Id);
            menu.Items.Add(removeEffects);

            menu.Items.Add(new Separator());

            var unlink = new MenuItem
            {
                Header = "Unlink Audio/Video",
                InputGestureText = "T",
                IsEnabled = evt.IsLinked,
                ToolTip = "After unlinking, the video and its audio move independently"
            };
            unlink.Click += (_, _) => _viewModel.UnlinkEvent(evt.Id);
            menu.Items.Add(unlink);

            var delete = new MenuItem { Header = "Delete (Del)" };
            delete.Click += (_, _) => _viewModel.DeleteEvent(evt.Id);
            menu.Items.Add(delete);
        }
        else
        {
            if (effectItems.Count == 0)
                menu.Items.Add(new MenuItem { Header = "No compatible effects", IsEnabled = false });
            foreach (var item in effectItems) menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private List<MenuItem> BuildEffectMenuItems(Guid eventId)
    {
        var items = new List<MenuItem>();
        foreach (var effect in _viewModel.GetCompatibleEffects(eventId))
        {
            var item = new MenuItem
            {
                Header = effect.Name,
                InputGestureText = effect.Category,
                ToolTip = effect.Description.Length > 0 ? effect.Description : null
            };
            var effectId = effect.Id; // capture a stable copy for the closure
            item.Click += (_, _) => _viewModel.ApplyEffectToEvent(effectId, eventId);
            items.Add(item);
        }
        return items;
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
