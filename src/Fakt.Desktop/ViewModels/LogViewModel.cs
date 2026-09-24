using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using Fakt.Core.Logging;
using Fakt.Core.Security;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;

namespace Fakt.Desktop.ViewModels;

public sealed class LogEntryViewModel
{
    public LogEntryViewModel(LogEntry entry)
    {
        Entry = entry;
    }

    public LogEntry Entry { get; }
    public DateTime Time => Entry.TimestampUtc.ToLocalTime();
    public string LevelText => Entry.Level switch
    {
        LogLevel.Error => "Ошибка",
        LogLevel.Warning => "Предупреждение",
        LogLevel.Debug => "Подробно",
        _ => "Сведения",
    };

    public Fakt.Core.Files.StatusTone Tone => Entry.Level switch
    {
        LogLevel.Error => Fakt.Core.Files.StatusTone.Error,
        LogLevel.Warning => Fakt.Core.Files.StatusTone.Warning,
        LogLevel.Debug => Fakt.Core.Files.StatusTone.Neutral,
        _ => Fakt.Core.Files.StatusTone.Info,
    };

    public string CategoryText => Entry.Category switch
    {
        ErrorCategory.Connection => "Подключение",
        ErrorCategory.DataFormat => "Формат данных",
        ErrorCategory.ModelResponse => "Ответ модели",
        ErrorCategory.Configuration => "Настройка",
        ErrorCategory.Access => "Доступ",
        ErrorCategory.Internal => "Внутренняя",
        _ => null,
    };

    public string Event => Entry.Event;
    public string Message => Entry.Message;

    public string Context
    {
        get
        {
            var parts = new List<string>();
            if (Entry.JobId.HasValue) parts.Add("задание " + Entry.JobId);
            if (Entry.FileId.HasValue) parts.Add("файл " + Entry.FileId);
            if (Entry.SourceRowId != null) parts.Add(Entry.SourceRowId);
            if (Entry.Stage != null) parts.Add(Entry.Stage);
            if (Entry.DurationMs.HasValue) parts.Add(Entry.DurationMs + " мс");
            if (Entry.Count.HasValue) parts.Add("кол-во " + Entry.Count);
            if (Entry.ErrorCode != null) parts.Add("код " + Entry.ErrorCode);
            if (Entry.ProviderRequestId != null) parts.Add("request " + Entry.ProviderRequestId);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Журнал технических событий без секретов и полных персональных данных; фильтры и подробный режим (администратор).</summary>
public sealed class LogViewModel : ObservableObject, IPageActivation
{
    private readonly AppServices _services;
    private string _levelFilter = "Все";
    private string _textFilter;
    private bool _includeVerbose;

    public LogViewModel(AppServices services)
    {
        _services = services;
        Entries = new ObservableCollection<LogEntryViewModel>();
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = Filter;
        RefreshCommand = new RelayCommand(Load);
        OpenFolderCommand = new RelayCommand(() => System.Diagnostics.Process.Start("explorer.exe", "\"" + services.Paths.LogDirectory + "\""));
        EnableVerboseCommand = new RelayCommand(p => SetVerbose(TimeSpan.FromHours(Convert.ToDouble(p ?? 1))), _ => CanManageDiagnostics);
        DisableVerboseCommand = new RelayCommand(() => SetVerbose(null), () => CanManageDiagnostics && IsVerbose);
        services.Logger.EntryWritten += entry => UiThread.Post(() =>
        {
            Entries.Insert(0, new LogEntryViewModel(entry));
            while (Entries.Count > 5000)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        });
    }

    public ObservableCollection<LogEntryViewModel> Entries { get; }
    public ICollectionView EntriesView { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand EnableVerboseCommand { get; }
    public ICommand DisableVerboseCommand { get; }

    public IReadOnlyList<string> Levels { get; } = new[] { "Все", "Ошибки", "Предупреждения и ошибки", "Сведения", "Подробно" };

    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (SetProperty(ref _levelFilter, value))
            {
                EntriesView.Refresh();
            }
        }
    }

    public string TextFilter
    {
        get => _textFilter;
        set
        {
            if (SetProperty(ref _textFilter, value))
            {
                EntriesView.Refresh();
            }
        }
    }

    public bool IncludeVerbose
    {
        get => _includeVerbose;
        set
        {
            if (SetProperty(ref _includeVerbose, value))
            {
                Load();
            }
        }
    }

    public bool CanManageDiagnostics => _services.Authorization.IsAllowed(Permission.ManageDiagnostics);

    public bool IsVerbose => _services.Settings.Current.Diagnostics.IsVerboseActive(DateTime.UtcNow);

    public string VerboseText
    {
        get
        {
            var diagnostics = _services.Settings.Current.Diagnostics;
            return diagnostics.IsVerboseActive(DateTime.UtcNow)
                ? $"Подробный журнал включён до {diagnostics.VerboseUntilUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm} ({diagnostics.VerboseEnabledBy}). Хранится {diagnostics.VerboseRetentionDays} дн."
                : $"Подробный журнал выключен. Обычный журнал хранится {diagnostics.RetentionDays} дн.; секреты и содержимое записей в него не пишутся.";
        }
    }

    public string LogFolder => _services.Paths.LogDirectory;

    public void OnActivated() => Load();

    private void Load()
    {
        Entries.Clear();
        foreach (var entry in _services.Logger.ReadLatest(3000, IncludeVerbose))
        {
            Entries.Add(new LogEntryViewModel(entry));
        }

        OnPropertiesChanged(nameof(VerboseText), nameof(IsVerbose));
    }

    private bool Filter(object item)
    {
        var entry = (LogEntryViewModel)item;
        var level = entry.Entry.Level;
        var levelOk = LevelFilter switch
        {
            "Ошибки" => level == LogLevel.Error,
            "Предупреждения и ошибки" => level == LogLevel.Error || level == LogLevel.Warning,
            "Сведения" => level == LogLevel.Info,
            "Подробно" => level == LogLevel.Debug,
            _ => true,
        };
        if (!levelOk)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(TextFilter))
        {
            return true;
        }

        var text = TextFilter.Trim();
        return (entry.Message?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (entry.Event?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               entry.Context.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SetVerbose(TimeSpan? duration)
    {
        try
        {
            _services.Settings.SetVerboseDiagnostics(duration);
            OnPropertiesChanged(nameof(VerboseText), nameof(IsVerbose));
        }
        catch (AccessDeniedException ex)
        {
            _services.Dialogs.Show("Недостаточно прав", ex.Message, MessageKind.Warning);
        }
    }
}
