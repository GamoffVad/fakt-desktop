using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Fakt.Core.Llm;
using Fakt.Desktop.ViewModels.Admin;

namespace Fakt.Desktop.Views.Admin;

public partial class LlmSettingsView : UserControl
{
    private TextBox _search;

    public LlmSettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Раскрытый список моделей: курсор сразу в поле поиска внутри списка.</summary>
    private void OnModelDropDownOpened(object sender, EventArgs e)
    {
        if (_search == null)
        {
            _search = ModelBox.Template.FindName("PART_Search", ModelBox) as TextBox;
            if (_search != null)
            {
                _search.PreviewKeyDown += OnSearchKeyDown;
            }
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_search != null)
            {
                _search.Focus();
                Keyboard.Focus(_search);
            }
        }));
    }

    /// <summary>Enter — выбрать первую найденную модель (или использовать введённый ID); стрелка вниз — к списку; Esc — закрыть.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (!(DataContext is LlmSettingsViewModel viewModel))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                var first = viewModel.ModelsView.Cast<ModelInfo>().FirstOrDefault();
                if (first != null)
                {
                    viewModel.SelectedModel = first;
                    viewModel.IsModelListOpen = false;
                }
                else if (viewModel.UseTypedModelCommand.CanExecute(null))
                {
                    viewModel.UseTypedModelCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.Down:
                if (ModelBox.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem item)
                {
                    item.Focus();
                }

                e.Handled = true;
                break;
            case Key.Escape:
                viewModel.IsModelListOpen = false;
                e.Handled = true;
                break;
        }
    }
}
