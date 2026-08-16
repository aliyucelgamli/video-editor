using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.App.Ui;
using VideoEditor.App.ViewModels;
using VideoEditor.Application.Actions;
using VideoEditor.Domain;
using VideoEditor.MediaEngine;

namespace VideoEditor.App;

public partial class MainWindow : Window
{
    private const string MediaIdFormat = "VideoEditorMediaId";
    private const string EffectIdFormat = "VideoEditorEffectId";
    private const double DragThreshold = 4.0;

    /// <summary>Whatever is being dragged floats above everything else.</summary>
    private const int DragZIndex = 100;

    private readonly MainViewModel _viewModel = new();
    private readonly IDialogService _dialogs = new DialogService();

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
    private double _laneSelectionAnchor;
    private bool _laneSelectionActive;

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
        _viewModel.LayersRequested += (_, _) => _layersWindow.Show(this, () => new LayersWindow(_viewModel));
        _viewModel.ExportSessionStarted += (_, session) =>
        {
            // Deferred: ShowDialog pumps messages until the window closes, so
            // opening it inside the event would stall the export itself.
            _ = Dispatcher.InvokeAsync(() =>
                new ExportProgressWindow(session) { Owner = this }.ShowDialog());
        };

        ApplyShortcuts();
    }

    // ---------- Keyboard shortcuts (bound at runtime from the ShortcutMap) ----------

    /// <summary>Action registry ids → the view model commands they trigger.</summary>
    private Dictionary<string, ICommand> ActionCommands() => new()
    {
        ["file.new"] = _viewModel.NewProjectCommand,
        ["file.open"] = _viewModel.OpenCommand,
        ["file.save"] = _viewModel.SaveCommand,
        ["file.saveAs"] = _viewModel.SaveAsCommand,
        ["file.import"] = _viewModel.ImportMediaCommand,
        ["file.export"] = _viewModel.ExportCommand,
        ["edit.undo"] = _viewModel.UndoCommand,
        ["edit.redo"] = _viewModel.RedoCommand,
        ["edit.delete"] = _viewModel.DeleteSelectedCommand,
        ["edit.split"] = _viewModel.SplitAtPlayheadCommand,
        ["edit.unlink"] = _viewModel.UnlinkSelectedCommand,
        ["edit.addText"] = _viewModel.AddTextCommand,
        ["playback.toggle"] = _viewModel.PlayPauseCommand,
        ["timeline.rangeStart"] = _viewModel.SetRangeStartCommand,
        ["timeline.rangeEnd"] = _viewModel.SetRangeEndCommand,
        ["timeline.clearRange"] = _viewModel.ClearRangeCommand,
        ["view.zoomIn"] = _viewModel.ZoomInCommand,
        ["view.zoomOut"] = _viewModel.ZoomOutCommand
    };

    /// <summary>Rebuilds the window's key bindings from the live shortcut map.</summary>
    private void ApplyShortcuts()
    {
        var commands = ActionCommands();
        InputBindings.Clear();
        foreach (var action in EditorActions.All)
        {
            if (!commands.TryGetValue(action.Id, out var command)) continue;
            foreach (var gesture in _viewModel.Shortcuts.GesturesFor(action))
            {
                if (!KeyGestureText.TryParse(gesture, out var modifiers, out var key)) continue;
                try
                {
                    // Property initialization, not the (command, key, modifiers)
                    // constructor: that one validates the gesture and rejects
                    // modifier-less letters, which the timeline relies on
                    // (S, T, I, O…). This is the path XAML KeyBindings take.
                    InputBindings.Add(new KeyBinding
                    {
                        Command = command,
                        Key = key,
                        Modifiers = modifiers
                    });
                }
                catch
                {
                    // A single unusable gesture must never stop the app from starting.
                }
            }
        }
    }

    private void MenuShortcuts_Click(object sender, RoutedEventArgs e) => OpenShortcutsWindow();

    private void OpenShortcutsWindow() =>
        new ShortcutsWindow(_viewModel.Shortcuts, onChanged: () =>
        {
            _viewModel.SaveSettings();
            ApplyShortcuts();
        }) { Owner = this }.ShowDialog();

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _viewModel.Settings, OpenShortcutsWindow, _viewModel.RunPerformanceTestAsync) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _viewModel.SaveSettings();
        _viewModel.ApplyPreviewQuality();
        _viewModel.ApplyDecoderSetting();
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
            project.Settings, project.ExportRange, project.Duration, _viewModel.FfmpegPath,
            _viewModel.Settings.UseHardwareEncoderByDefault)
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

    // ---------- Menu bar ----------

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuOpenExports_Click(object sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(System.IO.Path.Combine(CachePaths.LocateAppRoot(), "user", "exports"));

    private void MenuOpenLogs_Click(object sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(System.IO.Path.Combine(Environment.CurrentDirectory, "logs"));

    private static void OpenFolderInExplorer(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            // Opening a folder is a convenience — never let it crash the app.
        }
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        _dialogs.Alert(
            "About Video Editor",
            $"Video Editor {version}",
            "A non-destructive video/audio editor built on .NET and FFmpeg.\n" +
            "Docs: README.md · CLAUDE.md · TODO.md in the project folder.");
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
        if (!_viewModel.ConfirmDiscardChanges(isExit: true))
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }

        // Stops playback and kills any ffmpeg the preview kept warm — otherwise
        // those decoders outlive the app as orphaned processes.
        _viewModel.Preview.Shutdown();
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

    /// <summary>Selecting an effect previews it on the selected clip (nothing is added).</summary>
    private void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.PreviewEffect((EffectList.SelectedItem as EffectDefinitionViewModel)?.Id);
    }

    private void EffectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not EffectDefinitionViewModel effect) return;
        _viewModel.Effects.ApplyEffectToSelection(effect.Id);
        _viewModel.ClearEffectPreview(); // it is a real chain entry now
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

        // Click seeks; dragging from here paints a time selection (the yellow
        // range) that play/loop and export all use.
        var x = Math.Max(0, e.GetPosition(lane).X);
        SeekToPosition(x);
        _laneSelectionAnchor = x / _viewModel.PixelsPerSecond;
        _laneSelectionActive = false;

        _isLaneScrubbing = lane.CaptureMouse();
        lane.MouseMove += Lane_ScrubMove;
        lane.MouseLeftButtonUp += Lane_ScrubEnd;
        lane.LostMouseCapture += Lane_ScrubLost;
    }

    /// <summary>Right-click on empty timeline space: add things, manage lanes, zoom.</summary>
    private void Lane_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement lane) return;
        var time = Math.Max(0, e.GetPosition(lane).X) / _viewModel.PixelsPerSecond;
        var track = LaneTrack(sender);

        var menu = new ContextMenu
        {
            PlacementTarget = lane,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        var add = new MenuItem { Header = "Add" };
        var addText = new MenuItem { Header = "Text…" };
        addText.Click += (_, _) => { _viewModel.SeekTo(time); ShowAddTextDialog(); };
        add.Items.Add(addText);

        var addMedia = new MenuItem { Header = "Media Files… (video, image, audio)" };
        addMedia.Click += (_, _) => _viewModel.ImportMediaCommand.Execute(null);
        add.Items.Add(addMedia);
        menu.Items.Add(add);

        var addTrack = new MenuItem { Header = "Add Track" };
        foreach (var (label, type) in new[]
                 {
                     ("Video Track", TrackType.Video),
                     ("Text / Image Track", TrackType.Overlay),
                     ("Audio Track", TrackType.Audio)
                 })
        {
            var kind = type;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => _viewModel.AddTrack(kind);
            addTrack.Items.Add(item);
        }
        menu.Items.Add(addTrack);

        menu.Items.Add(new Separator());

        var split = new MenuItem { Header = "Split at Playhead", InputGestureText = "S" };
        split.Click += (_, _) => _viewModel.SplitAtPlayheadCommand.Execute(null);
        menu.Items.Add(split);

        var clearSelection = new MenuItem
        {
            Header = "Clear Selection",
            IsEnabled = _viewModel.HasExplicitRange
        };
        clearSelection.Click += (_, _) => _viewModel.ClearRange();
        menu.Items.Add(clearSelection);

        menu.Items.Add(new Separator());

        var layers = new MenuItem { Header = "Layers…" };
        layers.Click += (_, _) => _viewModel.ShowLayersCommand.Execute(null);
        menu.Items.Add(layers);

        var timeline = new MenuItem { Header = "Timeline" };
        var zoomIn = new MenuItem { Header = "Zoom In", InputGestureText = "+" };
        zoomIn.Click += (_, _) => _viewModel.ZoomInCommand.Execute(null);
        var zoomOut = new MenuItem { Header = "Zoom Out", InputGestureText = "-" };
        zoomOut.Click += (_, _) => _viewModel.ZoomOutCommand.Execute(null);
        timeline.Items.Add(zoomIn);
        timeline.Items.Add(zoomOut);
        if (track != null)
        {
            timeline.Items.Add(new Separator());
            var trackLabel = new MenuItem { Header = $"Track: {track.Name}", IsEnabled = false };
            timeline.Items.Add(trackLabel);
        }
        menu.Items.Add(timeline);

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => MenuSettings_Click(sender, e);
        menu.Items.Add(settings);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Lane_ScrubMove(object sender, MouseEventArgs e)
    {
        if (!_isLaneScrubbing || e.LeftButton != MouseButtonState.Pressed) return;

        var x = Math.Max(0, e.GetPosition((IInputElement)sender).X);
        var time = x / _viewModel.PixelsPerSecond;

        // Past the drag threshold this stops being a seek and becomes a
        // selection sweep (the playhead stays where the click put it).
        if (!_laneSelectionActive &&
            Math.Abs(time - _laneSelectionAnchor) * _viewModel.PixelsPerSecond < DragThreshold)
        {
            SeekToPosition(x);
            return;
        }

        _laneSelectionActive = true;
        _viewModel.PreviewRangeSelection(_laneSelectionAnchor, time);
    }

    private void Lane_ScrubEnd(object sender, MouseButtonEventArgs e)
    {
        (sender as FrameworkElement)?.ReleaseMouseCapture();
    }

    private void Lane_ScrubLost(object sender, MouseEventArgs e)
    {
        _isLaneScrubbing = false;
        if (_laneSelectionActive)
        {
            _laneSelectionActive = false;
            var end = Mouse.GetPosition(LanesScroll).X + LanesScroll.HorizontalOffset;
            _viewModel.CommitRangeSelection(
                _laneSelectionAnchor, Math.Max(0, end) / _viewModel.PixelsPerSecond);
        }

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
        // Lift it over its neighbours, otherwise the clip you are holding
        // disappears behind the ones it passes.
        Panel.SetZIndex(border, DragZIndex);
        _linkedEventBorder = evt.LinkedEventId is Guid linkedId ? FindEventBorder(linkedId) : null;
        if (_linkedEventBorder != null) Panel.SetZIndex(_linkedEventBorder, DragZIndex);
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

        if (_movingEventBorder != null)
        {
            _movingEventBorder.RenderTransform = null;
            Panel.SetZIndex(_movingEventBorder, 0);
        }
        if (_linkedEventBorder != null)
        {
            _linkedEventBorder.RenderTransform = null;
            Panel.SetZIndex(_linkedEventBorder, 0);
        }
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

    // ---------- Track headers: hold + drag to reorder lanes ----------

    private TrackViewModel? _draggingTrack;
    private Point _trackDragStart;
    private bool _trackDragActive;

    private void TrackHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement header || header.DataContext is not TrackViewModel track) return;
        _draggingTrack = track;
        _trackDragStart = e.GetPosition(HeadersScroll);
        _trackDragActive = false;
        Panel.SetZIndex(header, DragZIndex); // stay visible while it travels over other lanes
        header.CaptureMouse();
    }

    private void TrackHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingTrack is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement header) return;

        var offsetY = e.GetPosition(HeadersScroll).Y - _trackDragStart.Y;
        if (!_trackDragActive && Math.Abs(offsetY) < DragThreshold) return;

        _trackDragActive = true;
        header.RenderTransform = new TranslateTransform(0, offsetY); // live feedback
        _viewModel.StatusText = $"Moving '{_draggingTrack.Name}' — release to drop it";
    }

    private void TrackHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        (sender as FrameworkElement)?.ReleaseMouseCapture();

    private void TrackHeader_LostCapture(object sender, MouseEventArgs e)
    {
        var track = _draggingTrack;
        var wasDragging = _trackDragActive;
        _draggingTrack = null;
        _trackDragActive = false;
        if (sender is FrameworkElement header)
        {
            header.RenderTransform = null;
            Panel.SetZIndex(header, 0);
        }
        if (track is null || !wasDragging) return;

        // Lane height is fixed (56 + 2 margin), so the drop index follows from
        // how far the header travelled.
        const double laneHeight = 58.0;
        var offsetY = Mouse.GetPosition(HeadersScroll).Y - _trackDragStart.Y;
        var currentIndex = _viewModel.IndexOfTrack(track.Id);
        if (currentIndex < 0) return;

        _viewModel.MoveTrack(track.Id, currentIndex + (int)Math.Round(offsetY / laneHeight));
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

    private readonly ChildWindowSlot<LayersWindow> _layersWindow = new();
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
        _propertiesWindow.Show(this, () => new EventPropertiesWindow(
            properties, editText: () => ShowEditTextDialog(eventId)));
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

                var layer = new MenuItem { Header = "Layer" };
                var forward = new MenuItem { Header = "Bring Forward" };
                forward.Click += (_, _) => _viewModel.NudgeEventLayer(evt.Id, +1);
                var backward = new MenuItem { Header = "Send Backward" };
                backward.Click += (_, _) => _viewModel.NudgeEventLayer(evt.Id, -1);
                var allLayers = new MenuItem { Header = "All Layers…" };
                allLayers.Click += (_, _) => _viewModel.ShowLayersCommand.Execute(null);
                layer.Items.Add(forward);
                layer.Items.Add(backward);
                layer.Items.Add(new Separator());
                layer.Items.Add(allLayers);
                menu.Items.Add(layer);
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

    private void TrackOpacity_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        (SliderContext<TrackViewModel>(sender))?.BeginOpacityEdit();

    private void TrackOpacity_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        (SliderContext<TrackViewModel>(sender))?.EndOpacityEdit();

    private void TrackOpacity_LostMouseCapture(object sender, MouseEventArgs e) =>
        (SliderContext<TrackViewModel>(sender))?.EndOpacityEdit();

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
