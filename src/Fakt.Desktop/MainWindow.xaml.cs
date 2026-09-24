using System.ComponentModel;
using System.Windows;
using Fakt.Desktop.ViewModels;

namespace Fakt.Desktop;

public partial class MainWindow : Window
{
    /// <summary>Ширина, ниже которой навигация сворачивается до полосы значков (DESIGN.md, раздел 5.2).</summary>
    public const double CompactWidth = 1280;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsCompact = ActualWidth < CompactWidth;
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && !viewModel.Processing.ConfirmClose())
        {
            e.Cancel = true;
        }
    }
}
