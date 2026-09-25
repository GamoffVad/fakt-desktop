using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Fakt.Core.Security;
using Fakt.Core.Settings;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;

namespace Fakt.Desktop.ViewModels.Admin;

/// <summary>Вкладка «Обработка»: chunk Pandas, SQL-пакет, очереди, повторы, бюджет, образец, worker.</summary>
public sealed class ProcessingSettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _message;
    private MessageKind _messageKind;

    public ProcessingSettingsViewModel(AppServices services)
    {
        _services = services;
        Load(services.Settings.Current.Processing);
        SaveCommand = new RelayCommand(Save, () => CanEdit);
        ResetDefaultsCommand = new RelayCommand(ResetDefaults, () => CanEdit);
        ProbeCommand = new AsyncCommand(ProbeAsync, onError: ex =>
        {
            Message = ex.Message;
            MessageKind = MessageKind.Error;
        });
        try
        {
            var detected = Fakt.Infrastructure.Worker.WorkerClientFactory.Resolve(null, null, Fakt.Infrastructure.Settings.AppPaths.ApplicationDirectory);
            DetectedPythonPath = detected.PythonPath;
            DetectedWorkerDirectory = detected.WorkerDirectory;
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            // Пути будут определены при запуске worker; подсказка в полях не обязательна.
        }
    }

    private void Load(ProcessingSettings p)
    {
        RecursiveScan = p.RecursiveScan;
        FollowReparsePoints = p.FollowReparsePoints;
        IncludeHidden = p.IncludeHiddenFiles;
        SampleLines = p.SampleLines.ToString(CultureInfo.InvariantCulture);
        SampleMaxKb = (p.SampleMaxBytes / 1024).ToString(CultureInfo.InvariantCulture);
        ExtendedLines = p.ExtendedSampleLines.ToString(CultureInfo.InvariantCulture);
        ExtendedMaxKb = (p.ExtendedSampleMaxBytes / 1024).ToString(CultureInfo.InvariantCulture);
        PreviewRecords = p.PreviewRecords.ToString(CultureInfo.InvariantCulture);
        ChunkSize = p.ChunkSize.ToString(CultureInfo.InvariantCulture);
        SqlBatchSize = p.SqlBatchSize.ToString(CultureInfo.InvariantCulture);
        QueueCapacity = p.QueueCapacity.ToString(CultureInfo.InvariantCulture);
        MaxAttempts = p.MaxAttemptsPerRequest.ToString(CultureInfo.InvariantCulture);
        BudgetRequests = p.BudgetMaxRequests.ToString(CultureInfo.InvariantCulture);
        BudgetTokens = p.BudgetMaxTokens.ToString(CultureInfo.InvariantCulture);
        ConfirmAbove = p.ConfirmStructureRequestsAbove.ToString(CultureInfo.InvariantCulture);
        PythonPath = p.PythonPath;
        WorkerDirectory = p.WorkerDirectory;
    }

    /// <summary>Рекомендуемые значения; сохраняются только по кнопке «Сохранить».</summary>
    private void ResetDefaults()
    {
        Load(new ProcessingSettings());
        OnPropertyChanged(string.Empty);
        Message = "Подставлены значения по умолчанию (Python и worker — встроенные, рядом с приложением). Нажмите «Сохранить параметры обработки».";
        MessageKind = MessageKind.Info;
    }

    public bool CanEdit => _services.Authorization.IsAllowed(Permission.ManageSettings);
    public bool IsReadOnly => !CanEdit;
    public ICommand SaveCommand { get; }
    public ICommand ResetDefaultsCommand { get; }
    public AsyncCommand ProbeCommand { get; }

    /// <summary>Какой python.exe и каталог worker используются, если поля оставлены пустыми.</summary>
    public string DetectedPythonPath { get; }
    public string DetectedWorkerDirectory { get; }

    public bool RecursiveScan { get; set; }
    public bool FollowReparsePoints { get; set; }
    public bool IncludeHidden { get; set; }
    public string SampleLines { get; set; }
    public string SampleMaxKb { get; set; }
    public string ExtendedLines { get; set; }
    public string ExtendedMaxKb { get; set; }
    public string PreviewRecords { get; set; }
    public string ChunkSize { get; set; }
    public string SqlBatchSize { get; set; }
    public string QueueCapacity { get; set; }
    public string MaxAttempts { get; set; }
    public string BudgetRequests { get; set; }
    public string BudgetTokens { get; set; }
    public string ConfirmAbove { get; set; }
    public string PythonPath { get; set; }
    public string WorkerDirectory { get; set; }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public MessageKind MessageKind { get => _messageKind; private set => SetProperty(ref _messageKind, value); }

    private static int Int(string text, string what, int min, int max)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new ArgumentException($"{what}: целое число от {min} до {max}.");
        }

        return value;
    }

    private void Save()
    {
        try
        {
            var settings = new ProcessingSettings
            {
                RecursiveScan = RecursiveScan,
                FollowReparsePoints = FollowReparsePoints,
                IncludeHiddenFiles = IncludeHidden,
                SampleLines = Int(SampleLines, "Строк образца", 1, 1000),
                SampleMaxBytes = Int(SampleMaxKb, "Предел образца, КиБ", 1, 16384) * 1024,
                ExtendedSampleLines = Int(ExtendedLines, "Строк расширенного образца", 1, 1000),
                ExtendedSampleMaxBytes = Int(ExtendedMaxKb, "Предел расширенного образца, КиБ", 1, 16384) * 1024,
                PreviewRecords = Int(PreviewRecords, "Записей предпросмотра", 1, 1000),
                ChunkSize = Int(ChunkSize, "Размер chunk Pandas", 100, 100000),
                SqlBatchSize = Int(SqlBatchSize, "Размер SQL-пакета", 1, 10000),
                QueueCapacity = Int(QueueCapacity, "Предел очереди", 1, 256),
                MaxAttemptsPerRequest = Int(MaxAttempts, "Попыток на запрос", 1, 20),
                BudgetMaxRequests = Int(BudgetRequests, "Предел запросов задания", 0, int.MaxValue),
                BudgetMaxTokens = long.TryParse(BudgetTokens?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) && tokens >= 0 ? tokens : throw new ArgumentException("Предел токенов: целое число ≥ 0."),
                ConfirmStructureRequestsAbove = Int(ConfirmAbove, "Порог подтверждения", 0, 100000),
                PythonPath = string.IsNullOrWhiteSpace(PythonPath) ? null : PythonPath.Trim(),
                WorkerDirectory = string.IsNullOrWhiteSpace(WorkerDirectory) ? null : WorkerDirectory.Trim(),
            };
            _services.Settings.SaveProcessing(settings);
            Message = "Параметры обработки сохранены. Выполняющиеся задания используют параметры своего снимка.";
            MessageKind = MessageKind.Success;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is AccessDeniedException)
        {
            Message = ex.Message;
            MessageKind = MessageKind.Error;
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        Message = "Проверка worker…";
        MessageKind = MessageKind.Info;
        var hello = await Task.Run(() => _services.Workers.ProbeAsync(cancellationToken), cancellationToken);
        Message = $"Worker {hello.WorkerVersion} работает: Python {hello.PythonVersion}, pandas {hello.PandasVersion}, numpy {hello.NumpyVersion}, defusedxml {hello.DefusedXmlVersion}; {hello.Platform}.";
        MessageKind = MessageKind.Success;
    }
}

public sealed class PrincipalItemViewModel
{
    public PrincipalItemViewModel(PrincipalEntry entry)
    {
        Entry = entry;
    }

    public PrincipalEntry Entry { get; }
    public string DisplayName => Entry.DisplayName;
    public string Sid => Entry.Sid;
    public string KindText => Entry.IsGroup ? "группа" : "учётная запись или группа";
}

/// <summary>Вкладка «Доступ»: роли по пользователям и группам Windows (SID), область хранения секретов.</summary>
public sealed class AccessSettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _newAdmin;
    private string _newOperator;
    private SecretScope _scope;
    private string _message;
    private MessageKind _messageKind;

    public AccessSettingsViewModel(AppServices services)
    {
        _services = services;
        var access = services.Settings.Current.Access;
        Administrators = new ObservableCollection<PrincipalItemViewModel>(access.Administrators.Select(p => new PrincipalItemViewModel(p)));
        Operators = new ObservableCollection<PrincipalItemViewModel>(access.Operators.Select(p => new PrincipalItemViewModel(p)));
        _scope = access.SecretScope;
        AddAdminCommand = new RelayCommand(() => Add(NewAdmin, Administrators, () => NewAdmin = null), () => CanEdit && !string.IsNullOrWhiteSpace(NewAdmin));
        AddOperatorCommand = new RelayCommand(() => Add(NewOperator, Operators, () => NewOperator = null), () => CanEdit && !string.IsNullOrWhiteSpace(NewOperator));
        RemoveAdminCommand = new RelayCommand(p => Administrators.Remove(p as PrincipalItemViewModel), _ => CanEdit);
        RemoveOperatorCommand = new RelayCommand(p => Operators.Remove(p as PrincipalItemViewModel), _ => CanEdit);
        SaveCommand = new RelayCommand(Save, () => CanEdit);
    }

    public bool CanEdit => _services.Authorization.IsAllowed(Permission.ManageAccess);
    public bool IsReadOnly => !CanEdit;
    public ObservableCollection<PrincipalItemViewModel> Administrators { get; }
    public ObservableCollection<PrincipalItemViewModel> Operators { get; }
    public ICommand AddAdminCommand { get; }
    public ICommand AddOperatorCommand { get; }
    public ICommand RemoveAdminCommand { get; }
    public ICommand RemoveOperatorCommand { get; }
    public ICommand SaveCommand { get; }

    public string CurrentUser => $"{_services.Identity.UserName} — {RolePermissions.Title(_services.Authorization.CurrentRole)}";
    public string OwnerText
    {
        get
        {
            var access = _services.Settings.Current.Access;
            return $"Владелец: {access.OwnerName} (назначен {access.OwnerAssignedAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm}). Владелец всегда имеет роль «Администратор».";
        }
    }

    public string NewAdmin { get => _newAdmin; set => SetProperty(ref _newAdmin, value); }
    public string NewOperator { get => _newOperator; set => SetProperty(ref _newOperator, value); }
    public bool ScopeCurrentUser { get => _scope == SecretScope.CurrentUser; set { if (value) { _scope = SecretScope.CurrentUser; OnPropertyChanged(nameof(ScopeLocalMachine)); } } }
    public bool ScopeLocalMachine { get => _scope == SecretScope.LocalMachine; set { if (value) { _scope = SecretScope.LocalMachine; OnPropertyChanged(nameof(ScopeCurrentUser)); } } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public MessageKind MessageKind { get => _messageKind; private set => SetProperty(ref _messageKind, value); }

    private void Add(string name, ObservableCollection<PrincipalItemViewModel> target, Action clear)
    {
        var resolved = _services.Identity.Resolve(name);
        if (resolved == null)
        {
            Message = $"Учётная запись или группа «{name}» не найдена. Формат: ДОМЕН\\имя, КОМПЬЮТЕР\\имя или SID.";
            MessageKind = MessageKind.Warning;
            return;
        }

        if (target.All(p => p.Sid != resolved.Sid))
        {
            target.Add(new PrincipalItemViewModel(new PrincipalEntry { Sid = resolved.Sid, DisplayName = resolved.DisplayName, IsGroup = resolved.IsGroup }));
        }

        clear();
        Message = null;
    }

    private void Save()
    {
        var access = _services.Settings.Current.Access;
        if (_scope != access.SecretScope &&
            !_services.Dialogs.Confirm("Изменение области хранения секретов",
                "Сохранённые ключи API и пароль SQL останутся в прежней области и станут недоступны: их нужно будет ввести заново.", "Изменить область"))
        {
            return;
        }

        try
        {
            _services.Settings.SaveAccess(Administrators.Select(a => a.Entry), Operators.Select(o => o.Entry), _scope);
            Message = "Роли сохранены. Проверка роли выполняется в сервисах при каждом действии.";
            MessageKind = MessageKind.Success;
        }
        catch (AccessDeniedException ex)
        {
            Message = ex.Message;
            MessageKind = MessageKind.Error;
        }
    }
}

public sealed class AdminViewModel : ObservableObject, IPageActivation
{
    private readonly AppServices _services;
    private int _selectedTab;

    public AdminViewModel(AppServices services)
    {
        _services = services;
        Llm = new LlmSettingsViewModel(services);
        Database = new DatabaseSettingsViewModel(services);
        Processing = new ProcessingSettingsViewModel(services);
        Access = new AccessSettingsViewModel(services);
    }

    public LlmSettingsViewModel Llm { get; }
    public DatabaseSettingsViewModel Database { get; }
    public ProcessingSettingsViewModel Processing { get; }
    public AccessSettingsViewModel Access { get; }

    public int SelectedTab { get => _selectedTab; set => SetProperty(ref _selectedTab, value); }

    public bool IsReadOnly => !_services.Authorization.IsAllowed(Permission.ManageSettings);

    public string ReadOnlyNotice => IsReadOnly ? "Настройки доступны только для просмотра: изменять их может роль «Администратор»." : null;

    public void OnActivated()
    {
    }
}
