using System;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Infrastructure.Sql;
using Xunit;

namespace Fakt.IntegrationTests;

/// <summary>
/// Временная тестовая база FaktIT_… на SQL Server (по умолчанию localhost, Windows Authentication;
/// переопределяется переменной FAKT_TEST_SQL_SERVER). База создаётся тестом и удаляется после него.
/// Если сервер недоступен, тесты падают с объяснением, а не проходят молча.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public string Server { get; } = Environment.GetEnvironmentVariable("FAKT_TEST_SQL_SERVER") ?? "localhost";

    public string Database { get; } = "FaktIT_" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public DatabaseSettings Settings { get; private set; }

    public async Task InitializeAsync()
    {
        using var connection = new SqlConnection(MasterConnectionString());
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"SQL Server «{Server}» недоступен для интеграционных тестов: {ex.Message}. Задайте FAKT_TEST_SQL_SERVER или исключите тесты фильтром Category!=SqlServer.", ex);
        }

        using (var command = new SqlCommand($"CREATE DATABASE {SqlNames.Quote(Database)};", connection))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Settings = NewSettings();
    }

    public DatabaseSettings NewSettings(string schema = "dbo", string auxSchema = null, ColumnMap columns = null, string sourceFiles = "SourceFiles", string personFacts = "PersonFacts") => new()
    {
        Server = Server,
        Database = Database,
        Authentication = SqlAuthMode.Windows,
        Encrypt = SqlEncryptMode.Mandatory,
        TrustServerCertificate = true, // только для локального тестового сервера с самоподписанным сертификатом
        Schema = schema,
        AuxiliarySchema = auxSchema,
        SourceFilesTable = sourceFiles,
        PersonFactsTable = personFacts,
        Columns = columns ?? new ColumnMap(),
        CommandTimeoutSeconds = 120,
    };

    public async Task ApplyAllMigrationsAsync(DatabaseSettings settings)
    {
        var admin = new DatabaseAdmin(null);
        foreach (var migration in MigrationCatalog.All)
        {
            var result = await admin.ApplyMigrationAsync(settings, null, migration.Id, "integration-test", CancellationToken.None).ConfigureAwait(false);
            Assert.True(result.Success, result.Message);
        }
    }

    public async Task<object> ScalarAsync(string sql)
    {
        using var connection = new SqlConnection(SqlConnectionFactory.Build(Settings, null));
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    public async Task ExecuteAsync(string sql)
    {
        using var connection = new SqlConnection(SqlConnectionFactory.Build(Settings, null));
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Ожидание фоновой полнотекстовой индексации (CHANGE_TRACKING AUTO) после записи.</summary>
    public async Task WaitForFullTextAsync(string auxSchema = "dbo", int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var pending = await ScalarAsync($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID(N'{SqlNames.Quote(auxSchema)}.[FaktSearchDocs]'), 'TableFulltextPendingChanges') AS BIGINT)").ConfigureAwait(false);
            var status = await ScalarAsync($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID(N'{SqlNames.Quote(auxSchema)}.[FaktSearchDocs]'), 'TableFulltextPopulateStatus') AS INT)").ConfigureAwait(false);
            if (Convert.ToInt64(pending ?? 0L) == 0 && Convert.ToInt32(status ?? 0) == 0)
            {
                // Небольшая пауза: счётчик pending обнуляется чуть раньше, чем изменения становятся видимы запросам.
                await Task.Delay(1500).ConfigureAwait(false);
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException("Полнотекстовый индекс не обновился за отведённое время.");
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(MasterConnectionString());
        await connection.OpenAsync().ConfigureAwait(false);
        var sql = $@"
IF DB_ID({SqlNames.Literal(Database)}) IS NOT NULL
BEGIN
    ALTER DATABASE {SqlNames.Quote(Database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {SqlNames.Quote(Database)};
END";
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string MasterConnectionString() => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = "master",
        IntegratedSecurity = true,
        Encrypt = true,
        TrustServerCertificate = true,
        ConnectTimeout = 10,
    }.ConnectionString;
}
