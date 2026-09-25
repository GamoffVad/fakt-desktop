using System;
using System.Collections.Generic;
using Fakt.Core.Llm;

namespace Fakt.Core.Settings;

/// <summary>Общие настройки компьютера (без секретов): %ProgramData%\FAKT\settings.json.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public List<LlmProfile> LlmProfiles { get; set; } = new();

    public Guid? ActiveLlmProfileId { get; set; }

    public DatabaseSettings Database { get; set; } = new();

    public ProcessingSettings Processing { get; set; } = new();

    public AccessSettings Access { get; set; } = new();

    public DiagnosticsSettings Diagnostics { get; set; } = new();

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string UpdatedBy { get; set; }
}

public enum SqlAuthMode
{
    Windows,
    Sql,
}

public enum SqlEncryptMode
{
    /// <summary>Шифрование обязательно, сертификат сервера проверяется (рекомендуется).</summary>
    Mandatory,

    /// <summary>Без шифрования транспорта (только для изолированной сети; показывается предупреждение).</summary>
    Optional,
}

public sealed class DatabaseSettings
{
    public string Server { get; set; }

    /// <summary>Порт TCP; пусто — порт по умолчанию или экземпляр через SQL Browser.</summary>
    public int? Port { get; set; }

    /// <summary>Именованный экземпляр (SERVER\INSTANCE).</summary>
    public string Instance { get; set; }

    public string Database { get; set; }

    public SqlAuthMode Authentication { get; set; } = SqlAuthMode.Windows;

    public string UserName { get; set; }

    /// <summary>
    /// Адрес сервера и имя входа, для которых сохранён пароль SQL (см. <see cref="CredentialTarget"/>). Пароль не
    /// отправляется другому серверу или от имени другого входа: после их изменения его нужно ввести заново.
    /// </summary>
    public string PasswordBoundTo { get; set; }

    public SqlEncryptMode Encrypt { get; set; } = SqlEncryptMode.Mandatory;

    /// <summary>
    /// Доверять сертификату сервера без проверки цепочки. По умолчанию выключено; включение требует
    /// явного подтверждения и показывается как предупреждение безопасности.
    /// </summary>
    public bool TrustServerCertificate { get; set; }

    /// <summary>Имя хоста в сертификате, если отличается от адреса подключения (HostNameInCertificate не поддерживается System.Data.SqlClient — справочно).</summary>
    public string CertificateHostName { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int CommandTimeoutSeconds { get; set; } = 120;

    public string Schema { get; set; } = "dbo";

    public string SourceFilesTable { get; set; } = "SourceFiles";

    public string PersonFactsTable { get; set; } = "PersonFacts";

    /// <summary>Схема вспомогательных таблиц FAKT; пусто — та же, что у основных.</summary>
    public string AuxiliarySchema { get; set; }

    /// <summary>Сопоставление логических полей с именами столбцов существующей базы.</summary>
    public ColumnMap Columns { get; set; } = new();

    public string EffectiveAuxiliarySchema => string.IsNullOrWhiteSpace(AuxiliarySchema) ? Schema : AuxiliarySchema;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database);

    /// <summary>Нормализованная пара «адрес сервера | имя входа» для привязки сохранённого пароля SQL.</summary>
    public string CredentialTarget()
    {
        var address = (Server ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(Instance))
        {
            address += "\\" + Instance.Trim();
        }

        if (Port.HasValue && Port.Value > 0)
        {
            address += "," + Port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return (address + "|" + (UserName ?? string.Empty).Trim()).ToLowerInvariant();
    }

    public bool PasswordMatchesTarget => string.Equals(PasswordBoundTo, CredentialTarget(), StringComparison.Ordinal);
}

/// <summary>Имена столбцов двух основных таблиц. Значения по умолчанию соответствуют заданию.</summary>
public sealed class ColumnMap
{
    public string SourceFilesId { get; set; } = "ID";
    public string FileCode { get; set; } = "FileCode";
    public string FileName { get; set; } = "FileName";

    public string PersonFactsId { get; set; } = "ID";
    public string Surname { get; set; } = "Фамилия";
    public string Name { get; set; } = "Имя";
    public string Patronymic { get; set; } = "Отчество";
    public string BirthDate { get; set; } = "Дата рождения";
    public string BirthPlace { get; set; } = "Место рождения";
    public string All { get; set; } = "ALL";
    public string FileId { get; set; } = "ID_FileName";

    public ColumnMap Clone() => (ColumnMap)MemberwiseClone();

    public IEnumerable<KeyValuePair<string, string>> SourceFilesColumns()
    {
        yield return new KeyValuePair<string, string>(nameof(SourceFilesId), SourceFilesId);
        yield return new KeyValuePair<string, string>(nameof(FileCode), FileCode);
        yield return new KeyValuePair<string, string>(nameof(FileName), FileName);
    }

    public IEnumerable<KeyValuePair<string, string>> PersonFactsColumns()
    {
        yield return new KeyValuePair<string, string>(nameof(PersonFactsId), PersonFactsId);
        yield return new KeyValuePair<string, string>(nameof(Surname), Surname);
        yield return new KeyValuePair<string, string>(nameof(Name), Name);
        yield return new KeyValuePair<string, string>(nameof(Patronymic), Patronymic);
        yield return new KeyValuePair<string, string>(nameof(BirthDate), BirthDate);
        yield return new KeyValuePair<string, string>(nameof(BirthPlace), BirthPlace);
        yield return new KeyValuePair<string, string>(nameof(All), All);
        yield return new KeyValuePair<string, string>(nameof(FileId), FileId);
    }
}

public sealed class ProcessingSettings
{
    public bool RecursiveScan { get; set; } = true;

    /// <summary>Переходы по junction/symlink/reparse points при сканировании (по умолчанию выключены).</summary>
    public bool FollowReparsePoints { get; set; }

    public bool IncludeHiddenFiles { get; set; }

    public int SampleLines { get; set; } = 5;

    public int SampleMaxBytes { get; set; } = 64 * 1024;

    /// <summary>Расширенный образец по явному действию пользователя.</summary>
    public int ExtendedSampleLines { get; set; } = 30;

    public int ExtendedSampleMaxBytes { get; set; } = 256 * 1024;

    public int PreviewRecords { get; set; } = 100;

    /// <summary>Размер chunk Pandas (записей) — отдельно от размера пакета LLM.</summary>
    public int ChunkSize { get; set; } = 5000;

    /// <summary>Наблюдений в одной SQL-транзакции (не больше; фиксация — по завершённым пакетам).</summary>
    public int SqlBatchSize { get; set; } = 500;

    /// <summary>Предел пакетов в очередях конвейера (чтение → LLM → проверка → SQL).</summary>
    public int QueueCapacity { get; set; } = 32;

    public int MaxAttemptsPerRequest { get; set; } = 6;

    /// <summary>Предел запросов к LLM на одно задание; 0 — без предела. При достижении задание ставится на паузу.</summary>
    public int BudgetMaxRequests { get; set; } = 5000;

    /// <summary>Предел токенов (вход + выход) на задание; 0 — без предела.</summary>
    public long BudgetMaxTokens { get; set; }

    /// <summary>Порог подтверждения анализа структуры: при большем числе файлов показывается оценка запросов.</summary>
    public int ConfirmStructureRequestsAbove { get; set; } = 20;

    /// <summary>Путь к python.exe worker; пусто — поставляемый worker\python\python.exe.</summary>
    public string PythonPath { get; set; }

    /// <summary>Каталог worker (fakt_worker_main.py); пусто — worker рядом с приложением.</summary>
    public string WorkerDirectory { get; set; }
}

public enum SecretScope
{
    /// <summary>DPAPI CurrentUser: секреты доступны только этой учётной записи Windows.</summary>
    CurrentUser,

    /// <summary>DPAPI LocalMachine + ACL файла: секреты доступны назначенным пользователям этого компьютера.</summary>
    LocalMachine,
}

public sealed class AccessSettings
{
    /// <summary>SID владельца, выполнившего первоначальную настройку.</summary>
    public string OwnerSid { get; set; }

    public string OwnerName { get; set; }

    public DateTime? OwnerAssignedAtUtc { get; set; }

    /// <summary>SID пользователей и групп Windows с ролью «Администратор».</summary>
    public List<PrincipalEntry> Administrators { get; set; } = new();

    /// <summary>SID пользователей и групп Windows с ролью «Оператор».</summary>
    public List<PrincipalEntry> Operators { get; set; } = new();

    public SecretScope SecretScope { get; set; } = SecretScope.CurrentUser;

    public bool IsInitialized => !string.IsNullOrEmpty(OwnerSid);
}

public sealed class PrincipalEntry
{
    public string Sid { get; set; }

    /// <summary>Имя на момент назначения (DOMAIN\name) — для отображения; проверка роли идёт по SID.</summary>
    public string DisplayName { get; set; }

    public bool IsGroup { get; set; }
}

public sealed class DiagnosticsSettings
{
    /// <summary>Подробный журнал (без ключей). Включается администратором на ограниченный срок.</summary>
    public DateTime? VerboseUntilUtc { get; set; }

    public string VerboseEnabledBy { get; set; }

    /// <summary>Срок хранения файлов журнала, дней.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Срок хранения подробного журнала, дней.</summary>
    public int VerboseRetentionDays { get; set; } = 3;

    public bool IsVerboseActive(DateTime utcNow) => VerboseUntilUtc.HasValue && VerboseUntilUtc.Value > utcNow;
}
