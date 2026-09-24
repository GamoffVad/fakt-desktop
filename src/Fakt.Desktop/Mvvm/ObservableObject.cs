using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Fakt.Desktop.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>Выполнение в потоке интерфейса. Фоновые операции сообщают прогресс через него с ограниченной частотой.</summary>
public static class UiThread
{
    public static Dispatcher Dispatcher => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public static void Post(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    public static void Invoke(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
