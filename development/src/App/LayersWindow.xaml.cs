using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VideoEditor.App.ViewModels;
using VideoEditor.Domain;

namespace VideoEditor.App;

/// <summary>
/// The compositing stack: every visual clip listed from the top layer down,
/// with one-click bring-forward / send-backward and a typed layer number.
/// Track layers (added to their clips) sit at the bottom of the window.
/// Selecting a row selects the clip on the timeline.
/// </summary>
public partial class LayersWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _refreshing;

    public LayersWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        ClipList.ItemsSource = _viewModel.BuildLayerItems();
        TrackList.ItemsSource = _viewModel.VisualTracks()
            .Select(t => new { t.Id, t.Name, t.Layer })
            .ToList();
        _refreshing = false;
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (ClipList.SelectedItem is LayerItemViewModel item)
            _viewModel.SelectEvent(item.EventId);
    }

    // ---------- Clip layers ----------

    private void ClipUp_Click(object sender, RoutedEventArgs e) => NudgeClip(sender, +1);

    private void ClipDown_Click(object sender, RoutedEventArgs e) => NudgeClip(sender, -1);

    private void NudgeClip(object sender, int delta)
    {
        if (TagId(sender) is not { } eventId) return;
        _viewModel.NudgeEventLayer(eventId, delta);
        Refresh();
    }

    private void LayerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyLayerBox(sender);
        e.Handled = true;
    }

    private void LayerBox_LostFocus(object sender, RoutedEventArgs e) => ApplyLayerBox(sender);

    private void ApplyLayerBox(object sender)
    {
        if (_refreshing || sender is not TextBox box) return;
        if (TagId(box) is not { } eventId) return;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layer))
        {
            Refresh(); // put the old value back
            return;
        }

        _viewModel.SetEventLayer(eventId, layer);
        Refresh();
    }

    // ---------- Track layers ----------

    private void TrackUp_Click(object sender, RoutedEventArgs e) => NudgeTrack(sender, +1);

    private void TrackDown_Click(object sender, RoutedEventArgs e) => NudgeTrack(sender, -1);

    private void NudgeTrack(object sender, int delta)
    {
        if (TagId(sender) is not { } trackId) return;
        var current = _viewModel.VisualTracks().FirstOrDefault(t => t.Id == trackId);
        if (current.Id != trackId) return;

        _viewModel.SetTrackLayer(trackId, current.Layer + delta);
        Refresh();
    }

    /// <summary>"+" → pick the kind of lane to add.</summary>
    private void AddTrack_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var (label, type) in new[]
                 {
                     ("Video track", TrackType.Video),
                     ("Text / image track", TrackType.Overlay),
                     ("Audio track", TrackType.Audio)
                 })
        {
            var kind = type;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                _viewModel.AddTrack(kind);
                Refresh();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static Guid? TagId(object sender) =>
        (sender as FrameworkElement)?.Tag is Guid id ? id : null;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
