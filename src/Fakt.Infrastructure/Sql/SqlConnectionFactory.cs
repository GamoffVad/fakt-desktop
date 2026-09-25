using System;
using System.Data.SqlClient;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Logging;
using Fakt.Core.Settings;

namespace Fakt.Infrastructure.Sql;

/// <summary>
/// Подключение через System.Data.SqlClient (.NET Framework 4.8): TDS 7.4, TLS по возможностям SChannel ОС.
/// Encrypt=Strict (TDS 8.0) этим клиентом не поддерживается. Пароль SQL передаётся из хранилища секретов
/// и не записывается в настройки и журнал.
/// </summary>
public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(DatabaseSettings settings, string sqlPassword)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _connectionString = Build(settings, sqlPassword);
        Names = new SqlNames(settings);
    }

    public DatabaseSettings Settings { get; }

    public SqlNames Names { get; }

    public int CommandTimeout => Math.Max(5, Settings.CommandTimeoutSeconds);

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public SqlCommand Command(SqlConnection connection, string text, SqlTransaction transaction = null, int? timeoutSeconds = null)
    {
        return new SqlCommand(text, connection, transaction) { CommandTimeout = timeoutSeconds ?? CommandTimeout };
    }

    public static string Build(DatabaseSettings settings, string password)
    {
        if (string.IsNullOrWhiteSpace(settings.Server))
        {
            throw new ArgumentException("Не указан сервер SQL Server.");
        }

        if (string.IsNullOrWhiteSpace(settings.Database))
        {
            throw new ArgumentException("Не указана база данных.");
        }

        var dataSource = settings.Server.Trim();
        if (!string.IsNullOrWhiteSpace(settings.Instance))
        {
            dataSource += "\\" + settings.Instance.Trim();
        }

        if (settings.Port.HasValue && settings.Port.Value > 0)
        {
            dataSource += "," + settings.Port.Value.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = settings.Database.Trim(),
            Encrypt = settings.Encrypt == SqlEncryptMode.Mandatory,
            TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = Math.Max(1, settings.ConnectTimeoutSeconds),
            ApplicationName = "FAKT",
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false,
            Pooling = true,

            // По умолчанию для локального SQL Server пул после неудачного входа несколько секунд возвращает
            // запомненную ошибку: исправленные настройки или только что созданная база «не работали бы» сразу.
            PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock,
        };

        if (settings.Authentication == SqlAuthMode.Windows)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.UserName))
            {
                throw new ArgumentException("Для SQL Authentication укажите имя входа.");
            }

            builder.UserID = settings.UserName.Trim();
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    /// <summary>Категория и понятное пояснение ошибки SQL для интерфейса и журнала (без строки подключения).</summary>
    public static (ErrorCategory Category, string Message) Describe(Exception exception)
    {
        if (exception is SqlException sql)
        {
            var text = sql.Message ?? string.Empty;
            switch (sql.Number)
            {
                case 18456:
                    return (ErrorCategory.Access, "Вход не выполнен: проверьте учётные данные или права Windows-учётной записи на сервере. " + text);
                case 4060:
                    return (ErrorCategory.Configuration, "База данных недоступна или не существует: " + text);
                case 229:
                case 230:
                case 262:
                case 297:
                case 300:
                case 916:
                    return (ErrorCategory.Access, "Недостаточно прав в базе данных: " + text);
                case -2:
                    return (ErrorCategory.Connection, "Превышено время ожидания SQL Server: " + text);
                case 208:
                case 207:
                    return (ErrorCategory.Configuration, "Объект или столбец не найден — проверьте схему и примените миграции: " + text);
            }

            if (text.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("сертификат", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (ErrorCategory.Connection, "Ошибка защищённого соединения с SQL Server (TLS/сертификат). На Windows 7 включите TLS 1.2 для клиента SChannel и убедитесь, что сертификат сервера доверенный; режим Encrypt=Strict этим клиентом не поддерживается. " + text);
            }

            if (sql.Class >= 20 || sql.Number == 53 || sql.Number == 2 || sql.Number == 10060 || sql.Number == 10061 || sql.Number == 11001 || sql.Number == -1)
            {
                return (ErrorCategory.Connection, "Сервер SQL Server недоступен: " + text);
            }

            return (ErrorCategory.DataFormat, text);
        }

        if (exception is InvalidOperationException || exception is ArgumentException)
        {
            return (ErrorCategory.Configuration, exception.Message);
        }

        return (ErrorCategory.Internal, exception.Message);
    }

    public static bool IsUniqueViolation(SqlException exception) => exception.Number == 2627 || exception.Number == 2601;
}
