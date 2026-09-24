using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Sql;
using Xunit;

namespace Fakt.IntegrationTests;

/// <summary>
/// Автоматическое создание базы, указанной в настройках: отсутствующая база создаётся вместе со схемой FAKT,
/// существующая не изменяется.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class DatabaseProvisioningTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _sql;

    public DatabaseProvisioningTests(SqlServerFixture sql)
    {
        _sql = sql;
    }

    [Fact]
    public async Task MissingDatabaseIsCreatedWithFullSchema()
    {
        var name = "FaktIT_auto_" + Guid.NewGuid().ToString("N").Substring(0, 10);
        var settings = _sql.NewSettings();
        settings.Database = name;
        var admin = new DatabaseAdmin(null);
        try
        {
            var result = await admin.EnsureDatabaseAsync(settings, null, "integration-test", CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.True(result.Created);
            Assert.False(result.Existed);
            Assert.Equal(MigrationCatalog.All.Select(m => m.Id), result.AppliedMigrations);

            var report = await admin.InspectAsync(settings, null, CancellationToken.None);
            Assert.True(report.Connected, report.ConnectionError);
            Assert.True(report.CanProcess, string.Join("; ", report.ProcessingBlockers));
            Assert.True(report.CanSearch, string.Join("; ", report.SearchBlockers));
            Assert.All(report.Migrations, m => Assert.True(m.Applied, m.Id));

            // Повторный вызов: база уже есть — ничего не создаётся и не применяется.
            var again = await admin.EnsureDatabaseAsync(settings, null, "integration-test", CancellationToken.None);
            Assert.True(again.Success, again.Message);
            Assert.True(again.Existed);
            Assert.False(again.Created);
            Assert.Empty(again.AppliedMigrations);
        }
        finally
        {
            await DropAsync(name);
        }
    }

    [Fact]
    public async Task ExistingDatabaseIsLeftUnchanged()
    {
        // База фикстуры существует, но таблиц FAKT в ней нет: автоматические миграции к ней не применяются.
        var settings = _sql.NewSettings();
        var admin = new DatabaseAdmin(null);

        var result = await admin.EnsureDatabaseAsync(settings, null, "integration-test", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Existed);
        Assert.False(result.Created);
        Assert.Empty(result.AppliedMigrations);
        var tables = Convert.ToInt32(await _sql.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN (N'SourceFiles', N'PersonFacts')"));
        Assert.Equal(0, tables);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0001name")]
    public async Task InvalidDatabaseNameIsRejectedWithoutTouchingServer(string name)
    {
        var settings = _sql.NewSettings();
        settings.Database = name;

        var result = await new DatabaseAdmin(null).EnsureDatabaseAsync(settings, null, "integration-test", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Created);
    }

    [Fact]
    public async Task NameWithBracketsAndQuotesIsQuotedSafely()
    {
        // Имя с ] и ' не должно ломать CREATE DATABASE и не должно позволять выполнить посторонний SQL.
        var name = "FaktIT_q]'x_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var settings = _sql.NewSettings();
        settings.Database = name;
        try
        {
            var result = await new DatabaseAdmin(null).EnsureDatabaseAsync(settings, null, "integration-test", CancellationToken.None);
            Assert.True(result.Success, result.Message);
            Assert.True(result.Created);
            var id = await _sql.ScalarAsync($"SELECT DB_ID({SqlNames.Literal(name)})");
            Assert.NotNull(id);
            Assert.NotEqual(DBNull.Value, id);
        }
        finally
        {
            await DropAsync(name);
        }
    }

    private async Task DropAsync(string name)
    {
        SqlConnection.ClearAllPools();
        var master = _sql.NewSettings();
        master.Database = "master";
        using var connection = new SqlConnection(SqlConnectionFactory.Build(master, null));
        await connection.OpenAsync();
        var sql = $@"
IF DB_ID({SqlNames.Literal(name)}) IS NOT NULL
BEGIN
    ALTER DATABASE {SqlNames.Quote(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {SqlNames.Quote(name)};
END";
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
