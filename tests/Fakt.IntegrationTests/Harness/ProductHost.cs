using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Processing;
using Fakt.Application.Security;
using Fakt.Application.Settings;
using Fakt.Application.Structure;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Security;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Core.Structure;
using Fakt.Core.Worker;
using Fakt.Infrastructure.Scanning;
using Fakt.Infrastructure.Sql;
using Fakt.Infrastructure.Worker;
using Newtonsoft.Json;

namespace Fakt.Testing;

/// <summary>Хранилище настроек в памяти (вместо %ProgramData%\FAKT\settings.json). Переживает «перезапуск» хоста.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly object _gate = new();
    private string _json;

    public InMemorySettingsStore(AppSettings initial)
    {
        Save(initial);
    }

    public string Directory => Path.GetTempPath();

    public AppSettings Load()
    {
        lock (_gate)
        {
            return JsonConvert.DeserializeObject<AppSettings>(_json);
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            _json = JsonConvert.SerializeObject(settings);
        }
    }

    public void ApplyAccessControl(AccessSettings access)
    {
    }
}

public sealed class InMemorySecretStoreFactory : ISecretStoreFactory
{
    private readonly ConcurrentDictionary<SecretScope, InMemorySecretStore> _stores = new();

    public ISecretStore Create(SecretScope scope) => _stores.GetOrAdd(scope, s => new InMemorySecretStore(s));
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public InMemorySecretStore(SecretScope scope)
    {
        Scope = scope;
    }

    public SecretScope Scope { get; }

    public void Set(string key, string value) => _values[key] = value;

    public string Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public bool Exists(string key) => _values.ContainsKey(key);

    public void Delete(string key) => _values.TryRemove(key, out _);

    public IReadOnlyList<string> ListKeys(string prefix) => _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}

/// <summary>Фиксированная учётная запись теста: владелец настроек, роль «Администратор».</summary>
public sealed class TestIdentity : IIdentityProvider
{
    public static readonly TestIdentity Instance = new();

    public string UserName => @"FAKT-TEST\pipeline-test";

    public string UserSid => "S-1-5-21-1000000000-1000000000-1000000000-1001";

    public IReadOnlyCollection<string> GroupSids { get; } = Array.Empty<string>();

    public bool IsMember(string sid) => string.Equals(sid, UserSid, StringComparison.OrdinalIgnoreCase);

    public ResolvedPrincipal Resolve(string accountName) => null;

    public string NameOf(string sid) => sid;
}

/// <summary>Журнал в памяти: только счётчики событий и последние предупреждения (объём не растёт с числом записей).</summary>
public sealed class CountingLogger : IAppLogger
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LogEntry> _recent = new();

    public bool IsVerbose => false;

    public int KeepRecent { get; set; } = 200;

    public Action<LogEntry> OnEntry { get; set; }

    public void Write(LogEntry entry)
    {
        _counts.AddOrUpdate(entry.Level + ":" + entry.Event, 1, (_, n) => n + 1);
        if (entry.Level >= LogLevel.Warning)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > KeepRecent && _recent.TryDequeue(out _))
            {
            }
        }

        OnEntry?.Invoke(entry);
    }

    public long Count(LogLevel level, string eventName) => _counts.TryGetValue(level + ":" + eventName, out var n) ? n : 0;

    public IReadOnlyDictionary<string, long> Counts => _counts.ToDictionary(p => p.Key, p => p.Value);

    public IReadOnlyList<LogEntry> RecentWarnings => _recent.ToList();
}

/// <summary>Поиск Python worker: FAKT_PYTHON или поставляемый/собранный worker (как в приложении).</summary>
public static class WorkerEnvironment
{
    public static WorkerLaunchInfo Resolve(string explicitPython = null, string explicitWorkerDir = null)
    {
        var launch = WorkerClientFactory.Resolve(explicitPython, explicitWorkerDir, AppDomain.CurrentDomain.BaseDirectory);
        if (!File.Exists(launch.PythonPath))
        {
            throw new InvalidOperationException(
                $"Не найден python.exe для worker ({launch.PythonPath}). Соберите worker (worker\\build\\Build-Worker.ps1) или задайте переменную FAKT_PYTHON " +
                "(например, D:\\Projects_new\\Fakt\\worker\\build\\out\\python\\python.exe).");
        }

        if (!File.Exists(launch.ScriptPath))
        {
            throw new InvalidOperationException($"Не найден скрипт worker: {launch.ScriptPath}.");
        }

        return launch;
    }
}

/// <summary>
/// Композиция настоящих сервисов приложения (как в Fakt.Desktop AppServices) для тестов: ProcessingService, JobSession,
/// FilePipeline, BatchExtractor, ResilientLlmClient, SqlStorage/SqlFactWriter, DatabaseAdmin, PythonWorkerClient, FileHasher.
/// Заменены только настройки/секреты/учётная запись (в памяти) и провайдер LLM (локальный имитатор).
/// Декораторы хранилища и worker позволяют измерять стадии и внедрять отказы, делегируя настоящим реализациям.
/// </summary>
public sealed class ProductHost
{
    public ProductHost(InMemorySettingsStore store, LocalLlmSimulator simulator, WorkerLaunchInfo worker, IAppLogger logger = null,
        Func<IStorageFactory, IStorageFactory> decorateStorage = null, Func<IWorkerClientFactory, IWorkerClientFactory> decorateWorkers = null)
    {
        Logger = logger ?? NullLogger.Instance;
        Simulator = simulator;
        Settings = new SettingsService(store, new InMemorySecretStoreFactory(), TestIdentity.Instance, Logger);
        Authorization = new AuthorizationService(TestIdentity.Instance, () => Settings.Current.Access);
        Settings.Authorization = Authorization;
        Registry = new SimulatorAdapterRegistry(simulator);
        StorageFactory = decorateStorage != null ? decorateStorage(new SqlStorageFactory()) : new SqlStorageFactory();
        DatabaseAdmin = new DatabaseAdmin(Logger);
        IWorkerClientFactory workers = new WorkerClientFactory(() => worker, Logger);
        Workers = decorateWorkers != null ? decorateWorkers(workers) : workers;
        Processing = new ProcessingService(Settings, Authorization, Registry, StorageFactory, DatabaseAdmin, Workers, new FileHasher(), Logger)
        {
            AppVersion = "pipeline-test",
        };
        Structure = new StructureDetectionService(Logger);
    }

    public IAppLogger Logger { get; }

    public LocalLlmSimulator Simulator { get; }

    public SettingsService Settings { get; }

    public AuthorizationService Authorization { get; }

    public SimulatorAdapterRegistry Registry { get; }

    public IStorageFactory StorageFactory { get; }

    public DatabaseAdmin DatabaseAdmin { get; }

    public IWorkerClientFactory Workers { get; }

    public ProcessingService Processing { get; }

    public StructureDetectionService Structure { get; }

    public IStorage OpenStorage() => StorageFactory.Create(Settings.Current.Database, null);

    /// <summary>Настройки приложения для прогона: профиль имитатора, база, параметры обработки, владелец — тестовая учётная запись.</summary>
    public static InMemorySettingsStore CreateSettings(DatabaseSettings database, ProcessingSettings processing, LlmProfile profile)
    {
        var settings = new AppSettings
        {
            Database = database,
            Processing = processing,
            LlmProfiles = new List<LlmProfile> { profile },
            ActiveLlmProfileId = profile.Id,
            Access = new AccessSettings
            {
                OwnerSid = TestIdentity.Instance.UserSid,
                OwnerName = TestIdentity.Instance.UserName,
                OwnerAssignedAtUtc = DateTime.UtcNow,
            },
        };
        return new InMemorySettingsStore(settings);
    }

    /// <summary>Профиль LLM, указывающий на имитатор. Режим ответа задан явно (JSON Schema), поэтому тест извлечения не нужен.</summary>
    public static LlmProfile SimulatorProfile(int batchRows = 10, int maxConcurrentRequests = 2, int maxInputTokens = 6000) => new()
    {
        Name = "Локальный имитатор LLM (тест)",
        ProviderId = LocalLlmSimulator.ProviderId,
        BaseUrl = "http://127.0.0.1/fakt-llm-simulator",
        ModelId = LocalLlmSimulator.SimulatedModelId,
        ManualModelId = true,
        OutputMode = StructuredOutputMode.JsonSchema,
        BatchRows = batchRows,
        MaxConcurrentRequests = maxConcurrentRequests,
        MaxInputTokensPerRequest = maxInputTokens,
        MaxOutputTokens = 8192,
        TimeoutSeconds = 120,
    };

    /// <summary>Файл, как его видит сканер (метаданные без чтения содержимого).</summary>
    public static ScannedFile Scan(string path)
    {
        var info = new FileInfo(path);
        return new ScannedFile(info.FullName, info.Name, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc, FileAttributesInfo.None);
    }

    /// <summary>Проверка заданной вручную структуры настоящим парсером worker (как «Изменить структуру» в интерфейсе).</summary>
    public async Task<JobFileInput> PrepareInputAsync(string path, StructureDescriptor structure, CancellationToken cancellationToken)
    {
        var file = Scan(path);
        using var worker = Workers.Create();
        var result = await Structure.ValidateManualAsync(file, structure, worker, Settings.Current.Processing, cancellationToken).ConfigureAwait(false);
        if (result.Status != FileStatus.Tabular)
        {
            throw new InvalidOperationException($"Структура файла {file.Name} не подтверждена парсером: {result.Message} {string.Join("; ", result.Errors)}");
        }

        return new JobFileInput { File = file, Structure = result.Structure };
    }
}
