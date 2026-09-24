using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Views.Dialogs;

namespace Fakt.Desktop.Services;

public enum MessageKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Модель содержимого диалога: заголовок, кнопки и результат. Конкретное содержимое — через DataTemplate.</summary>
public class DialogViewModel : ObservableObject
{
    private string _title;
    private string _primaryText = "OK";
    private string _secondaryText;
    private bool _isDanger;
    private bool _canConfirm = true;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string PrimaryText
    {
        get => _primaryText;
        set => SetProperty(ref _primaryText, value);
    }

    /// <summary>null — кнопки отмены нет.</summary>
    public string SecondaryText
    {
        get => _secondaryText;
        set => SetProperty(ref _secondaryText, value);
    }

    public bool IsDanger
    {
        get => _isDanger;
        set => SetProperty(ref _isDanger, value);
    }

    public bool CanConfirm
    {
        get => _canConfirm;
        set => SetProperty(ref _canConfirm, value);
    }

    public double Width { get; set; } = 560;

    /// <summary>Проверка перед закрытием по основной кнопке; false — диалог остаётся открытым.</summary>
    public virtual bool OnConfirm() => true;
}

public sealed class MessageDialogViewModel : DialogViewModel
{
    public string Message { get; set; }
    public string Details { get; set; }
    public MessageKind Kind { get; set; }
}

public interface IDialogService
{
    void Show(string title, string message, MessageKind kind = MessageKind.Info, string details = null);

    bool Confirm(string title, string message, string confirmText = "Продолжить", string cancelText = "Отмена", bool danger = false, string details = null);

    bool ShowDialog(DialogViewModel viewModel);

    string PickFolder(string title, string initialFolder);

    bool CopyToClipboard(string text);
}

public sealed class DialogService : IDialogService
{
    private static Window Owner =>
        System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow;

    public void Show(string title, string message, MessageKind kind = MessageKind.Info, string details = null)
    {
        ShowDialog(new MessageDialogViewModel { Title = title, Message = message, Kind = kind, Details = details, PrimaryText = "Закрыть" });
    }

    public bool Confirm(string title, string message, string confirmText = "Продолжить", string cancelText = "Отмена", bool danger = false, string details = null)
    {
        return ShowDialog(new MessageDialogViewModel
        {
            Title = title,
            Message = message,
            Details = details,
            Kind = danger ? MessageKind.Warning : MessageKind.Info,
            PrimaryText = confirmText,
            SecondaryText = cancelText,
            IsDanger = danger,
        });
    }

    public bool ShowDialog(DialogViewModel viewModel)
    {
        var result = false;
        UiThread.Invoke(() =>
        {
            var window = new DialogWindow { DataContext = viewModel, Owner = Owner };
            if (window.Owner == null)
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            result = window.ShowDialog() == true;
        });
        return result;
    }

    public string PickFolder(string title, string initialFolder)
    {
        string path = null;
        UiThread.Invoke(() => path = FolderPicker.Pick(Owner, title, initialFolder));
        return path;
    }

    /// <summary>Буфер обмена может быть занят другим процессом: несколько попыток без аварии.</summary>
    public bool CopyToClipboard(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text ?? string.Empty, true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }
}
