using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Llm;
using Fakt.Application.Processing;
using Fakt.Application.Security;
using Fakt.Application.Services;
using Fakt.Application.Settings;
using Fakt.Application.Structure;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Infrastructure.Llm;
using Fakt.Infrastructure.Logging;
using Fakt.Infrastructure.Platform;
using Fakt.Infrastructure.Scanning;
using Fakt.Infrastructure.Secrets;
using Fakt.Infrastructure.Security;
using Fakt.Infrastructure.Settings;
using Fakt.Infrastructure.Sql;
using Fakt.Infrastructure.Worker;
using Xunit;

namespace Fakt.IntegrationTests.RealLlm;

/// <summary>
/// Композиция сервисов как в приложении (AppServices), но с временным каталогом настроек: владелец — текущий
/// пользователь Windows, база SQL создаётся автоматически (EnsureDatabaseAsync), профиль LLM — из переменных окружения,
/// ключ сохраняется в DPAPI временного каталога. Возможности модели проверяются один раз «Тестом извлечения».
/// </summary>
public sealed class RealLlmEnvironment : IAsyncLifetime
{
    private LlmHttp _http;

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string ConfigDirectory { get; } = Path.Combine(Path.GetTempPath(), "fakt-realllm-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public string SqlServer { get; } = Environment.GetEnvironmentVariable("FAKT_TEST_SQL_SERVER") ?? "localhost";

    public string DatabaseName { get; } = RealLlmConfig.Database ?? "FaktRealLlm_" + Guid.NewGuid().ToString("N").Substring(0, 10);

    public SettingsService Settings { get; private set; }

    public AuthorizationService Authorization { get; private set; }

    public LlmAdapterRegistry Registry { get; private set; }

    public LlmProfileService Profiles { get; private set; }

    public WorkerClientFactory Workers { get; private set; }

    public StructureDetectionService Structure { get; private set; }

    public ProcessingService Processing { get; private set; }

    public SearchService Search { get; private set; }

    public DatabaseAdmin DatabaseAdmin { get; private set; }

    public JsonLineLogger Logger { get; private set; }

    public LlmProfile Profile { get; private set; }

    public DatabaseProvisionResult Provision { get; private set; }

    public ExtractionTestResult Probe { get; private set; }

    public string InitializationError { get; private set; }

    public async Task InitializeAsync()
    {
        if (!RealLlmConfig.IsConfigured)
        {
            return;
        }

        TlsConfigurator.Configure();
        var paths = new AppPaths(ConfigDirectory);
        var identity = new WindowsIdentityProvider();
        // Подробная диагностика (сырые ответы модели) — только когда база и журнал оставляются для разбора.
        Logger = new JsonLineLogger(paths.LogDirectory, () => RealLlmConfig.KeepDatabase);
        var store = new JsonSettingsStore(paths);
        var secrets = new DpapiSecretStoreFactory(paths.UserSecretsDirectory, paths.MachineSecretsDirectory);
        Settings = new SettingsService(store, secrets, identity, Logger);
        Authorization = new AuthorizationService(identity, () => Settings.Current.Access);
        Settings.Authorization = Authorization;
        Settings.InitializeOwner(Array.Empty<PrincipalEntry>(), Array.Empty<PrincipalEntry>(), SecretScope.CurrentUser);

        _http = new LlmHttp();
        Registry = new LlmAdapterRegistry(_http);
        Profiles = new LlmProfileService(Registry, Logger);
        Workers = new WorkerClientFactory(
            () => WorkerClientFactory.Resolve(Settings.Current.Processing.PythonPath, Path.Combine(RepositoryRoot, "worker"), AppPaths.ApplicationDirectory),
            Logger);
        DatabaseAdmin = new DatabaseAdmin(Logger);
        var storage = new SqlStorageFactory();
        Processing = new ProcessingService(Settings, Authorization, Registry, storage, DatabaseAdmin, Workers, new FileHasher(), Logger) { AppVersion = "realllm-test" };
        Structure = new StructureDetectionService(Logger);
        Search = new SearchService(Settings, Authorization, storage, Logger);

        // База данных: отсутствующая создаётся автоматически вместе со схемой (как при сохранении настроек в приложении).
        var database = new DatabaseSettings
        {
            Server = SqlServer,
            Database = DatabaseName,
            Authentication = SqlAuthMode.Windows,
            Encrypt = SqlEncryptMode.Mandatory,
            TrustServerCertificate = true, // только для локального тестового сервера с самоподписанным сертификатом
            CommandTimeoutSeconds = 300,
        };
        Settings.SaveDatabase(database, null);
        Provision = await new DatabaseService(Settings, Authorization, DatabaseAdmin, Logger).EnsureDatabaseAsync(Settings.Current.Database, null, CancellationToken.None).ConfigureAwait(false);
        if (!Provision.Success)
        {
            InitializationError = "База данных: " + Provision.Message;
            return;
        }

        // Профиль LLM.
        var descriptor = Registry.Providers.FirstOrDefault(p => p.Id == RealLlmConfig.Provider)
                         ?? throw new InvalidOperationException("Неизвестный провайдер: " + RealLlmConfig.Provider);
        Profile = new LlmProfile
        {
            Name = "Реальный интеграционный тест",
            ProviderId = descriptor.Id,
            BaseUrl = string.IsNullOrWhiteSpace(RealLlmConfig.BaseUrl) ? descriptor.DefaultBaseUrl : RealLlmConfig.BaseUrl,
            ModelId = RealLlmConfig.Model,
            ManualModelId = true,
            TimeoutSeconds = RealLlmConfig.TimeoutSeconds,
            BatchRows = RealLlmConfig.BatchRows,
            MaxConcurrentRequests = 2,
            MaxOutputTokens = 8192,
        };
        foreach (var field in descriptor.Fields.Where(f => f.DefaultValue != null))
        {
            Profile.Options[field.Key] = field.DefaultValue;
        }

        Settings.SaveProfile(Profile, makeActive: true);
        if (!string.IsNullOrEmpty(RealLlmConfig.ApiKey))
        {
            Settings.SetApiKey(Profile.Id, RealLlmConfig.ApiKey);
        }

        Profile = Settings.ActiveProfile;

        // Возможности модели: тот же «Тест извлечения», что и в разделе «Администрирование → LLM».
        Probe = await Profiles.TestExtractionAsync(Runtime(), CancellationToken.None).ConfigureAwait(false);
        if (Probe.ModeUsed.HasValue)
        {
            Settings.SaveCapabilities(Profile.Id, Probe.Capabilities);
            Profile = Settings.ActiveProfile;
        }
    }

    public LlmRuntimeConfig Runtime()
    {
        var profile = Settings.ActiveProfile;
        return Settings.BuildRuntimeConfig(profile, Registry.Providers.First(p => p.Id == profile.ProviderId));
    }

    public ResilientLlmClient Client() =>
        new(Registry.Get(Profile.ProviderId), Runtime(), new RetryPolicy { MaxAttempts = 4 }, new BudgetTracker(0, 0), Logger);

    /// <summary>Список файлов папки через тот же сканер, что и в приложении.</summary>
    public static async Task<List<ScannedFile>> ScanAsync(string folder)
    {
        var files = new List<ScannedFile>();
        await new FileSystemScanner().ScanAsync(new ScanOptions { RootPath = folder, Recursive = true }, batch => files.AddRange(batch), _ => { }, null, CancellationToken.None)
            .ConfigureAwait(false);
        return files;
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        Logger?.Dispose();
        if (RealLlmConfig.IsConfigured && !RealLlmConfig.KeepDatabase && Provision?.Created == true)
        {
            SqlConnection.ClearAllPools();
            var master = new DatabaseSettings { Server = SqlServer, Database = "master", Authentication = SqlAuthMode.Windows, Encrypt = SqlEncryptMode.Mandatory, TrustServerCertificate = true };
            using var connection = new SqlConnection(SqlConnectionFactory.Build(master, null));
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = new SqlCommand(
                $"IF DB_ID({SqlNames.Literal(DatabaseName)}) IS NOT NULL BEGIN ALTER DATABASE {SqlNames.Quote(DatabaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {SqlNames.Quote(DatabaseName)}; END",
                connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (RealLlmConfig.KeepDatabase)
        {
            return; // база и каталог настроек с журналом оставлены для разбора
        }

        try
        {
            Directory.Delete(ConfigDirectory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Fakt.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория (Fakt.sln).");
    }
}
