using System;
using System.Collections.Generic;
using System.Linq;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Security;
using Fakt.Core.Settings;

namespace Fakt.Application.Settings;

/// <summary>
/// Настройки приложения и секреты. Изменение требует роли «Администратор» (проверка в сервисе).
/// Ключ API хранится отдельно для каждого профиля и привязан к хосту Base URL: при смене адреса
/// сохранённый ключ не отправляется на новый хост, пока его не введут заново.
/// </summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly ISecretStoreFactory _secretFactory;
    private readonly IIdentityProvider _identity;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsService(ISettingsStore store, ISecretStoreFactory secretFactory, IIdentityProvider identity, IAppLogger logger)
    {
        _store = store;
        _secretFactory = secretFactory;
        _identity = identity;
        _logger = logger ?? NullLogger.Instance;
        _current = WithDefaults(store.Load());
    }

    /// <summary>
    /// Подключение к базе ещё не задано — подставляется локальный SQL Server и база FAKT (см.
    /// <see cref="DatabaseSettings.LocalDefaults"/>), чтобы не вводить значения вручную. Сопоставление столбцов,
    /// тайм-ауты и имена таблиц сохраняются; явно заданное подключение не меняется.
    /// </summary>
    public static AppSettings WithDefaults(AppSettings settings)
    {
        if (settings == null)
        {
            return null;
        }

        settings.Database ??= new DatabaseSettings();
        var db = settings.Database;
        if (string.IsNullOrWhiteSpace(db.Server) && string.IsNullOrWhiteSpace(db.Database) && db.Port == null && string.IsNullOrWhiteSpace(db.Instance))
        {
            var defaults = DatabaseSettings.LocalDefaults();
            db.Server = defaults.Server;
            db.Database = defaults.Database;
            db.Authentication = defaults.Authentication;
            db.UserName = null;
            db.Encrypt = defaults.Encrypt;
            db.TrustServerCertificate = defaults.TrustServerCertificate;
        }

        // Версия 1 по умолчанию требовала шифрование и для локального сервера: SQL Server с самоподписанным
        // сертификатом такое подключение отклоняет. Соединение с локальным сервером не выходит за пределы
        // компьютера, поэтому ранее сохранённое требование снимается один раз (проверка сертификата не отключается).
        if (settings.SchemaVersion < 2)
        {
            if (DatabaseSettings.IsLocalServer(db.Server) && db.Encrypt == SqlEncryptMode.Mandatory && !db.TrustServerCertificate)
            {
                db.Encrypt = SqlEncryptMode.Optional;
            }

            settings.SchemaVersion = 2;
        }

        settings.Processing ??= new ProcessingSettings();
        return settings;
    }

    public IAuthorizationService Authorization { get; set; }

    public event Action SettingsChanged;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public ISecretStore Secrets => _secretFactory.Create(Current.Access.SecretScope);

    public void Reload()
    {
        lock (_gate)
        {
            _current = WithDefaults(_store.Load());
        }

        SettingsChanged?.Invoke();
    }

    private void Demand(Permission permission)
    {
        if (Authorization == null)
        {
            throw new InvalidOperationException("Сервис авторизации не инициализирован.");
        }

        Authorization.Demand(permission);
    }

    private void Save(Action<AppSettings> change, string what)
    {
        lock (_gate)
        {
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(_current));
            change(copy);
            copy.UpdatedBy = _identity.UserName;
            _store.Save(copy);
            _current = copy;
        }

        _logger.Info("settings.saved", "Сохранены настройки: " + what, e => e.User = _identity.UserName);
        SettingsChanged?.Invoke();
    }

    // ---------- Доступ ----------

    /// <summary>Первоначальная настройка владельца. Разрешена только пока владелец не назначен.</summary>
    public void InitializeOwner(IEnumerable<PrincipalEntry> administrators, IEnumerable<PrincipalEntry> operators, SecretScope scope)
    {
        if (Current.Access.IsInitialized)
        {
            throw new InvalidOperationException("Владелец уже назначен. Изменение ролей выполняется администратором в разделе «Доступ».");
        }

        Save(settings =>
        {
            settings.Access = new AccessSettings
            {
                OwnerSid = _identity.UserSid,
                OwnerName = _identity.UserName,
                OwnerAssignedAtUtc = DateTime.UtcNow,
                Administrators = (administrators ?? Enumerable.Empty<PrincipalEntry>()).Where(p => !string.IsNullOrEmpty(p.Sid)).ToList(),
                Operators = (operators ?? Enumerable.Empty<PrincipalEntry>()).Where(p => !string.IsNullOrEmpty(p.Sid)).ToList(),
                SecretScope = scope,
            };
        }, "назначение владельца");
        TryApplyAcl();
    }

    public void SaveAccess(IEnumerable<PrincipalEntry> administrators, IEnumerable<PrincipalEntry> operators, SecretScope scope)
    {
        Demand(Permission.ManageAccess);
        var scopeChanged = Current.Access.SecretScope != scope;
        Save(settings =>
        {
            settings.Access.Administrators = administrators.Where(p => !string.IsNullOrEmpty(p.Sid)).ToList();
            settings.Access.Operators = operators.Where(p => !string.IsNullOrEmpty(p.Sid)).ToList();
            settings.Access.SecretScope = scope;
        }, "роли доступа");
        if (scopeChanged)
        {
            _logger.Warn("settings.secret_scope", "Изменена область хранения секретов: ключи и пароли нужно ввести заново.", e => e.User = _identity.UserName);
        }

        TryApplyAcl();
    }

    private void TryApplyAcl()
    {
        try
        {
            _store.ApplyAccessControl(Current.Access);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.IO.IOException || ex is InvalidOperationException || ex is System.Security.Principal.IdentityNotMappedException)
        {
            _logger.Warn("settings.acl", "Не удалось ограничить доступ к каталогу настроек ACL: " + ex.Message);
        }
    }

    // ---------- LLM ----------

    public LlmProfile ActiveProfile
    {
        get
        {
            var settings = Current;
            return settings.LlmProfiles.FirstOrDefault(p => p.Id == settings.ActiveLlmProfileId) ?? settings.LlmProfiles.FirstOrDefault();
        }
    }

    public void SaveProfile(LlmProfile profile, bool makeActive)
    {
        Demand(Permission.ManageSettings);
        ValidateProfile(profile);
        var existing = Current.LlmProfiles.FirstOrDefault(p => p.Id == profile.Id);
        var newHost = UrlBuilder.KeyBindingOf(profile.BaseUrl);
        var copy = profile.Clone();
        if (existing != null && Secrets.Exists(SecretKeys.ApiKey(profile.Id)) && !string.Equals(copy.KeyBoundHost, newHost, StringComparison.OrdinalIgnoreCase))
        {
            // Адрес изменён: сохранённый ключ не будет отправлен на новый хост.
            Secrets.Delete(SecretKeys.ApiKey(profile.Id));
            copy.KeyBoundHost = null;
            _logger.Warn("settings.key_unbound", $"Base URL профиля «{copy.Name}» изменён: сохранённый ключ удалён, введите ключ для нового адреса.", e => e.User = _identity.UserName);
        }

        if (existing != null && !string.Equals(existing.ModelId, copy.ModelId, StringComparison.Ordinal))
        {
            copy.Capabilities = new CapabilityState();
        }

        copy.UpdatedAtUtc = DateTime.UtcNow;
        Save(settings =>
        {
            settings.LlmProfiles.RemoveAll(p => p.Id == copy.Id);
            settings.LlmProfiles.Add(copy);
            if (makeActive || settings.ActiveLlmProfileId == null)
            {
                settings.ActiveLlmProfileId = copy.Id;
            }
        }, $"профиль LLM «{copy.Name}»");
    }

    public void SetActiveProfile(Guid profileId)
    {
        Demand(Permission.ManageSettings);
        Save(settings => settings.ActiveLlmProfileId = profileId, "активный профиль LLM");
    }

    public void DeleteProfile(Guid profileId)
    {
        Demand(Permission.ManageSettings);
        var secrets = Secrets;
        foreach (var key in secrets.ListKeys($"llm/{profileId:N}/"))
        {
            secrets.Delete(key);
        }

        Save(settings =>
        {
            settings.LlmProfiles.RemoveAll(p => p.Id == profileId);
            if (settings.ActiveLlmProfileId == profileId)
            {
                settings.ActiveLlmProfileId = settings.LlmProfiles.FirstOrDefault()?.Id;
            }
        }, "удаление профиля LLM");
    }

    /// <summary>Возможности модели, подтверждённые тестом, сохраняются без изменения остальных полей профиля.</summary>
    public void SaveCapabilities(Guid profileId, CapabilityState capabilities)
    {
        Demand(Permission.ManageSettings);
        Save(settings =>
        {
            var profile = settings.LlmProfiles.FirstOrDefault(p => p.Id == profileId);
            if (profile != null)
            {
                profile.Capabilities = capabilities;
            }
        }, "возможности модели");
    }

    public void SetApiKey(Guid profileId, string apiKey)
    {
        Demand(Permission.ManageSettings);
        var profile = Current.LlmProfiles.FirstOrDefault(p => p.Id == profileId) ?? throw new InvalidOperationException("Сначала сохраните профиль.");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Secrets.Delete(SecretKeys.ApiKey(profileId));
            Save(settings => settings.LlmProfiles.First(p => p.Id == profileId).KeyBoundHost = null, "удаление ключа API");
            return;
        }

        Secrets.Set(SecretKeys.ApiKey(profileId), apiKey.Trim());
        var host = UrlBuilder.KeyBindingOf(profile.BaseUrl);
        Save(settings => settings.LlmProfiles.First(p => p.Id == profileId).KeyBoundHost = host, "ключ API (значение не записывается в настройки)");
    }

    public bool HasApiKey(Guid profileId) => Secrets.Exists(SecretKeys.ApiKey(profileId));

    public void SetSecretHeader(Guid profileId, string headerName, string value)
    {
        Demand(Permission.ManageSettings);
        if (string.IsNullOrWhiteSpace(value))
        {
            Secrets.Delete(SecretKeys.Header(profileId, headerName));
        }
        else
        {
            Secrets.Set(SecretKeys.Header(profileId, headerName), value);
        }
    }

    /// <summary>
    /// Параметры вызова с раскрытыми секретами. Ключ передаётся только на хост, для которого он был сохранён;
    /// ключ другого профиля или провайдера сюда не попадает (секреты хранятся по идентификатору профиля).
    /// </summary>
    public LlmRuntimeConfig BuildRuntimeConfig(LlmProfile profile, LlmProviderDescriptor descriptor)
    {
        if (profile == null)
        {
            throw new LlmException(LlmErrorKind.Configuration, "Профиль LLM не выбран. Настройте его в «Администрирование → LLM».");
        }

        var secrets = Secrets;
        string key = null;
        if (secrets.Exists(SecretKeys.ApiKey(profile.Id)))
        {
            var host = UrlBuilder.KeyBindingOf(profile.BaseUrl);
            if (!string.Equals(profile.KeyBoundHost, host, StringComparison.OrdinalIgnoreCase))
            {
                throw new LlmException(LlmErrorKind.Configuration,
                    $"Ключ API профиля «{profile.Name}» сохранён для адреса {profile.KeyBoundHost ?? "(неизвестно)"}, а профиль указывает на {host}. Ключ не отправляется на другой адрес: введите его заново.");
            }

            key = secrets.Get(SecretKeys.ApiKey(profile.Id));
            if (key == null)
            {
                throw new LlmException(LlmErrorKind.Configuration,
                    "Ключ API не удалось расшифровать: он сохранён другой учётной записью Windows или на другом компьютере (область DPAPI). Введите ключ заново или измените область хранения секретов.");
            }
        }

        if (descriptor?.ApiKey == ApiKeyRequirement.Required && string.IsNullOrEmpty(key))
        {
            throw new LlmException(LlmErrorKind.Configuration, $"Для провайдера {descriptor.DisplayName} требуется ключ API.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in profile.ExtraHeaders ?? new List<HeaderSetting>())
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            headers[header.Name.Trim()] = header.IsSecret ? secrets.Get(SecretKeys.Header(profile.Id, header.Name.Trim())) : header.Value;
        }

        return new LlmRuntimeConfig(profile, key, headers);
    }

    public static void ValidateProfile(LlmProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Укажите название профиля.");
        }

        if (string.IsNullOrWhiteSpace(profile.ProviderId))
        {
            throw new ArgumentException("Выберите провайдера.");
        }

        if (!UrlBuilder.IsValidHttpUrl(profile.BaseUrl, out var error))
        {
            throw new ArgumentException("Base URL: " + error);
        }

        if (profile.TimeoutSeconds < 5 || profile.TimeoutSeconds > 3600)
        {
            throw new ArgumentException("Тайм-аут должен быть от 5 до 3600 секунд.");
        }

        if (profile.MaxOutputTokens < 256 || profile.MaxOutputTokens > 1_000_000)
        {
            throw new ArgumentException("Максимальный объём ответа — от 256 до 1 000 000 токенов.");
        }

        if (profile.MaxConcurrentRequests < 1 || profile.MaxConcurrentRequests > 64)
        {
            throw new ArgumentException("Одновременных запросов — от 1 до 64.");
        }

        if (profile.BatchRows < 1 || profile.BatchRows > 200)
        {
            throw new ArgumentException("Записей в пакете — от 1 до 200.");
        }

        if (profile.MaxInputTokensPerRequest < 1000)
        {
            throw new ArgumentException("Предел входных токенов на запрос — не менее 1000.");
        }

        if (profile.Temperature.HasValue && (profile.Temperature < 0 || profile.Temperature > 2))
        {
            throw new ArgumentException("Температура — от 0 до 2.");
        }

        foreach (var header in profile.ExtraHeaders ?? new List<HeaderSetting>())
        {
            var name = header.Name?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Any(ch => ch <= ' ' || ch == ':' || ch > '~'))
            {
                throw new ArgumentException($"Недопустимое имя заголовка «{header.Name}».");
            }

            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) && !header.IsSecret)
            {
                throw new ArgumentException("Заголовок Authorization должен быть отмечен как секретный.");
            }
        }
    }

    // ---------- База данных ----------

    public void SaveDatabase(DatabaseSettings database, string sqlPasswordOrNull)
    {
        Demand(Permission.ManageSettings);
        if (database.Authentication == SqlAuthMode.Sql && sqlPasswordOrNull != null)
        {
            Secrets.Set(SecretKeys.SqlPassword, sqlPasswordOrNull);
            database.PasswordBoundTo = database.CredentialTarget();
        }
        else if (database.Authentication == SqlAuthMode.Windows || !database.PasswordMatchesTarget)
        {
            // Windows Authentication или сменился сервер / имя входа: сохранённый пароль не переносится на новый адрес.
            Secrets.Delete(SecretKeys.SqlPassword);
            database.PasswordBoundTo = null;
        }

        Save(settings => settings.Database = database, "подключение к базе данных");
    }

    /// <summary>
    /// Пароль SQL из хранилища секретов: null для Windows Authentication и для сервера или имени входа,
    /// отличных от тех, для которых пароль сохранён.
    /// </summary>
    public string SqlPassword(DatabaseSettings database) =>
        database.Authentication == SqlAuthMode.Sql && database.PasswordMatchesTarget ? Secrets.Get(SecretKeys.SqlPassword) : null;

    // ---------- Обработка и диагностика ----------

    public void SaveProcessing(ProcessingSettings processing)
    {
        Demand(Permission.ManageSettings);
        if (processing.ChunkSize < 100 || processing.ChunkSize > 100000)
        {
            throw new ArgumentException("Размер chunk — от 100 до 100 000 записей.");
        }

        if (processing.SqlBatchSize < 1 || processing.SqlBatchSize > 10000)
        {
            throw new ArgumentException("Размер SQL-пакета — от 1 до 10 000.");
        }

        if (processing.QueueCapacity < 1 || processing.QueueCapacity > 256)
        {
            throw new ArgumentException("Предел очереди — от 1 до 256 пакетов.");
        }

        if (processing.SampleMaxBytes < 1024 || processing.SampleMaxBytes > 16 * 1024 * 1024)
        {
            throw new ArgumentException("Предел образца — от 1 КиБ до 16 МиБ.");
        }

        Save(settings => settings.Processing = processing, "параметры обработки");
    }

    public void SetVerboseDiagnostics(TimeSpan? duration)
    {
        Demand(Permission.ManageDiagnostics);
        Save(settings =>
        {
            settings.Diagnostics.VerboseUntilUtc = duration.HasValue ? DateTime.UtcNow + duration.Value : (DateTime?)null;
            settings.Diagnostics.VerboseEnabledBy = duration.HasValue ? _identity.UserName : null;
        }, duration.HasValue ? "подробный журнал включён" : "подробный журнал выключен");
    }

    public void SaveDiagnostics(int retentionDays, int verboseRetentionDays)
    {
        Demand(Permission.ManageDiagnostics);
        Save(settings =>
        {
            settings.Diagnostics.RetentionDays = Math.Max(1, retentionDays);
            settings.Diagnostics.VerboseRetentionDays = Math.Max(1, verboseRetentionDays);
        }, "срок хранения журнала");
    }
}
