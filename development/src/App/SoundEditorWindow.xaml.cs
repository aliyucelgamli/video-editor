using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoEditor.App.Ui;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

/// <summary>
/// The sound editor: a waveform of one audio file that can be split, trimmed,
/// levelled, faded, run through the audio effect chain and exported to any of
/// the offered formats. Everything is non-destructive — the source file is only
/// ever read, and the edit lives in a <c>SoundEditSession</c> with its own undo
/// history, separate from the timeline's.
///
/// This class owns the mouse and the clock; every model change goes through
/// <see cref="SoundEditorViewModel"/>.
/// </summary>
public partial class SoundEditorWindow : Window
{
    /// <summary>A drag shorter than this is a click that moves the playhead.</summary>
    private const double SelectionDragThreshold = 3.0;

    private readonly SoundEditorViewModel _viewModel;
    private readonly IDialogService _dialogs = new DialogService();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private bool _isSelecting;
    private double _selectionAnchor;
    private double _dragStartX;
    private DateTime _playStartedAt;
    private double _playStartedFrom;
    private double _playLength;

    public SoundEditorWindow(SoundEditorContext context, Guid? initialMediaId = null)
    {
        InitializeComponent();
        _viewModel = new SoundEditorViewModel(context);
        DataContext = _viewModel;

        _viewModel.VisualsChanged += (_, _) => RefreshWave();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SoundEditorViewModel.IsPlaying)) RefreshPlayGlyph();
        };
        _clock.Tick += Clock_Tick;

        // Deferred to Loaded: the strip needs a real viewport width before it can
        // be sized, and ffprobe must not hold up the first paint.
        Loaded += async (_, _) =>
        {
            RefreshWave();
            if (initialMediaId is { } mediaId) await LoadMediaAsync(mediaId);
        };
    }

    /// <summary>Loads a file into an already open editor (a later drag from the library).</summary>
    public async Task LoadMediaAsync(Guid mediaId)
    {
        await _viewModel.LoadFromLibraryAsync(mediaId);
        RefreshWave();
    }

    // ---------- Waveform painting and sizing ----------

    /// <summary>
    /// Pushes the current model into the strip. The strip is sized here rather
    /// than by layout, because its width is the zoom factor times the viewport
    /// and a <c>FrameworkElement</c> has no natural width of its own.
    /// </summary>
    private void RefreshWave()
    {
        var viewportWidth = WaveScroll.ViewportWidth;
        if (viewportWidth <= 0) viewportWidth = WaveScroll.ActualWidth;

        Wave.Width = Math.Max(60, viewportWidth * _viewModel.Zoom);
        Wave.Height = Math.Max(80, WaveScroll.ViewportHeight > 0
            ? WaveScroll.ViewportHeight
            : WaveScroll.ActualHeight);

        Wave.PeaksPerSecond = _viewModel.PeaksPerSecond;
        Wave.Peaks = _viewModel.Peaks;
        Wave.SelectionStart = _viewModel.SelectionStart;
        Wave.SelectionEnd = _viewModel.SelectionEnd;
        Wave.PlayheadTime = _viewModel.PlayheadTime;
        Wave.SelectedSegmentId = _viewModel.SelectedSegment?.Id;
        Wave.Session = _viewModel.Session;
    }

    private void WaveScroll_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshWave();

    private void RefreshPlayGlyph() =>
        PlayGlyph.Text = _viewModel.IsPlaying ? "\uE71A" : "\uE768"; // stop = playing, play = idle

    // ---------- Mouse on the waveform ----------

    private void Wave_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.HasClip) return;
        var time = Wave.TimeAt(e.GetPosition(Wave).X);

        if (e.ClickCount == 2)
        {
            SelectPieceAt(time);
            return;
        }

        _isSelecting = true;
        _selectionAnchor = time;
        _dragStartX = e.GetPosition(Wave).X;
        _viewModel.PlayheadTime = time;
        _viewModel.SetSelection(time, time);
        Wave.CaptureMouse();
    }

    private void Wave_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || e.LeftButton != MouseButtonState.Pressed) return;

        var x = e.GetPosition(Wave).X;
        if (Math.Abs(x - _dragStartX) < SelectionDragThreshold) return;
        _viewModel.SetSelection(_selectionAnchor, Wave.TimeAt(x));
    }

    private void Wave_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;
        _isSelecting = false;
        Wave.ReleaseMouseCapture();

        // A click without a drag is "put the playhead here", not "select nothing
        // wide" — so the selection is dropped and the piece under it is picked.
        if (Math.Abs(e.GetPosition(Wave).X - _dragStartX) < SelectionDragThreshold)
        {
            _viewModel.ClearSelection();
            SelectPieceAt(_viewModel.PlayheadTime);
        }
    }

    private void Wave_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ClearSelection();
        e.Handled = true;
    }

    /// <summary>Makes the piece covering an output time the one being edited.</summary>
    private void SelectPieceAt(double outputTime)
    {
        if (_viewModel.Session?.Locate(outputTime) is not { } hit) return;
        _viewModel.SelectedSegment =
            _viewModel.Segments.FirstOrDefault(segment => segment.Id == hit.Segment.Id);
    }

    // ---------- Transport ----------

    private void PlayStop_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsPlaying)
        {
            StopPlayback();
            return;
        }
        _ = StartPlaybackAsync(); // fire-and-forget: the render is awaited inside
    }

    private async Task StartPlaybackAsync()
    {
        _playStartedFrom = _viewModel.PlayheadTime;
        _playLength = await _viewModel.PlayAsync();
        if (_playLength <= 0) return;

        // The clock starts after the render, so the playhead and the sound agree.
        _playStartedAt = DateTime.UtcNow;
        _clock.Start();
    }

    private void StopPlayback()
    {
        _clock.Stop();
        _viewModel.StopPlayback();
    }

    private void Clock_Tick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _playStartedAt).TotalSeconds;
        _viewModel.PlayheadTime = _playStartedFrom + elapsed;

        if (elapsed < _playLength) return;
        _clock.Stop();
        _viewModel.NotifyPlaybackFinished();
    }

    private void Rewind_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        _viewModel.PlayheadTime = 0;
        WaveScroll.ScrollToHorizontalOffset(0);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ApplyZoom(_viewModel.Zoom * 1.5);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ApplyZoom(_viewModel.Zoom / 1.5);

    /// <summary>Zooms around the playhead, so the spot being worked on stays put.</summary>
    private void ApplyZoom(double zoom)
    {
        var anchor = _viewModel.PlayheadTime;
        _viewModel.Zoom = zoom;
        RefreshWave();

        // XAt reads ActualWidth, which is a layout result — without this the
        // anchor is computed from the pre-zoom width and the view jumps home.
        Wave.UpdateLayout();
        var target = Wave.XAt(anchor) - WaveScroll.ViewportWidth / 2;
        WaveScroll.ScrollToHorizontalOffset(Math.Max(0, target));
    }

    // ---------- Loading ----------

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a sound to edit",
            Filter = "Audio and video (*.wav;*.mp3;*.ogg;*.opus;*.flac;*.m4a;*.aac;*.mp4;*.mov;*.mkv;*.webm)" +
                     "|*.wav;*.mp3;*.ogg;*.opus;*.flac;*.m4a;*.aac;*.mp4;*.mov;*.mkv;*.webm" +
                     "|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        await _viewModel.LoadFileAsync(dialog.FileName);
        RefreshWave();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = AcceptsDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(DragFormats.MediaId) is string raw && Guid.TryParse(raw, out var mediaId))
        {
            await _viewModel.LoadFromLibraryAsync(mediaId);
            RefreshWave();
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var audible = paths.FirstOrDefault(SoundEditorViewModel.IsAudibleFile);
        if (audible is null) return;

        await _viewModel.LoadFileAsync(audible);
        RefreshWave();
    }

    private static bool AcceptsDrop(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormats.MediaId)) return true;
        return e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
               paths.Any(SoundEditorViewModel.IsAudibleFile);
    }

    // ---------- Edit buttons ----------

    private void Split_Click(object sender, RoutedEventArgs e) => RunEdit(_viewModel.SplitAtPlayhead);

    private void TrimStart_Click(object sender, RoutedEventArgs e) =>
        RunEdit(() => _viewModel.TrimEdgeToPlayhead(trimStart: true));

    private void TrimEnd_Click(object sender, RoutedEventArgs e) =>
        RunEdit(() => _viewModel.TrimEdgeToPlayhead(trimStart: false));

    private void TrimToSelection_Click(object sender, RoutedEventArgs e) =>
        RunEdit(_viewModel.TrimToSelection);

    private void DeleteSelection_Click(object sender, RoutedEventArgs e) =>
        RunEdit(_viewModel.DeleteSelection);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _viewModel.SelectAll();

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => _viewModel.ClearSelection();

    private void Undo_Click(object sender, RoutedEventArgs e) => RunEdit(_viewModel.Undo);

    private void ResetClip_Click(object sender, RoutedEventArgs e) => RunEdit(_viewModel.ResetClip);

    private void ResetSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSegment is not { } segment) return;
        _viewModel.BeginSliderEdit();
        segment.ResetLevels();
    }

    /// <summary>Every structural edit stops playback and repaints.</summary>
    private void RunEdit(Action edit)
    {
        StopPlayback();
        edit();
        RefreshWave();
    }

    private void Slider_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        _viewModel.BeginSliderEdit();

    // ---------- Pieces ----------

    private void PieceUp_Click(object sender, RoutedEventArgs e) => MovePiece(sender, -1);

    private void PieceDown_Click(object sender, RoutedEventArgs e) => MovePiece(sender, +1);

    private void MovePiece(object sender, int delta)
    {
        if (TagId(sender) is not { } id) return;
        RunEdit(() => _viewModel.MoveSegment(id, delta));
    }

    private void PieceRemove_Click(object sender, RoutedEventArgs e)
    {
        if (TagId(sender) is not { } id) return;
        RunEdit(() => _viewModel.RemoveSegment(id));
    }

    // ---------- Effect chain ----------

    private void EffectList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelectedEffect();

    private void AddEffect_Click(object sender, RoutedEventArgs e) => AddSelectedEffect();

    private void AddSelectedEffect()
    {
        if (EffectList.SelectedItem is not EffectDefinitionViewModel definition) return;
        _viewModel.AddEffect(definition.Id);
        RefreshWave();
    }

    private void EffectUp_Click(object sender, RoutedEventArgs e) => MoveEffect(sender, -1);

    private void EffectDown_Click(object sender, RoutedEventArgs e) => MoveEffect(sender, +1);

    private void MoveEffect(object sender, int delta)
    {
        if (TagId(sender) is not { } id) return;
        _viewModel.MoveEffect(id, delta);
    }

    private void EffectRemove_Click(object sender, RoutedEventArgs e)
    {
        if (TagId(sender) is not { } id) return;
        _viewModel.RemoveEffect(id);
        RefreshWave();
    }

    // ---------- Export ----------

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasClip) return;
        StopPlayback();

        var (folder, fileName, filter) = _viewModel.ExportTarget();
        var dialog = new SaveFileDialog
        {
            Title = "Export sound",
            Filter = filter,
            FileName = fileName,
            AddExtension = true,
            OverwritePrompt = true
        };
        try
        {
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
        }
        catch
        {
            // A missing default folder must not stop the dialog from opening.
        }

        if (dialog.ShowDialog(this) != true) return;

        var result = await _viewModel.ExportAsync(dialog.FileName);
        if (result.Success) OfferToOpen(result.OutputPath);
        else if (!result.Cancelled)
            _dialogs.Alert("Export Failed", "The sound could not be exported.",
                result.Error, DialogTone.Error);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _viewModel.CancelExport();

    /// <summary>Success notice with the two things a user wants next.</summary>
    private void OfferToOpen(string path)
    {
        var choice = _dialogs.Show(new DialogOptions
        {
            Title = "Export Finished",
            Message = Path.GetFileName(path),
            Details = path,
            Tone = DialogTone.Success,
            Buttons = new[]
            {
                new DialogButton("Play", "play"),
                new DialogButton("Open folder", "folder"),
                new DialogButton("Close", "close", IsPrimary: true)
            },
            DismissResult = "close"
        });

        switch (choice)
        {
            case "play":
                Launch(path);
                break;
            case "folder":
                Launch(Path.GetDirectoryName(path) ?? path);
                break;
        }
    }

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // Opening a finished file is a convenience — never let it crash the app.
        }
    }

    // ---------- Keyboard ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A focused text box or combo box owns its keys; the editor only takes
        // what is left over.
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;

        switch (e.Key)
        {
            case Key.Space:
                PlayStop_Click(sender, new RoutedEventArgs());
                break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.None:
                RunEdit(_viewModel.SplitAtPlayhead);
                break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                RunEdit(_viewModel.DeleteSelection);
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                RunEdit(_viewModel.Undo);
                break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.SelectAll();
                break;
            case Key.Home:
                Rewind_Click(sender, new RoutedEventArgs());
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _clock.Stop();
        _viewModel.Dispose();
    }

    private static Guid? TagId(object sender) =>
        (sender as FrameworkElement)?.Tag is Guid id ? id : null;
}
