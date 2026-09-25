using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Fakt.Application.Services;
using Fakt.Core.Security;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;
using Fakt.Infrastructure.Sql;

namespace Fakt.Desktop.ViewModels.Admin;

public sealed class ColumnMappingViewModel : ObservableObject
{
    private string _physical;

    public ColumnMappingViewModel(string key, string title, string table, string physical, string expected)
    {
        Key = key;
        Title = title;
        Table = table;
        _physical = physical;
        Expected = expected;
    }

    public string Key { get; }
    public string Title { get; }
    public string Table { get; }
    public string Expected { get; }
    public ObservableCollection<string> Available { get; } = new();

    public string Physical
    {
        get => _physical;
        set => SetProperty(ref _physical, value);
    }
}

public sealed class MigrationItemViewModel
{
    public MigrationItemViewModel(MigrationInfo info)
    {
        Info = info;
    }

    public MigrationInfo Info { get; }
    public string Id => Info.Id;
    public string Title => Info.Title;
    public string Description => Info.Description;
    public string StateText => Info.Applied ? $"Применена {Info.AppliedAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm}" + (Info.AppliedBy != null ? " · " + Info.AppliedBy : string.Empty) : "Не применена";
    public string FlagsText
    {
        get
        {
            var flags = new List<string>();
            if (Info.AltersUserTables) flags.Add("добавляет столбцы/индексы в основные таблицы (без удаления данных)");
            if (Info.RequiredForProcessing) flags.Add("нужна для обработки");
            if (Info.RequiredForSearch) flags.Add("нужна для поиска");
            if (Info.RequiresNoTransaction) flags.Add("выполняется вне транзакции");
            return string.Join(" · ", flags);
        }
    }

    public bool Applied => Info.Applied;
}

/// <summary>
/// Вкладка «База данных»: подключение к существующей базе SQL Server, проверка соединения, прав, ожидаемых
/// столбцов и Full-Text Search, точные различия схемы, сопоставление имён и явное применение миграций.
/// Существующие таблицы не удаляются и не пересоздаются.
/// </summary>
public sealed class DatabaseSettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _server;
    private string _port;
    private string _instance;
    private string _database;
    private SqlAuthMode _auth;
    private string _userName;
    private string _password;
    private bool _hasSavedPassword;
    private SqlEncryptMode _encrypt;
    private bool _trustCertificate;
    private string _connectTimeout;
    private string _commandTimeout;
    private string _schema;
    private string _auxSchema;
    private string _sourceFilesTable;
    private string _personFactsTable;
    private SchemaReport _report;
    private string _message;
    private MessageKind _messageKind;
    private string _rebuildProgress;
    private bool _showAdvanced;
    private bool _savedBeforeTest;

    public DatabaseSettingsViewModel(AppServices services)
    {
        _services = services;
        Mappings = new ObservableCollection<ColumnMappingViewModel>();
        Issues = new ObservableCollection<SchemaIssue>();
        Migrations = new ObservableCollection<MigrationItemViewModel>();
        TestCommand = new AsyncCommand(TestAsync, () => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database), OnError);
        SaveCommand = new RelayCommand(Save, () => CanEdit);
        ApplyMigrationCommand = new AsyncCommand((p, ct) => ApplyMigrationAsync(p as MigrationItemViewModel, ct), p => CanManage && p is MigrationItemViewModel m && !m.Applied && Report?.Connected == true, OnError);
        ShowScriptCommand = new RelayCommand(p => ShowScript(p as MigrationItemViewModel));
        RebuildCommand = new AsyncCommand(RebuildAsync, () => CanManage && Report?.Connected == true, OnError);
        UseSuggestionCommand = new RelayCommand(p => UseSuggestion(p as SchemaIssue), p => CanEdit && p is SchemaIssue issue && issue.LogicalField != null);
        RevertCommand = new RelayCommand(Revert, () => CanEdit);
        ResetDefaultsCommand = new RelayCommand(ResetDefaults, () => CanEdit);
        ToggleAdvancedCommand = new RelayCommand(() => ShowAdvanced = !ShowAdvanced);
        PropertyChanged += (_, e) =>
        {
            if (ConnectionInputs.Contains(e.PropertyName))
            {
                OnPropertiesChanged(nameof(ConnectionPreview), nameof(PasswordRebindWarning));
            }
        };
        Load(services.Settings.Current.Database);
    }

    private static readonly HashSet<string> ConnectionInputs = new(StringComparer.Ordinal)
    {
        nameof(Server), nameof(Port), nameof(Instance), nameof(Database), nameof(Authentication), nameof(UserName),
        nameof(EncryptMandatory), nameof(TrustServerCertificate), nameof(ConnectTimeout), nameof(Password), nameof(HasSavedPassword),
    };

    public ObservableCollection<ColumnMappingViewModel> Mappings { get; }
    public ObservableCollection<SchemaIssue> Issues { get; }
    public ObservableCollection<MigrationItemViewModel> Migrations { get; }

    public AsyncCommand TestCommand { get; }
    public ICommand SaveCommand { get; }
    public AsyncCommand ApplyMigrationCommand { get; }
    public ICommand ShowScriptCommand { get; }
    public AsyncCommand RebuildCommand { get; }
    public ICommand UseSuggestionCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand ResetDefaultsCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }

    /// <summary>Показана ли тонкая настройка (шестерёнка): экземпляр, порт, шифрование, таблицы, миграции.</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (SetProperty(ref _showAdvanced, value))
            {
                OnPropertyChanged(nameof(AdvancedButtonText));
            }
        }
    }

    public string AdvancedButtonText => ShowAdvanced ? "Скрыть тонкую настройку" : "Тонкая настройка";

    public bool CanEdit => _services.Authorization.IsAllowed(Permission.ManageSettings);
    public bool CanManage => _services.Authorization.IsAllowed(Permission.ManageDatabase);
    public bool IsReadOnly => !CanEdit;

    public string Server
    {
        get => _server;
        set
        {
            var wasLocal = DatabaseSettings.IsLocalServer(_server);
            if (!SetProperty(ref _server, value))
            {
                return;
            }

            // Сетевой сервер: данные пойдут по сети — шифрование включается автоматически (его можно отключить явно).
            // Локальный сервер: соединение не выходит за пределы компьютера, а самоподписанный сертификат локального
            // SQL Server не проходит проверку — обязательное шифрование снимается (проверка сертификата не отключается).
            var isLocal = DatabaseSettings.IsLocalServer(value);
            if (wasLocal && !string.IsNullOrWhiteSpace(value) && !isLocal && !EncryptMandatory)
            {
                EncryptMandatory = true;
            }
            else if (!wasLocal && isLocal && EncryptMandatory && !TrustServerCertificate)
            {
                EncryptMandatory = false;
            }

            OnPropertiesChanged(nameof(SecurityWarning), nameof(LocalConnectionNote));
        }
    }
    public string Port { get => _port; set => SetProperty(ref _port, value); }
    public string Instance { get => _instance; set => SetProperty(ref _instance, value); }
    public string Database { get => _database; set => SetProperty(ref _database, value); }

    public SqlAuthMode Authentication
    {
        get => _auth;
        set
        {
            if (SetProperty(ref _auth, value))
            {
                OnPropertyChanged(nameof(IsSqlAuth));
            }
        }
    }

    public bool IsSqlAuth => Authentication == SqlAuthMode.Sql;
    public bool UseWindowsAuth { get => Authentication == SqlAuthMode.Windows; set { if (value) Authentication = SqlAuthMode.Windows; } }
    public bool UseSqlAuth { get => Authentication == SqlAuthMode.Sql; set { if (value) Authentication = SqlAuthMode.Sql; } }

    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    public bool HasSavedPassword
    {
        get => _hasSavedPassword;
        private set => SetProperty(ref _hasSavedPassword, value);
    }

    public bool EncryptMandatory
    {
        get => _encrypt == SqlEncryptMode.Mandatory;
        set
        {
            _encrypt = value ? SqlEncryptMode.Mandatory : SqlEncryptMode.Optional;
            OnPropertiesChanged(nameof(EncryptMandatory), nameof(SecurityWarning), nameof(LocalConnectionNote));
        }
    }

    public bool TrustServerCertificate
    {
        get => _trustCertificate;
        set
        {
            if (value && !_trustCertificate && !_services.Dialogs.Confirm("Отключить проверку сертификата",
                    "Без проверки сертификата соединение уязвимо для подмены сервера. Используйте это только для тестового сервера с самоподписанным сертификатом. Рекомендуется установить сертификат сервера в доверенные корневые сертификаты Windows.",
                    "Отключить проверку", "Оставить проверку", danger: true))
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _trustCertificate, value))
            {
                OnPropertyChanged(nameof(SecurityWarning));
            }
        }
    }

    /// <summary>Строка подключения, которую использует приложение (пароль скрыт).</summary>
    public string ConnectionPreview
    {
        get
        {
            try
            {
                var settings = PreviewSettings();
                var builder = new SqlConnectionStringBuilder(SqlConnectionFactory.Build(settings, IsSqlAuth ? "-" : null));
                if (IsSqlAuth)
                {
                    builder.Password = "********";
                }

                return builder.ConnectionString;
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>Сохранённый пароль привязан к серверу и имени входа и не будет отправлен по изменённому адресу.</summary>
    public string PasswordRebindWarning
    {
        get
        {
            if (!IsSqlAuth || !HasSavedPassword || !string.IsNullOrEmpty(Password))
            {
                return null;
            }

            var saved = _services.Settings.Current.Database;
            var current = PreviewSettings();
            current.PasswordBoundTo = saved.PasswordBoundTo;
            return current.PasswordMatchesTarget ? null
                : "Сервер или имя входа изменены: сохранённый пароль привязан к прежнему адресу и не будет отправлен на новый. Введите пароль заново — при сохранении старый будет удалён.";
        }
    }

    public string SecurityWarning =>
        !EncryptMandatory && !DatabaseSettings.IsLocalServer(Server) ? "Шифрование отключено: данные передаются по сети открытым текстом — только для изолированной сети." :
        TrustServerCertificate ? "Сертификат сервера не проверяется." : null;

    /// <summary>Пояснение для локального сервера без шифрования транспорта.</summary>
    public string LocalConnectionNote => !EncryptMandatory && DatabaseSettings.IsLocalServer(Server)
        ? "Локальный SQL Server: соединение не выходит за пределы компьютера (общая память), поэтому транспорт не шифруется, а проверка сертификата остаётся включённой. Для сетевого сервера шифрование включится автоматически."
        : null;

    public string ConnectTimeout { get => _connectTimeout; set => SetProperty(ref _connectTimeout, value); }
    public string CommandTimeout { get => _commandTimeout; set => SetProperty(ref _commandTimeout, value); }
    public string Schema { get => _schema; set => SetProperty(ref _schema, value); }
    public string AuxSchema { get => _auxSchema; set => SetProperty(ref _auxSchema, value); }
    public string SourceFilesTable { get => _sourceFilesTable; set => SetProperty(ref _sourceFilesTable, value); }
    public string PersonFactsTable { get => _personFactsTable; set => SetProperty(ref _personFactsTable, value); }

    public SchemaReport Report
    {
        get => _report;
        private set
        {
            SetProperty(ref _report, value);
            OnPropertiesChanged(nameof(ServerText), nameof(ReadinessText), nameof(FullTextText), nameof(PermissionsText), nameof(HasReport));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasReport => Report != null;

    public string ServerText => Report == null ? null :
        !Report.Connected ? "Нет соединения: " + Report.ConnectionError :
        $"{Report.ServerVersion} · {Report.Edition} · база {Report.DatabaseName} · вход {Report.LoginName}" +
        (Report.AuthScheme != null ? " · " + Report.AuthScheme : string.Empty) +
        (Report.EncryptedConnection == true ? " · соединение зашифровано" : Report.EncryptedConnection == false ? " · соединение НЕ зашифровано" : string.Empty);

    public string ReadinessText => Report == null || !Report.Connected ? null :
        (Report.CanProcess ? "Обработка: готово." : "Обработка недоступна: " + string.Join("; ", Report.ProcessingBlockers.Distinct().Take(3))) + " " +
        (Report.CanSearch ? "Поиск: готово." : "Поиск недоступен: " + string.Join("; ", Report.SearchBlockers.Distinct().Take(3)));

    public string FullTextText
    {
        get
        {
            var fts = Report?.FullText;
            if (fts == null || !Report.Connected)
            {
                return null;
            }

            if (!fts.Installed)
            {
                return "Full-Text Search не установлен на сервере: доступен только медленный режим «Подстрока».";
            }

            if (!fts.IndexExists)
            {
                return "Full-Text Search установлен, индекс не создан (миграция V007).";
            }

            return $"Полнотекстовый индекс активен (язык {fts.Language}, проиндексировано {fts.IndexedItems:N0}, состояние: {fts.PopulateStatusText})." +
                   (fts.PendingChanges > 0 ? $" Ожидают индексации: {fts.PendingChanges:N0}." : string.Empty);
        }
    }

    public string PermissionsText => Report?.Connected != true ? null :
        $"Права: чтение — {(Report.Permissions.CanSelect ? "да" : "нет")}, запись — {(Report.Permissions.CanInsert ? "да" : "нет")}, " +
        $"создание таблиц — {(Report.Permissions.CanCreateTable ? "да" : "нет")}, изменение схемы — {(Report.Permissions.CanAlter ? "да" : "нет")}, " +
        $"полнотекстовый каталог — {(Report.Permissions.CanCreateFullText ? "да" : "нет")}";

    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public MessageKind MessageKind { get => _messageKind; private set => SetProperty(ref _messageKind, value); }
    public string RebuildProgress { get => _rebuildProgress; private set => SetProperty(ref _rebuildProgress, value); }

    private void OnError(Exception ex)
    {
        Message = ex is AccessDeniedException ? "Недостаточно прав: " + ex.Message : Fakt.Infrastructure.Sql.SqlConnectionFactory.Describe(ex).Message;
        MessageKind = MessageKind.Error;
    }

    private void Load(DatabaseSettings settings)
    {
        Server = settings.Server;
        Port = settings.Port?.ToString(CultureInfo.InvariantCulture);
        Instance = settings.Instance;
        Database = settings.Database;
        Authentication = settings.Authentication;
        UserName = settings.UserName;
        Password = null;
        _encrypt = settings.Encrypt;
        _trustCertificate = settings.TrustServerCertificate;
        OnPropertiesChanged(nameof(EncryptMandatory), nameof(TrustServerCertificate), nameof(SecurityWarning), nameof(LocalConnectionNote));
        ConnectTimeout = settings.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        CommandTimeout = settings.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        Schema = settings.Schema;
        AuxSchema = settings.AuxiliarySchema;
        SourceFilesTable = settings.SourceFilesTable;
        PersonFactsTable = settings.PersonFactsTable;
        HasSavedPassword = settings.Authentication == SqlAuthMode.Sql && SafeExists();
        var c = settings.Columns ?? new ColumnMap();
        Mappings.Clear();
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.SourceFilesId), "ID источника", "SourceFiles", c.SourceFilesId, "BIGINT IDENTITY, PK"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.FileCode), "Код файла", "SourceFiles", c.FileCode, "VARCHAR(32) NOT NULL UNIQUE"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.FileName), "Имя файла", "SourceFiles", c.FileName, "NVARCHAR(1024) NOT NULL"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.PersonFactsId), "ID наблюдения", "PersonFacts", c.PersonFactsId, "BIGINT IDENTITY, PK"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.Surname), "Фамилия", "PersonFacts", c.Surname, "NVARCHAR(200) NULL"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.Name), "Имя", "PersonFacts", c.Name, "NVARCHAR(200) NULL"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.Patronymic), "Отчество", "PersonFacts", c.Patronymic, "NVARCHAR(200) NULL"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.BirthDate), "Дата рождения", "PersonFacts", c.BirthDate, "DATE NULL"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.BirthPlace), "Место рождения", "PersonFacts", c.BirthPlace, "NVARCHAR(1000) NULL"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.All), "ALL (JSON)", "PersonFacts", c.All, "NVARCHAR(MAX) NOT NULL, ISJSON"));
        Mappings.Add(new ColumnMappingViewModel(nameof(ColumnMap.FileId), "ID_FileName (FK)", "PersonFacts", c.FileId, "BIGINT NOT NULL → SourceFiles.ID"));
    }

    private bool SafeExists()
    {
        try
        {
            return _services.Settings.Secrets.Exists(Fakt.Core.Llm.SecretKeys.SqlPassword);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.IO.IOException)
        {
            return false;
        }
    }

    private DatabaseSettings Build()
    {
        int ParseInt(string text, string what, int min, int max)
        {
            if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                throw new ArgumentException($"{what}: целое число от {min} до {max}.");
            }

            return value;
        }

        var columns = new ColumnMap();
        foreach (var mapping in Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Physical))
            {
                throw new ArgumentException($"Не задано имя столбца для поля «{mapping.Title}».");
            }

            typeof(ColumnMap).GetProperty(mapping.Key).SetValue(columns, mapping.Physical.Trim());
        }

        return new DatabaseSettings
        {
            Server = Server?.Trim(),
            Port = string.IsNullOrWhiteSpace(Port) ? (int?)null : ParseInt(Port, "Порт", 1, 65535),
            Instance = string.IsNullOrWhiteSpace(Instance) ? null : Instance.Trim(),
            Database = Database?.Trim(),
            Authentication = Authentication,
            UserName = UserName?.Trim(),
            PasswordBoundTo = _services.Settings.Current.Database.PasswordBoundTo,
            Encrypt = _encrypt,
            TrustServerCertificate = _trustCertificate,
            ConnectTimeoutSeconds = ParseInt(ConnectTimeout, "Тайм-аут соединения", 1, 600),
            CommandTimeoutSeconds = ParseInt(CommandTimeout, "Тайм-аут команд", 5, 7200),
            Schema = string.IsNullOrWhiteSpace(Schema) ? "dbo" : Schema.Trim(),
            AuxiliarySchema = string.IsNullOrWhiteSpace(AuxSchema) ? null : AuxSchema.Trim(),
            SourceFilesTable = string.IsNullOrWhiteSpace(SourceFilesTable) ? "SourceFiles" : SourceFilesTable.Trim(),
            PersonFactsTable = string.IsNullOrWhiteSpace(PersonFactsTable) ? "PersonFacts" : PersonFactsTable.Trim(),
            Columns = columns,
        };
    }

    /// <summary>Параметры соединения без строгой проверки — для предварительного просмотра строки подключения.</summary>
    private DatabaseSettings PreviewSettings() => new()
    {
        Server = Server?.Trim(),
        Port = int.TryParse(Port?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 ? port : (int?)null,
        Instance = string.IsNullOrWhiteSpace(Instance) ? null : Instance.Trim(),
        Database = Database?.Trim(),
        Authentication = Authentication,
        UserName = UserName?.Trim(),
        Encrypt = _encrypt,
        TrustServerCertificate = _trustCertificate,
        ConnectTimeoutSeconds = int.TryParse(ConnectTimeout?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) && timeout > 0 ? timeout : 15,
    };

    /// <summary>
    /// Значения по умолчанию: локальный SQL Server, база FAKT, вход Windows, стандартные таблицы и столбцы.
    /// Ничего не сохраняется до нажатия «Сохранить»; существующие базы и таблицы не изменяются.
    /// </summary>
    private void ResetDefaults()
    {
        var defaults = DatabaseSettings.LocalDefaults();
        Load(defaults);
        Report = null;
        Issues.Clear();
        Migrations.Clear();
        Message = $"Подставлены значения по умолчанию: сервер {defaults.Server}, база {defaults.Database}, вход Windows. Нажмите «Проверить соединение» " +
                  "(если базы нет, она будет создана) и «Сохранить».";
        MessageKind = MessageKind.Info;
    }

    private void Revert()
    {
        Load(_services.Settings.Current.Database);
        if (Report != null)
        {
            ApplyReport(Report);
        }

        Message = "Изменения отменены: показаны сохранённые настройки подключения.";
        MessageKind = MessageKind.Info;
    }

    private async Task TestAsync(CancellationToken cancellationToken)
    {
        Message = "Проверка соединения и схемы…";
        MessageKind = MessageKind.Info;
        var settings = Build();
        var password = !IsSqlAuth ? null : string.IsNullOrEmpty(Password) ? _services.Settings.SqlPassword(settings) : Password;

        // Базы с указанным именем нет на сервере — создаётся автоматически вместе со схемой FAKT (нужно право CREATE DATABASE).
        DatabaseProvisionResult provision = null;
        if (CanManage)
        {
            Message = "Проверка наличия базы данных…";
            provision = await Task.Run(() => _services.Database.EnsureDatabaseAsync(settings, password ?? string.Empty, cancellationToken), cancellationToken);
        }

        Message = "Проверка соединения и схемы…";
        var report = await Task.Run(() => _services.Database.InspectAsync(settings, password ?? string.Empty, cancellationToken), cancellationToken);
        ApplyReport(report);
        if (provision != null && !provision.Success && (!report.Connected || provision.Created))
        {
            Message = WithCertificateHint(provision.Message);
            MessageKind = MessageKind.Error;
            return;
        }

        // Существующая (в том числе рабочая) база: создаём недостающие таблицы и объекты FAKT после подтверждения.
        string setupNote = null;
        if (CanManage && report.Connected && provision?.Created != true)
        {
            var plan = SchemaSetupPlan.AutoApply(report);
            if (plan.Count > 0)
            {
                // Записи, уже лежащие в PersonFacts, индексируются для поиска после подготовки (только для сохранённых настроек).
                var existingRows = report.PersonFacts?.Exists == true ? report.PersonFacts.ApproximateRows ?? 0 : 0;
                var indexExisting = existingRows > 0 && _savedBeforeTest;
                var text = SchemaSetupPlan.Describe(report, plan) +
                           (indexExisting ? Environment.NewLine + $"Существующие записи (около {existingRows:N0}) будут проиндексированы для поиска." : string.Empty);
                if (_services.Dialogs.Confirm($"Подготовить базу «{report.DatabaseName}»", text, "Создать таблицы", "Отмена"))
                {
                    Message = "Создание таблиц FAKT…";
                    var setup = await Task.Run(() => _services.Database.SetUpSchemaAsync(settings, password ?? string.Empty, plan, cancellationToken), cancellationToken);
                    report = await Task.Run(() => _services.Database.InspectAsync(settings, password ?? string.Empty, cancellationToken), cancellationToken);
                    ApplyReport(report);
                    if (!setup.Success)
                    {
                        _savedBeforeTest = false;
                        Message = setup.Message + " Изменения, выполненные до ошибки, сохранены; таблицы и данные не удалялись.";
                        MessageKind = MessageKind.Error;
                        return;
                    }

                    setupNote = $"В базе созданы таблицы и объекты FAKT ({setup.Applied.Count}).";
                    if (indexExisting)
                    {
                        Message = "Индексация существующих записей для поиска…";
                        var total = await Task.Run(() => _services.Database.RebuildSearchProjectionAsync(
                            new Progress<long>(n => Message = $"Индексация существующих записей для поиска: {n:N0}…"), cancellationToken), cancellationToken);
                        setupNote += $" Для поиска проиндексировано существующих записей: {total:N0}.";
                        report = await Task.Run(() => _services.Database.InspectAsync(settings, password ?? string.Empty, cancellationToken), cancellationToken);
                        ApplyReport(report);
                    }
                }
                else
                {
                    setupNote = "Таблицы FAKT не созданы — обработка и поиск будут недоступны, пока база не подготовлена.";
                }
            }

            var left = SchemaSetupPlan.LeftForAdministrator(report);
            if (left.Count > 0)
            {
                setupNote = (setupNote == null ? string.Empty : setupNote + " ") +
                            "Необязательные изменения существующих таблиц не выполнялись (" + string.Join("; ", left.Select(m => m.Title)) + ") — их можно применить в «Тонкой настройке».";
            }
        }

        var status = !report.Connected ? WithCertificateHint(report.ConnectionError)
            : report.CanProcess && report.CanSearch ? "Соединение установлено, база готова к обработке и поиску."
            : "Соединение установлено, но есть различия схемы — откройте «Тонкая настройка», чтобы увидеть их.";
        if (_savedBeforeTest && report.Connected)
        {
            status = "Настройки сохранены. " + status;
        }

        _savedBeforeTest = false;
        if (setupNote != null)
        {
            status = setupNote + " " + status;
        }

        Message = provision?.Created == true ? provision.Message + " " + status : status;
        MessageKind = !report.Connected ? MessageKind.Error : report.CanProcess && report.CanSearch ? MessageKind.Success : MessageKind.Warning;
    }

    /// <summary>Ошибка недоверенного сертификата при подключении к локальному серверу — подсказка, что сделать.</summary>
    private string WithCertificateHint(string error)
    {
        if (string.IsNullOrEmpty(error) || !EncryptMandatory || !DatabaseSettings.IsLocalServer(Server) ||
            (error.IndexOf("сертификат", StringComparison.OrdinalIgnoreCase) < 0 && error.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) < 0))
        {
            return error;
        }

        return "Локальный SQL Server использует самоподписанный сертификат, которому Windows не доверяет. Снимите отметку «Шифровать соединение» " +
               "(или нажмите «Значения по умолчанию»): соединение с сервером этого компьютера не выходит в сеть. " + error;
    }

    private void ApplyReport(SchemaReport report)
    {
        Report = report;
        Issues.Clear();
        foreach (var issue in report.Issues.OrderByDescending(i => i.Severity))
        {
            Issues.Add(issue);
        }

        Migrations.Clear();
        foreach (var migration in report.Migrations)
        {
            Migrations.Add(new MigrationItemViewModel(migration));
        }

        foreach (var mapping in Mappings)
        {
            mapping.Available.Clear();
            var table = mapping.Table == "SourceFiles" ? report.SourceFiles : report.PersonFacts;
            foreach (var column in table?.Columns ?? new List<ColumnInfo>())
            {
                mapping.Available.Add(column.Name);
            }
        }
    }

    private void UseSuggestion(SchemaIssue issue)
    {
        Message = $"Выберите существующий столбец для поля «{issue.LogicalField}» в таблице сопоставления ниже и сохраните настройки.";
        MessageKind = MessageKind.Info;
    }

    private void Save()
    {
        try
        {
            var settings = Build();
            if (settings.Authentication == SqlAuthMode.Sql && string.IsNullOrEmpty(Password) && !HasSavedPassword)
            {
                Message = "Введите пароль SQL Authentication.";
                MessageKind = MessageKind.Warning;
                return;
            }

            _services.Settings.SaveDatabase(settings, settings.Authentication == SqlAuthMode.Sql && !string.IsNullOrEmpty(Password) ? Password : null);
            Password = null;
            HasSavedPassword = settings.Authentication == SqlAuthMode.Sql && SafeExists();
            Message = "Настройки подключения сохранены. Пароль хранится в защищённом хранилище (DPAPI).";
            MessageKind = MessageKind.Success;

            // Сразу после сохранения — проверка соединения; отсутствующая база при этом создаётся автоматически.
            if (TestCommand.CanExecute(null))
            {
                _savedBeforeTest = true;
                TestCommand.Execute(null);
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ex is AccessDeniedException)
        {
            Message = ex.Message;
            MessageKind = MessageKind.Error;
        }
    }

    private void ShowScript(MigrationItemViewModel migration)
    {
        if (migration == null)
        {
            return;
        }

        _services.Dialogs.ShowDialog(new ScriptViewModel(_services, migration.Info));
    }

    private async Task ApplyMigrationAsync(MigrationItemViewModel migration, CancellationToken cancellationToken)
    {
        if (migration == null)
        {
            return;
        }

        var saved = _services.Settings.Current.Database;
        var current = Build();
        if (Newtonsoft.Json.JsonConvert.SerializeObject(saved) != Newtonsoft.Json.JsonConvert.SerializeObject(current))
        {
            Message = "Сначала сохраните настройки подключения: миграция применяется к сохранённой конфигурации.";
            MessageKind = MessageKind.Warning;
            return;
        }

        var preview = new ScriptViewModel(_services, migration.Info)
        {
            Title = $"Применить миграцию {migration.Id}?",
            PrimaryText = "Применить",
            SecondaryText = "Отмена",
            IsDanger = migration.Info.AltersUserTables,
        };
        if (!_services.Dialogs.ShowDialog(preview))
        {
            return;
        }

        var result = await Task.Run(() => _services.Database.ApplyMigrationAsync(migration.Id, cancellationToken), cancellationToken);
        Message = result.Message + $" ({result.Elapsed.TotalSeconds:0.0} с)";
        MessageKind = result.Success ? MessageKind.Success : MessageKind.Error;
        await TestAsync(cancellationToken);
    }

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        if (!_services.Dialogs.Confirm("Перестроить поисковую проекцию",
                "Проекция будет пересоздана из таблиц PersonFacts и SourceFiles пакетами. Основные таблицы не изменяются. На больших объёмах операция занимает время.", "Перестроить"))
        {
            return;
        }

        var total = await Task.Run(() => _services.Database.RebuildSearchProjectionAsync(new Progress<long>(n => RebuildProgress = $"Обработано наблюдений: {n:N0}"), cancellationToken), cancellationToken);
        RebuildProgress = $"Готово: перестроено {total:N0} наблюдений. Полнотекстовый индекс обновится автоматически.";
    }
}

/// <summary>Просмотр SQL миграции перед применением.</summary>
public sealed class ScriptViewModel : DialogViewModel
{
    public ScriptViewModel(AppServices services, MigrationInfo migration)
    {
        Title = $"{migration.Id}: {migration.Title}";
        PrimaryText = "Закрыть";
        Width = 820;
        Migration = migration;
        CopyCommand = new RelayCommand(() => services.Dialogs.CopyToClipboard(migration.Script));
    }

    public MigrationInfo Migration { get; }
    public string Description => Migration.Description;
    public string Script => Migration.Script;
    public ICommand CopyCommand { get; }
}
