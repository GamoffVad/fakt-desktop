using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Fakt.Desktop.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object> _execute;
    private readonly Func<object, bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
        : this(_ => execute(), canExecute == null ? null : new Func<object, bool>(_ => canExecute()))
    {
    }

    public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object parameter) => _execute(parameter);
}

/// <summary>
/// Асинхронная команда: повторный запуск во время выполнения блокируется, ошибки не роняют приложение
/// и передаются обработчику, поддерживается отмена.
/// </summary>
public sealed class AsyncCommand : ObservableObject, ICommand
{
    private readonly Func<object, CancellationToken, Task> _execute;
    private readonly Func<object, bool> _canExecute;
    private readonly Action<Exception> _onError;
    private CancellationTokenSource _cts;
    private bool _isRunning;

    public AsyncCommand(Func<CancellationToken, Task> execute, Func<bool> canExecute = null, Action<Exception> onError = null)
        : this((_, ct) => execute(ct), canExecute == null ? null : new Func<object, bool>(_ => canExecute()), onError)
    {
    }

    public AsyncCommand(Func<object, CancellationToken, Task> execute, Func<object, bool> canExecute = null, Action<Exception> onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
    }

    public event EventHandler CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public ICommand CancelCommand { get; }

    public bool CanExecute(object parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object parameter)
    {
        await ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(object parameter = null)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        try
        {
            await _execute(parameter, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_onError != null)
            {
                _onError(ex);
            }
            else
            {
                throw;
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
