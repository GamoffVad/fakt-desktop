using System.Windows;
using System.Windows.Input;
using Fakt.Desktop.Services;

namespace Fakt.Desktop.Views.Dialogs;

public partial class DialogWindow : Window
{
    public DialogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Фокус на первом интерактивном элементе содержимого; иначе — на основной кнопке.
            if (!MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
            {
                PrimaryButton.Focus();
            }
        };
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (DataContext is DialogViewModel viewModel && !viewModel.OnConfirm())
        {
            return;
        }

        DialogResult = true;
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
