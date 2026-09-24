using System;
using System.Linq;
using Fakt.Application.Llm;
using Fakt.Application.Processing;
using Fakt.Application.Security;
using Fakt.Application.Services;
using Fakt.Application.Settings;
using Fakt.Application.Structure;
using Fakt.Desktop.Services;
using Fakt.Infrastructure.Llm;
using Fakt.Infrastructure.Logging;
using Fakt.Infrastructure.Platform;
using Fakt.Infrastructure.Scanning;
using Fakt.Infrastructure.Secrets;
using Fakt.Infrastructure.Security;
using Fakt.Infrastructure.Settings;
using Fakt.Infrastructure.Sql;
using Fakt.Infrastructure.Worker;

namespace Fakt.Desktop.Composition;

/// <summary>Корень композиции: создание сервисов без DI-контейнера, зависимости передаются явно.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices()
    {
    }

    public static string Version => typeof(AppServices).Assembly.GetName().Version.ToString(3);

    public AppPaths Paths { get; private set; }
    public JsonLineLogger Logger { get; private set; }
    public JsonSettingsStore SettingsStore { get; private set; }
    public WindowsIdentityProvider Identity { get; private set; }
    public SettingsService Settings { get; private set; }
    public AuthorizationService Authorization { get; private set; }
    public LlmHttp Http { get; private set; }
    public LlmAdapterRegistry Registry { get; private set; }
    public LlmProfileService Profiles { get; private set; }
    public WorkerClientFactory Workers { get; private set; }
    public FileSystemScanner Scanner { get; private set; }
    public FileHasher Hasher { get; private set; }
    public SqlStorageFactory StorageFactory { get; private set; }
    public DatabaseAdmin DatabaseAdmin { get; private set; }
    public ProcessingService Processing { get; private set; }
    public StructureDetectionService Structure { get; private set; }
    public EstimationService Estimation { get; private set; }
    public SearchService Search { get; private set; }
    public HistoryService History { get; private set; }
    public DatabaseService Database { get; private set; }
    public IDialogService Dialogs { get; private set; }
    public string TlsMode { get; private set; }

    public static AppServices Create(string configDirectory)
    {
        var services = new AppServices();
        services.TlsMode = TlsConfigurator.Configure();
        services.Paths = new AppPaths(configDirectory);
        services.SettingsStore = new JsonSettingsStore(services.Paths);
        services.Identity = new WindowsIdentityProvider();
        SettingsService settings = null;
        services.Logger = new JsonLineLogger(services.Paths.LogDirectory, () => settings?.Current.Diagnostics.IsVerboseActive(DateTime.UtcNow) == true);
        var secretFactory = new DpapiSecretStoreFactory(services.Paths.UserSecretsDirectory, services.Paths.MachineSecretsDirectory);
        settings = new SettingsService(services.SettingsStore, secretFactory, services.Identity, services.Logger);
        services.Settings = settings;
        services.Authorization = new AuthorizationService(services.Identity, () => settings.Current.Access);
        settings.Authorization = services.Authorization;
        services.Logger.ApplyRetention(settings.Current.Diagnostics.RetentionDays, settings.Current.Diagnostics.VerboseRetentionDays);

        services.Http = new LlmHttp();
        services.Registry = new LlmAdapterRegistry(services.Http);
        services.Profiles = new LlmProfileService(services.Registry, services.Logger);
        services.Workers = new WorkerClientFactory(
            () => WorkerClientFactory.Resolve(settings.Current.Processing.PythonPath, settings.Current.Processing.WorkerDirectory, AppPaths.ApplicationDirectory),
            services.Logger);
        services.Scanner = new FileSystemScanner();
        services.Hasher = new FileHasher();
        services.StorageFactory = new SqlStorageFactory();
        services.DatabaseAdmin = new DatabaseAdmin(services.Logger);
        services.Processing = new ProcessingService(settings, services.Authorization, services.Registry, services.StorageFactory, services.DatabaseAdmin,
            services.Workers, services.Hasher, services.Logger) { AppVersion = Version };
        services.Structure = new StructureDetectionService(services.Logger);
        services.Estimation = new EstimationService();
        services.Search = new SearchService(settings, services.Authorization, services.StorageFactory, services.Logger);
        services.History = new HistoryService(settings, services.Authorization, services.StorageFactory);
        services.Database = new DatabaseService(settings, services.Authorization, services.DatabaseAdmin, services.Logger);
        services.Dialogs = new DialogService();
        return services;
    }

    public Fakt.Core.Llm.LlmProviderDescriptor Provider(string id) => Registry.Providers.FirstOrDefault(p => p.Id == id);

    public void Dispose()
    {
        Http?.Dispose();
        Logger?.Dispose();
    }
}
