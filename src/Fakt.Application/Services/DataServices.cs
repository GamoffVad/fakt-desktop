using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Settings;
using Fakt.Core.Logging;
using Fakt.Core.Processing;
using Fakt.Core.Search;
using Fakt.Core.Security;
using Fakt.Core.Settings;
using Fakt.Core.Storage;

namespace Fakt.Application.Services;

/// <summary>Проверка базы и явное применение миграций (только роль «Администратор»).</summary>
public sealed class DatabaseService
{
    private readonly SettingsService _settings;
    private readonly IAuthorizationService _authorization;
    private readonly IDatabaseAdmin _admin;
    private readonly IAppLogger _logger;

    public DatabaseService(SettingsService settings, IAuthorizationService authorization, IDatabaseAdmin admin, IAppLogger logger)
    {
        _settings = settings;
        _authorization = authorization;
        _admin = admin;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Автоматическое создание базы, указанной в настройках, если её нет на сервере (со схемой FAKT).
    /// Существующая база не изменяется.
    /// </summary>
    public async Task<DatabaseProvisionResult> EnsureDatabaseAsync(DatabaseSettings settings, string passwordOrNull, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ManageDatabase);
        var password = passwordOrNull ?? _settings.SqlPassword(settings);
        var result = await _admin.EnsureDatabaseAsync(settings, password, _authorization.CurrentUserName, cancellationToken).ConfigureAwait(false);
        if (result.Created)
        {
            _logger.Info("db.provisioned", result.Message, e => e.User = _authorization.CurrentUserName);
        }

        return result;
    }

    /// <summary>Проверка произвольных (ещё не сохранённых) настроек — для кнопки «Проверить соединение».</summary>
    public Task<SchemaReport> InspectAsync(DatabaseSettings settings, string passwordOrNull, CancellationToken cancellationToken)
    {
        var password = passwordOrNull ?? _settings.SqlPassword(settings);
        return _admin.InspectAsync(settings, password, cancellationToken);
    }

    public Task<SchemaReport> InspectCurrentAsync(CancellationToken cancellationToken) =>
        InspectAsync(_settings.Current.Database, null, cancellationToken);

    public async Task<MigrationResult> ApplyMigrationAsync(string migrationId, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ManageDatabase);
        var database = _settings.Current.Database;
        var result = await _admin.ApplyMigrationAsync(database, _settings.SqlPassword(database), migrationId, _authorization.CurrentUserName, cancellationToken).ConfigureAwait(false);
        _logger.Info("db.migration_requested", result.Message, e => e.User = _authorization.CurrentUserName);
        return result;
    }

    /// <summary>
    /// Подготовка существующей базы: применяются миграции из <see cref="SchemaSetupPlan.AutoApply"/> по порядку;
    /// при первой ошибке остальные не выполняются. Все миграции только добавляют объекты.
    /// </summary>
    public async Task<SchemaSetupResult> SetUpSchemaAsync(DatabaseSettings settings, string passwordOrNull, IReadOnlyList<MigrationInfo> plan, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ManageDatabase);
        var password = passwordOrNull ?? _settings.SqlPassword(settings);
        var result = new SchemaSetupResult();
        foreach (var migration in plan)
        {
            var applied = await _admin.ApplyMigrationAsync(settings, password, migration.Id, _authorization.CurrentUserName, cancellationToken).ConfigureAwait(false);
            if (!applied.Success)
            {
                result.Success = false;
                result.Message = $"Не удалось выполнить «{migration.Title}»: {applied.Message}";
                _logger.Warn("db.setup_failed", result.Message, e => e.User = _authorization.CurrentUserName);
                return result;
            }

            result.Applied.Add(migration.Id);
        }

        result.Message = result.Applied.Count == 0 ? "Изменений не потребовалось." : $"Созданы таблицы и объекты FAKT ({string.Join(", ", result.Applied)}).";
        _logger.Info("db.setup", result.Message, e => e.User = _authorization.CurrentUserName);
        return result;
    }

    public Task<long> RebuildSearchProjectionAsync(IProgress<long> progress, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ManageDatabase);
        var database = _settings.Current.Database;
        return _admin.RebuildSearchProjectionAsync(database, _settings.SqlPassword(database), progress, cancellationToken);
    }
}

/// <summary>
/// Поиск для страницы «Поиск»: проверка прав, отдельный подсчёт общего числа, карточка наблюдения.
/// Защита от устаревших результатов — на стороне модели представления (номер запроса).
/// </summary>
public sealed class SearchService
{
    private readonly SettingsService _settings;
    private readonly IAuthorizationService _authorization;
    private readonly IStorageFactory _storageFactory;
    private readonly IAppLogger _logger;

    public SearchService(SettingsService settings, IAuthorizationService authorization, IStorageFactory storageFactory, IAppLogger logger)
    {
        _settings = settings;
        _authorization = authorization;
        _storageFactory = storageFactory;
        _logger = logger ?? NullLogger.Instance;
    }

    private ISearchRepository Repository()
    {
        var database = _settings.Current.Database;
        if (!database.IsConfigured)
        {
            throw new InvalidOperationException("Подключение к базе данных не настроено («Администрирование → База данных»).");
        }

        return _storageFactory.Create(database, _settings.SqlPassword(database)).Search;
    }

    public async Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.SearchData);
        var page = await Repository().SearchAsync(query, cancellationToken).ConfigureAwait(false);
        // Журнал не содержит текста запроса (может включать персональные данные): только режим, число строк и время.
        _logger.Info("search.executed", $"Поиск: режим {query.Mode}, строк на странице {page.Rows.Count}", e =>
        {
            e.Stage = "search";
            e.DurationMs = (long)page.Elapsed.TotalMilliseconds;
            e.Count = page.Rows.Count;
        });
        return page;
    }

    public Task<long> CountAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.SearchData);
        return Repository().CountAsync(query, cancellationToken);
    }

    public Task<ObservationDetails> GetObservationAsync(long personFactId, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.SearchData);
        return Repository().GetObservationAsync(personFactId, cancellationToken);
    }

    public Task<FullTextInfo> GetFullTextInfoAsync(CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.SearchData);
        return Repository().GetFullTextInfoAsync(cancellationToken);
    }
}

public sealed class HistoryService
{
    private readonly SettingsService _settings;
    private readonly IAuthorizationService _authorization;
    private readonly IStorageFactory _storageFactory;

    public HistoryService(SettingsService settings, IAuthorizationService authorization, IStorageFactory storageFactory)
    {
        _settings = settings;
        _authorization = authorization;
        _storageFactory = storageFactory;
    }

    private IJobRepository Jobs()
    {
        var database = _settings.Current.Database;
        if (!database.IsConfigured)
        {
            throw new InvalidOperationException("Подключение к базе данных не настроено («Администрирование → База данных»).");
        }

        return _storageFactory.Create(database, _settings.SqlPassword(database)).Jobs;
    }

    public Task<IReadOnlyList<JobRecord>> ListJobsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ViewHistory);
        return Jobs().ListJobsAsync(skip, take, cancellationToken);
    }

    public Task<IReadOnlyList<JobFileRecord>> ListFilesAsync(long jobId, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ViewHistory);
        return Jobs().ListJobFilesAsync(jobId, cancellationToken);
    }

    public Task<IReadOnlyList<RowErrorRecord>> ListErrorsAsync(long jobFileId, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ViewHistory);
        return Jobs().ListErrorsAsync(jobFileId, 500, cancellationToken);
    }

    public Task<int> MarkInterruptedAsync(CancellationToken cancellationToken) => Jobs().MarkInterruptedAsync(cancellationToken);
}
