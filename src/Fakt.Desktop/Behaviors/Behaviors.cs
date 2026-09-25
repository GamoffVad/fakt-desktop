using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Fakt.Desktop.Behaviors;

/// <summary>Столбцы предпросмотра строятся по списку имён; ячейка — шаблон PreviewCellTemplate (NULL/пусто различаются).</summary>
public static class PreviewColumns
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
        "Columns", typeof(IList<string>), typeof(PreviewColumns), new PropertyMetadata(null, OnColumnsChanged));

    public static IList<string> GetColumns(DependencyObject element) => (IList<string>)element.GetValue(ColumnsProperty);

    public static void SetColumns(DependencyObject element, IList<string> value) => element.SetValue(ColumnsProperty, value);

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is DataGrid grid))
        {
            return;
        }

        grid.Columns.Clear();
        if (!(e.NewValue is IList<string> columns))
        {
            return;
        }

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "№",
            Binding = new Binding("Ordinal"),
            IsReadOnly = true,
            CellStyle = grid.TryFindResource("DataGrid.Cell.Index") as Style,
            ElementStyle = grid.TryFindResource("Text.Cell.Numeric") as Style,
        });

        var template = grid.TryFindResource("PreviewCellTemplate") as DataTemplate;
        for (var i = 0; i < columns.Count; i++)
        {
            var factory = new FrameworkElementFactory(typeof(ContentPresenter));
            factory.SetBinding(ContentPresenter.ContentProperty, new Binding($"Cells[{i}]"));
            factory.SetValue(ContentPresenter.ContentTemplateProperty, template);
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = columns[i],
                CellTemplate = new DataTemplate { VisualTree = factory },
                IsReadOnly = true,
                MinWidth = 80,
                MaxWidth = 420,
            });
        }
    }
}

/// <summary>Подсветка совпадений в тексте: исходная строка не изменяется, совпадения выделяются фоном.</summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TermsProperty = DependencyProperty.RegisterAttached(
        "Terms", typeof(IReadOnlyList<string>), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
    public static IReadOnlyList<string> GetTerms(DependencyObject element) => (IReadOnlyList<string>)element.GetValue(TermsProperty);
    public static void SetTerms(DependencyObject element, IReadOnlyList<string> value) => element.SetValue(TermsProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is TextBlock block))
        {
            return;
        }

        block.Inlines.Clear();
        var text = GetText(block) ?? string.Empty;
        var terms = (GetTerms(block) ?? Array.Empty<string>())
            .Select(t => t?.Trim().TrimEnd('*'))
            .Where(t => !string.IsNullOrEmpty(t) && t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (terms.Count == 0 || text.Length == 0)
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        var brush = block.TryFindResource("Brush.Highlight") as Brush ?? Brushes.Khaki;
        var position = 0;
        while (position < text.Length)
        {
            var bestIndex = -1;
            var bestLength = 0;
            foreach (var term in terms)
            {
                var index = text.IndexOf(term, position, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (bestIndex < 0 || index < bestIndex || (index == bestIndex && term.Length > bestLength)))
                {
                    bestIndex = index;
                    bestLength = term.Length;
                }
            }

            if (bestIndex < 0)
            {
                block.Inlines.Add(new Run(text.Substring(position)));
                break;
            }

            if (bestIndex > position)
            {
                block.Inlines.Add(new Run(text.Substring(position, bestIndex - position)));
            }

            block.Inlines.Add(new Run(text.Substring(bestIndex, bestLength)) { Background = brush, FontWeight = FontWeights.SemiBold });
            position = bestIndex + bestLength;
        }
    }
}

/// <summary>Связывание PasswordBox с моделью представления (значение не отображается и не журналируется).</summary>
public static class PasswordBinding
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(PasswordBinding), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBinding));

    // Обработчик ввода регистрируется для всех PasswordBox сразу: раньше он подключался только при изменении значения
    // со стороны модели, и при пустом начальном значении набранный ключ или пароль не попадал в модель.
    static PasswordBinding()
    {
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent, new RoutedEventHandler(OnPasswordChanged));
    }

    public static string GetValue(DependencyObject element) => (string)element.GetValue(ValueProperty);
    public static void SetValue(DependencyObject element, string value) => element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox box && !(bool)box.GetValue(UpdatingProperty))
        {
            var value = e.NewValue as string ?? string.Empty;
            if (box.Password != value)
            {
                // Значение пришло из модели: обратно в модель его не отправляем.
                box.SetValue(UpdatingProperty, true);
                try
                {
                    box.Password = value;
                }
                finally
                {
                    box.SetValue(UpdatingProperty, false);
                }
            }
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if ((bool)box.GetValue(UpdatingProperty) || !BindingOperations.IsDataBound(box, ValueProperty))
        {
            return;
        }

        box.SetValue(UpdatingProperty, true);
        try
        {
            SetValue(box, box.Password);
        }
        finally
        {
            box.SetValue(UpdatingProperty, false);
        }
    }
}

/// <summary>Команда при активации строки таблицы: щелчок по строке, двойной щелчок или Enter.</summary>
public static class RowActivation
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(RowActivation), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand GetCommand(DependencyObject element) => (ICommand)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is DataGrid grid))
        {
            return;
        }

        grid.PreviewKeyDown -= OnKeyDown;
        grid.MouseLeftButtonUp -= OnMouseUp;
        if (e.NewValue != null)
        {
            grid.PreviewKeyDown += OnKeyDown;
            grid.MouseLeftButtonUp += OnMouseUp;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var grid = (DataGrid)sender;
        Execute(grid, grid.SelectedItem);
        e.Handled = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row != null)
        {
            Execute(grid, row.Item);
        }
    }

    private static void Execute(DataGrid grid, object item)
    {
        var command = GetCommand(grid);
        if (item != null && command?.CanExecute(item) == true)
        {
            command.Execute(item);
        }
    }

    internal static T FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T found)
            {
                return found;
            }

            child = child is Visual || child is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }

        return null;
    }
}

/// <summary>Enter в поле ввода выполняет команду (поиск запускается по Enter, а не по каждому нажатию клавиши).</summary>
public static class EnterKey
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(EnterKey), new PropertyMetadata(null, OnChanged));

    public static ICommand GetCommand(DependencyObject element) => (ICommand)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand value) => element.SetValue(CommandProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            element.PreviewKeyDown -= OnKeyDown;
            if (e.NewValue != null)
            {
                element.PreviewKeyDown += OnKeyDown;
            }
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var element = (DependencyObject)sender;
        if (sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        var command = GetCommand(element);
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
