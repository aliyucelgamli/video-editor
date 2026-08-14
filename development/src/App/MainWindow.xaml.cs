using System.ComponentModel;
using System.Windows;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.ConfirmDiscardChanges())
            e.Cancel = true;
        base.OnClosing(e);
    }
}
