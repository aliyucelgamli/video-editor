using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

public partial class MainWindow : Window
{
    private const string MediaIdFormat = "VideoEditorMediaId";

    private readonly MainViewModel _viewModel = new();
    private Point _dragStart;
    private MediaItemViewModel? _dragCandidate;

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
        _dragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as MediaItemViewModel;
    }

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject(MediaIdFormat, _dragCandidate.Id.ToString());
        _dragCandidate = null;
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

    // ---------- Timeline lanes: drop targets + selection ----------

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

    private void Lane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _viewModel.SelectEvent(null);

    private void EventBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EventViewModel evt)
        {
            _viewModel.SelectEvent(evt.Id);
            e.Handled = true;
        }
    }

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
