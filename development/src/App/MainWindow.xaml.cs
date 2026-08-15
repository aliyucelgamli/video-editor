using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.App.Ui;
using VideoEditor.App.ViewModels;
using VideoEditor.Domain;

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
    private enum EventDragMode { None, Move, StretchLeft, StretchRight, TrimLeft, TrimRight, Slip }
    private const double EdgeZonePx = 8.0;
    private EventDragMode _eventDragMode = EventDragMode.None;
    private EventViewModel? _movingEvent;
    private Border? _movingEventBorder;
    private Border? _linkedEventBorder;
    private double _movingEventStartX;
    private double _movingEventNewStart;
    private double _stretchNewDuration;
    private double _slipDeltaSeconds;
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
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PlayheadX)) KeepPlayheadVisible();
        };
        _viewModel.NewProjectRequested += (_, _) => ShowNewProjectDialog();
        _viewModel.ExportRequested += (_, _) => ShowExportDialog();
        _viewModel.AddTextRequested += (_, _) => ShowAddTextDialog();
        _viewModel.ExportSessionStarted += (_, session) =>
        {
            // Deferred: ShowDialog pumps messages until the window closes, so
            // opening it inside the event would stall the export itself.
            _ = Dispatcher.InvokeAsync(() =>
                new ExportProgressWindow(session) { Owner = this }.ShowDialog());
        };
    }

    /// <summary>File → New: resolution/fps dialog, then a fresh project.</summary>
    private void ShowNewProjectDialog()
    {
        if (!_viewModel.ConfirmDiscardChanges()) return;
        var dialog = new NewProjectWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _viewModel.CreateNewProject(
            dialog.ProjectName, dialog.ProjectWidth, dialog.ProjectHeight, dialog.ProjectFps);
    }

    /// <summary>Export button: format/quality dialog, then save-as + render.</summary>
    private void ShowExportDialog()
    {
        var project = _viewModel.CurrentProject;
        var dialog = new ExportWindow(
            project.Settings, project.ExportRange, project.Duration, _viewModel.FfmpegPath)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        _viewModel.StartExport(
            dialog.SelectedFormat, dialog.OutputWidth, dialog.OutputHeight, dialog.OutputFps,
            dialog.Crf, dialog.UseHardwareEncoder);
    }

    /// <summary>Text button: style dialog, then a title lands at the playhead.</summary>
    private void ShowAddTextDialog()
    {
        var dialog = new TextEventWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _viewModel.AddTextEvent(dialog.TextStyle);
    }

    private void ShowEditTextDialog(Guid eventId)
    {
        if (_viewModel.GetTextStyle(eventId) is not { } style) return;
        var dialog = new TextEventWindow(style) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _viewModel.EditTextEvent(eventId, dialog.TextStyle);
    }

    /// <summary>During playback, scrolls the timeline so the red playhead stays on screen.</summary>
    private void KeepPlayheadVisible()
    {
        if (!_viewModel.Preview.IsPlaying) return;
        var viewport = LanesScroll.ViewportWidth;
        if (viewport <= 0) return;

        var x = _viewModel.PlayheadX;
        var left = LanesScroll.HorizontalOffset;
        if (x < left || x > left + viewport - 24)
            LanesScroll.ScrollToHorizontalOffset(Math.Max(0, x - 48));
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

    /// <summary>Delete in the library removes the selected references (in-use assets are kept).</summary>
    private void MediaList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        var ids = MediaList.SelectedItems.OfType<MediaItemViewModel>().Select(m => m.Id).ToList();
        if (ids.Count == 0) return; // let the window binding delete the selected clip instead
        _viewModel.RemoveMediaItems(ids);
        e.Handled = true;
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

        // Pull keyboard focus out of the library so Del deletes this clip,
        // not a media item, and Space toggles playback.
        LanesScroll.Focus();
        MediaList.SelectedIndex = -1;

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

    /// <summary>
    /// Plain edge drag = trim, Shift+edge = time stretch (VEGAS-style),
    /// Alt anywhere = slip, plain inside = move.
    /// </summary>
    private static EventDragMode DetectDragMode(Border border, double localX)
    {
        var onLeftEdge = localX <= EdgeZonePx;
        var onRightEdge = localX >= border.ActualWidth - EdgeZonePx;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return onLeftEdge ? EventDragMode.StretchLeft
                : onRightEdge ? EventDragMode.StretchRight
                : EventDragMode.Move;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return EventDragMode.Slip;
        return onLeftEdge ? EventDragMode.TrimLeft
            : onRightEdge ? EventDragMode.TrimRight
            : EventDragMode.Move;
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
            case EventDragMode.TrimRight: DragTrim(deltaX, fromLeftEdge: false); break;
            case EventDragMode.TrimLeft: DragTrim(deltaX, fromLeftEdge: true); break;
            case EventDragMode.Slip: DragSlip(deltaX); break;
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

    /// <summary>
    /// Live trim feedback (same visual as stretch); the model changes once on
    /// release, clamped to the media's bounds by the view model.
    /// </summary>
    private void DragTrim(double deltaX, bool fromLeftEdge)
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
        _viewModel.StatusText = $"Trim to {_stretchNewDuration:0.##}s";
    }

    /// <summary>Slip feedback: geometry is untouched, only the status reports the shift.</summary>
    private void DragSlip(double deltaX)
    {
        _slipDeltaSeconds = deltaX / _viewModel.PixelsPerSecond;
        _viewModel.StatusText = $"Slip {(_slipDeltaSeconds >= 0 ? "+" : string.Empty)}{_slipDeltaSeconds:0.##}s " +
                                "(source slides, position stays)";
    }

    private static void UpdateEventCursor(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        var localX = e.GetPosition(border).X;
        var onEdge = localX <= EdgeZonePx || localX >= border.ActualWidth - EdgeZonePx;
        border.Cursor = onEdge ? Cursors.SizeWE
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Cursors.ScrollWE
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
        var slipDelta = _slipDeltaSeconds;

        if (_movingEventBorder != null) _movingEventBorder.RenderTransform = null;
        if (_linkedEventBorder != null) _linkedEventBorder.RenderTransform = null;
        _movingEvent = null;
        _movingEventBorder = null;
        _linkedEventBorder = null;
        _isDraggingEvent = false;
        _eventDragMode = EventDragMode.None;
        _slipDeltaSeconds = 0;

        if (evt is null || !wasDragging) return;

        switch (mode)
        {
            case EventDragMode.Move:
                _viewModel.MoveEvent(evt.Id, newStart);
                break;
            case EventDragMode.StretchLeft or EventDragMode.StretchRight:
                _viewModel.StretchEvent(evt.Id, newStart, newDuration);
                break;
            case EventDragMode.TrimLeft or EventDragMode.TrimRight:
                _viewModel.TrimEvent(evt.Id, mode == EventDragMode.TrimLeft, newStart, newDuration);
                break;
            case EventDragMode.Slip:
                _viewModel.SlipEvent(evt.Id, slipDelta);
                break;
        }
    }

    // ---------- fx button (Event FX window) + right-click menu ----------

    private readonly ChildWindowSlot<EventFxWindow> _fxWindow = new();

    private void EventFx_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventViewModel evt) return;
        OpenFxWindow(evt);
        e.Handled = true;
    }

    /// <summary>Opens the per-clip Event FX window (one at a time).</summary>
    private void OpenFxWindow(EventViewModel evt)
    {
        _fxWindow.Show(this, () => new EventFxWindow(_viewModel, evt.Id, evt.Name));
    }

    // ---------- size + "…" buttons (transform editor / Clip Properties) ----------

    private readonly ChildWindowSlot<EventPropertiesWindow> _propertiesWindow = new();
    private readonly ChildWindowSlot<TransformEditorWindow> _transformWindow = new();

    /// <summary>Size button: the visual gizmo editor (audio clips fall back to Properties).</summary>
    private void EventSize_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventViewModel evt) return;
        _viewModel.SelectEvent(evt.Id);
        OpenTransformEditor(evt.Id);
        e.Handled = true;
    }

    private void EventMore_Click(object sender, RoutedEventArgs e) => OpenPropertiesFrom(sender, e);

    private void OpenPropertiesFrom(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventViewModel evt) return;
        _viewModel.SelectEvent(evt.Id);
        OpenPropertiesWindow(evt.Id);
        e.Handled = true;
    }

    private void OpenTransformEditor(Guid eventId)
    {
        if (_viewModel.CreateTransformEditor(eventId) is not { } editor)
        {
            OpenPropertiesWindow(eventId); // audio clip — nothing visual to transform
            return;
        }
        _transformWindow.Show(this, () => new TransformEditorWindow(editor));
    }

    private void OpenPropertiesWindow(Guid eventId)
    {
        if (_viewModel.CreateEventProperties(eventId) is not { } properties) return;
        _propertiesWindow.Show(this, () => new EventPropertiesWindow(properties));
    }

    private static readonly (EasingType Type, string Label)[] EasingChoices =
    {
        (EasingType.InOutSine, "Smooth (sine)"),
        (EasingType.Linear, "Linear"),
        (EasingType.InSine, "Ease in (sine)"),
        (EasingType.OutSine, "Ease out (sine)"),
        (EasingType.InOutQuad, "Smooth (quad)"),
        (EasingType.InOutCubic, "Smooth (cubic)"),
        (EasingType.InBack, "Back in (overshoot)"),
        (EasingType.OutBack, "Back out (overshoot)")
    };

    private static MenuItem BuildEasingMenu(string header, EasingType current, Action<EasingType> apply)
    {
        var root = new MenuItem { Header = header };
        foreach (var (type, label) in EasingChoices)
        {
            var choice = type;
            var item = new MenuItem { Header = label, IsChecked = type == current };
            item.Click += (_, _) => apply(choice);
            root.Items.Add(item);
        }
        return root;
    }

    // ---------- Fade grips (top corners of every clip) ----------

    private (Guid EventId, bool IsFadeIn, double OriginalSeconds, double StartX)? _fadeDrag;

    private void FadeGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement grip || grip.DataContext is not EventViewModel evt) return;
        var isFadeIn = grip.Tag as string == "in";
        if (_viewModel.GetEventFadeInfo(evt.Id) is not { } fade) return;

        _viewModel.SelectEvent(evt.Id);
        _fadeDrag = (evt.Id, isFadeIn,
            isFadeIn ? fade.FadeIn : fade.FadeOut,
            e.GetPosition(LanesScroll).X);
        grip.CaptureMouse();
        e.Handled = true;
    }

    private void FadeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_fadeDrag is not { } drag || sender is not FrameworkElement grip || !grip.IsMouseCaptured) return;

        var deltaSeconds = (e.GetPosition(LanesScroll).X - drag.StartX) / _viewModel.PixelsPerSecond;
        var seconds = drag.OriginalSeconds + (drag.IsFadeIn ? deltaSeconds : -deltaSeconds);
        _viewModel.SetEventFadeLive(drag.EventId, drag.IsFadeIn, seconds);
        (grip.DataContext as EventViewModel)?.RefreshFadeVisuals();
        e.Handled = true;
    }

    private void FadeGrip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_fadeDrag is null) return;
        (sender as FrameworkElement)?.ReleaseMouseCapture(); // commit happens in LostCapture
        e.Handled = true;
    }

    private void FadeGrip_LostCapture(object sender, MouseEventArgs e)
    {
        if (_fadeDrag is not { } drag) return;
        _fadeDrag = null;
        _viewModel.CommitEventFade(drag.EventId, drag.IsFadeIn, drag.OriginalSeconds);
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

            if (_viewModel.GetTextStyle(evt.Id) != null)
            {
                var editText = new MenuItem { Header = "Edit Text…" };
                editText.Click += (_, _) => ShowEditTextDialog(evt.Id);
                menu.Items.Add(editText);
            }

            var split = new MenuItem { Header = "Split at Playhead", InputGestureText = "S" };
            split.Click += (_, _) => _viewModel.SplitAtPlayheadCommand.Execute(null);
            menu.Items.Add(split);

            if (evt.IsVisual)
            {
                var transform = new MenuItem { Header = "Size && Position…" };
                transform.Click += (_, _) => OpenTransformEditor(evt.Id);
                menu.Items.Add(transform);
            }

            if (_viewModel.GetEventFadeInfo(evt.Id) is { } fade)
            {
                menu.Items.Add(BuildEasingMenu("Fade In Easing", fade.InEasing,
                    easing => _viewModel.SetEventFadeEasing(evt.Id, fadeIn: true, easing)));
                menu.Items.Add(BuildEasingMenu("Fade Out Easing", fade.OutEasing,
                    easing => _viewModel.SetEventFadeEasing(evt.Id, fadeIn: false, easing)));
            }

            var properties = new MenuItem { Header = "Properties…" };
            properties.Click += (_, _) => OpenPropertiesWindow(evt.Id);
            menu.Items.Add(properties);

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
                _viewModel.PreviewRangeDrag(_viewModel.SnapBarTime(time), _viewModel.RangeEndX / pps);
                break;
            case RulerDragMode.RangeEnd:
                _viewModel.PreviewRangeDrag(_viewModel.RangeStartX / pps, _viewModel.SnapBarTime(time));
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
