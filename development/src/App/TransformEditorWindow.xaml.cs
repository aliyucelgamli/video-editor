using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoEditor.App.ViewModels;
using VideoEditor.Application.Editing;

namespace VideoEditor.App;

/// <summary>
/// Visual transform editor: the clip's frame sits on a stage inside the
/// project bounds and is manipulated directly, Unity-style — corner handles
/// scale (aspect-locked), edge handles stretch one axis, dragging inside
/// moves. The right panel mirrors the numbers; OK commits one undo step.
/// All gizmo math lives in <see cref="TransformGizmo"/> (unit-tested).
/// </summary>
public partial class TransformEditorWindow : Window
{
    private const double StagePadding = 24;
    private const double HandleSize = 9;
    private const double HitRadiusScreen = 8;
    private const double CenterSnapScreen = 8;

    private readonly TransformEditorViewModel _viewModel;
    private readonly Rectangle[] _handles;
    private static readonly GizmoHandle[] HandleOrder =
    {
        GizmoHandle.TopLeft, GizmoHandle.Top, GizmoHandle.TopRight, GizmoHandle.Right,
        GizmoHandle.BottomRight, GizmoHandle.Bottom, GizmoHandle.BottomLeft, GizmoHandle.Left
    };

    private double _viewScale = 1;
    private double _originX;
    private double _originY;

    private GizmoHandle? _dragHandle;
    private Point _dragStartProject;
    private double[] _dragStartValues = Array.Empty<double>();

    public TransformEditorWindow(TransformEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        ClipNameText.Text = viewModel.ClipName;
        SourceInfoText.Text = viewModel.SourceLabel;
        LockAspectCheck.IsChecked = viewModel.LockAspect;

        _handles = new Rectangle[HandleOrder.Length];
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2B, 0x5C, 0xB4)),
                StrokeThickness = 1,
                IsHitTestVisible = false // the stage canvas handles all input
            };
            Stage.Children.Add(_handles[i]);
        }

        _viewModel.PropertyChanged += (_, _) => { UpdateGizmo(); UpdateFields(); };
        UpdateFields();
        _ = LoadFrameAsync(); // fire-and-forget: the outline shows until the frame arrives
    }

    private async Task LoadFrameAsync()
    {
        try
        {
            LayerImage.Source = await _viewModel.LoadFrameAsync();
        }
        catch
        {
            // No frame (missing file / no ffmpeg) — the gizmo still works on the outline.
        }
    }

    // ---------- Stage layout ----------

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutStage();

    private void LayoutStage()
    {
        var availableWidth = Math.Max(50, Stage.ActualWidth - StagePadding * 2);
        var availableHeight = Math.Max(50, Stage.ActualHeight - StagePadding * 2);
        _viewScale = Math.Min(
            availableWidth / Math.Max(1, _viewModel.ProjectWidth),
            availableHeight / Math.Max(1, _viewModel.ProjectHeight));

        var frameWidth = _viewModel.ProjectWidth * _viewScale;
        var frameHeight = _viewModel.ProjectHeight * _viewScale;
        _originX = (Stage.ActualWidth - frameWidth) / 2;
        _originY = (Stage.ActualHeight - frameHeight) / 2;

        FrameOutline.Width = frameWidth;
        FrameOutline.Height = frameHeight;
        Canvas.SetLeft(FrameOutline, _originX);
        Canvas.SetTop(FrameOutline, _originY);

        UpdateGizmo();
    }

    /// <summary>Places the layer image, selection border and handles from the model.</summary>
    private void UpdateGizmo()
    {
        var rect = CurrentRect();
        var left = _originX + rect.Left * _viewScale;
        var top = _originY + rect.Top * _viewScale;
        var width = Math.Max(1, rect.Width * _viewScale);
        var height = Math.Max(1, rect.Height * _viewScale);

        LayerImage.Width = width;
        LayerImage.Height = height;
        Canvas.SetLeft(LayerImage, left);
        Canvas.SetTop(LayerImage, top);

        SelectionRect.Width = width;
        SelectionRect.Height = height;
        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);

        for (var i = 0; i < HandleOrder.Length; i++)
        {
            var (x, y) = HandlePoint(HandleOrder[i], left, top, width, height);
            Canvas.SetLeft(_handles[i], x - HandleSize / 2);
            Canvas.SetTop(_handles[i], y - HandleSize / 2);
        }
    }

    private static (double X, double Y) HandlePoint(
        GizmoHandle handle, double left, double top, double width, double height) => handle switch
    {
        GizmoHandle.TopLeft => (left, top),
        GizmoHandle.Top => (left + width / 2, top),
        GizmoHandle.TopRight => (left + width, top),
        GizmoHandle.Right => (left + width, top + height / 2),
        GizmoHandle.BottomRight => (left + width, top + height),
        GizmoHandle.Bottom => (left + width / 2, top + height),
        GizmoHandle.BottomLeft => (left, top + height),
        _ => (left, top + height / 2)
    };

    private GizmoRect CurrentRect() => TransformGizmo.RectFor(
        _viewModel.ScaleX, _viewModel.ScaleY, _viewModel.PositionX, _viewModel.PositionY,
        _viewModel.ProjectWidth, _viewModel.ProjectHeight);

    private Point ToProject(Point stagePoint) => new(
        (stagePoint.X - _originX) / _viewScale,
        (stagePoint.Y - _originY) / _viewScale);

    // ---------- Mouse interaction ----------

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var projectPoint = ToProject(e.GetPosition(Stage));
        var hit = TransformGizmo.HitTest(
            CurrentRect(), projectPoint.X, projectPoint.Y, HitRadiusScreen / _viewScale);
        if (hit is null) return;

        _dragHandle = hit;
        _dragStartProject = projectPoint;
        _dragStartValues = new[]
        {
            _viewModel.ScaleX, _viewModel.ScaleY, _viewModel.PositionX, _viewModel.PositionY
        };
        Stage.CaptureMouse();
        e.Handled = true;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var projectPoint = ToProject(e.GetPosition(Stage));

        if (_dragHandle is not { } handle)
        {
            Stage.Cursor = CursorFor(TransformGizmo.HitTest(
                CurrentRect(), projectPoint.X, projectPoint.Y, HitRadiusScreen / _viewScale));
            return;
        }

        var (scaleX, scaleY, positionX, positionY) = TransformGizmo.Drag(
            _dragStartValues[0], _dragStartValues[1], _dragStartValues[2], _dragStartValues[3],
            handle,
            projectPoint.X - _dragStartProject.X,
            projectPoint.Y - _dragStartProject.Y,
            _viewModel.ProjectWidth, _viewModel.ProjectHeight,
            _viewModel.LockAspect);

        if (handle == GizmoHandle.Move)
        {
            var snap = CenterSnapScreen / _viewScale;
            positionX = TransformGizmo.SnapOffset(positionX, snap);
            positionY = TransformGizmo.SnapOffset(positionY, snap);
        }

        _viewModel.SetTransform(scaleX, scaleY, positionX, positionY);
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragHandle is null) return;
        _dragHandle = null;
        Stage.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static Cursor CursorFor(GizmoHandle? handle) => handle switch
    {
        GizmoHandle.Move => Cursors.SizeAll,
        GizmoHandle.TopLeft or GizmoHandle.BottomRight => Cursors.SizeNWSE,
        GizmoHandle.TopRight or GizmoHandle.BottomLeft => Cursors.SizeNESW,
        GizmoHandle.Left or GizmoHandle.Right => Cursors.SizeWE,
        GizmoHandle.Top or GizmoHandle.Bottom => Cursors.SizeNS,
        _ => Cursors.Arrow
    };

    /// <summary>Esc cancels an active drag (back to where the drag started).</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _dragHandle is null) return;
        _dragHandle = null;
        Stage.ReleaseMouseCapture();
        _viewModel.SetTransform(
            _dragStartValues[0], _dragStartValues[1], _dragStartValues[2], _dragStartValues[3]);
        e.Handled = true;
    }

    // ---------- Details panel ----------

    private bool _updatingFields;

    private void UpdateFields()
    {
        _updatingFields = true;
        ScaleXBox.Text = Math.Round(_viewModel.ScaleX * 100).ToString(CultureInfo.InvariantCulture);
        ScaleYBox.Text = Math.Round(_viewModel.ScaleY * 100).ToString(CultureInfo.InvariantCulture);
        PositionXBox.Text = Math.Round(_viewModel.PositionX).ToString(CultureInfo.InvariantCulture);
        PositionYBox.Text = Math.Round(_viewModel.PositionY).ToString(CultureInfo.InvariantCulture);
        _updatingFields = false;
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyFields();
        e.Handled = true;
    }

    private void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_updatingFields) ApplyFields();
    }

    private void ApplyFields()
    {
        double Parse(TextBox box, double fallback) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        _viewModel.SetTransform(
            Parse(ScaleXBox, _viewModel.ScaleX * 100) / 100.0,
            Parse(ScaleYBox, _viewModel.ScaleY * 100) / 100.0,
            Parse(PositionXBox, _viewModel.PositionX),
            Parse(PositionYBox, _viewModel.PositionY));
    }

    private void LockAspect_Changed(object sender, RoutedEventArgs e)
    {
        // The Checked event can fire while InitializeComponent is still running.
        if (_viewModel is null) return;
        _viewModel.LockAspect = LockAspectCheck.IsChecked == true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => _viewModel.Reset();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Commit();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing without OK rolls the clip back to its original transform.</summary>
    private void Window_Closed(object? sender, EventArgs e) => _viewModel.RevertIfUncommitted();
}
