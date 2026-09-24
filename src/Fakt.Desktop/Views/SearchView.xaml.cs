using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Fakt.Desktop.ViewModels;

namespace Fakt.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => QueryBox.Focus();
    }

    /// <summary>Фокус переводится в карточку при открытии и возвращается в строку поиска при закрытии.</summary>
    private void OnOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() => CloseCardButton.Focus()));
        }
        else
        {
            QueryBox.Focus();
        }
    }

    private void OnOverlayBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SearchViewModel viewModel && viewModel.CloseCardCommand.CanExecute(null))
        {
            viewModel.CloseCardCommand.Execute(null);
        }
    }
}
