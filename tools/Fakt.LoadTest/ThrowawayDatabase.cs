using System;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Sql;

namespace Fakt.LoadTest;

/// <summary>
/// Временная база FaktLoad_… (как SqlServerFixture интеграционных тестов): создаётся прогоном, схема — миграциями
/// приложения, после прогона удаляется. Другие базы сервера не затрагиваются.
/// </summary>
public sealed class ThrowawayDatabase
{
    private ThrowawayDatabase(string server, string name)
    {
        Server = server;
        Name = name;
        Settings = new DatabaseSettings
        {
            Server = server,
            Database = name,
            Authentication = SqlAuthMode.Windows,
            Encrypt = SqlEncryptMode.Mandatory,
            TrustServerCertificate = true, // только для локального тестового сервера с самоподписанным сертификатом
        };
    }

    public string Server { get; }

    public string Name { get; }

    public DatabaseSettings Settings { get; }

    public string ConnectionString => SqlConnectionFactory.Build(Settings, null);

    public static async Task<ThrowawayDatabase> CreateAsync(string server, CancellationToken cancellationToken)
    {
        var database = new ThrowawayDatabase(server, "FaktLoad_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        using var connection = new SqlConnection(MasterConnectionString(server));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand($"CREATE DATABASE {SqlNames.Quote(database.Name)};", connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return database;
    }

    public async Task<object> ScalarAsync(string sql, int timeoutSeconds = 600)
    {
        using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    public async Task DropAsync()
    {
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(MasterConnectionString(Server));
        await connection.OpenAsync().ConfigureAwait(false);
        var sql = $@"
IF DB_ID({SqlNames.Literal(Name)}) IS NOT NULL
BEGIN
    ALTER DATABASE {SqlNames.Quote(Name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {SqlNames.Quote(Name)};
END";
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string MasterConnectionString(string server) => new SqlConnectionStringBuilder
    {
        DataSource = server,
        InitialCatalog = "master",
        IntegratedSecurity = true,
        Encrypt = true,
        TrustServerCertificate = true,
        ConnectTimeout = 15,
        ApplicationName = "FAKT-LoadTest",
    }.ConnectionString;
}
