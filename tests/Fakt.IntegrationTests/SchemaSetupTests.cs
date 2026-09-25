using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Services;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Sql;
using Xunit;

namespace Fakt.IntegrationTests;

/// <summary>
/// Подключение к существующей (рабочей) базе: FAKT создаёт недостающие таблицы и объекты, существующие таблицы и
/// данные сохраняются. Базы создаются тестом и удаляются после него.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class SchemaSetupTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _sql;

    public SchemaSetupTests(SqlServerFixture sql)
    {
        _sql = sql;
    }

    [Fact]
    public async Task ExistingDatabaseWithForeignTablesGetsFaktTablesAndKeepsData()
    {
        var name = "FaktIT_prod_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var settings = await CreateDatabaseAsync(name, @"
CREATE TABLE dbo.Clients (Id INT IDENTITY PRIMARY KEY, FullName NVARCHAR(200) NOT NULL, Phone VARCHAR(20) NULL);
INSERT INTO dbo.Clients (FullName, Phone) VALUES (N'Иванов Иван', '+79990000001'), (N'Петрова Анна', NULL);");
        try
        {
            var admin = new DatabaseAdmin(null);
            var before = await admin.InspectAsync(settings, null, CancellationToken.None);
            Assert.False(before.CanProcess);

            var plan = SchemaSetupPlan.AutoApply(before);
            var fullText = before.FullText.Installed;
            Assert.Equal(MigrationCatalog.All.Count - (fullText ? 0 : 1), plan.Count); // основных таблиц нет — создаётся всё
            await ApplyAsync(admin, settings, plan);

            var after = await admin.InspectAsync(settings, null, CancellationToken.None);
            Assert.True(after.CanProcess && after.CanSearch, string.Join("; ", after.ProcessingBlockers.Concat(after.SearchBlockers)));
            Assert.Empty(SchemaSetupPlan.AutoApply(after)); // повторное подключение ничего не меняет
            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(settings, "SELECT COUNT(*) FROM dbo.Clients")));
            Assert.Equal("+79990000001", await ScalarAsync(settings, "SELECT Phone FROM dbo.Clients WHERE FullName = N'Иванов Иван'"));
        }
        finally
        {
            await DropAsync(name);
        }
    }

    [Fact]
    public async Task ExistingMainTablesAreExtendedWithoutLosingRows()
    {
        var name = "FaktIT_prod_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var settings = await CreateDatabaseAsync(name, @"
CREATE TABLE dbo.SourceFiles (ID BIGINT IDENTITY PRIMARY KEY, FileCode VARCHAR(32) NOT NULL UNIQUE, FileName NVARCHAR(1024) NOT NULL);
CREATE TABLE dbo.PersonFacts (
    ID BIGINT IDENTITY PRIMARY KEY,
    [Фамилия] NVARCHAR(200) NULL, [Имя] NVARCHAR(200) NULL, [Отчество] NVARCHAR(200) NULL,
    [Дата рождения] DATE NULL, [Место рождения] NVARCHAR(1000) NULL,
    [ALL] NVARCHAR(MAX) NOT NULL,
    ID_FileName BIGINT NOT NULL REFERENCES dbo.SourceFiles (ID));
INSERT INTO dbo.SourceFiles (FileCode, FileName) VALUES ('T_000000000000000001', N'old.csv');
INSERT INTO dbo.PersonFacts ([Фамилия], [Имя], [ALL], ID_FileName) VALUES (N'Сидоров', N'Пётр', N'{""phones"":[""+79990000002""]}', 1), (N'Кузнецова', N'Ольга', N'{}', 1);
CREATE TABLE dbo.Clients (Id INT PRIMARY KEY, FullName NVARCHAR(200));
INSERT INTO dbo.Clients VALUES (1, N'Клиент');");
        try
        {
            var admin = new DatabaseAdmin(null);
            var before = await admin.InspectAsync(settings, null, CancellationToken.None);
            var plan = SchemaSetupPlan.AutoApply(before);
            var left = SchemaSetupPlan.LeftForAdministrator(before);

            // Необязательные изменения существующих таблиц не выполняются автоматически.
            Assert.DoesNotContain(plan, m => m.Id == "V004" || m.Id == "V008");
            Assert.Contains(left, m => m.Id == "V004");
            Assert.Contains(left, m => m.Id == "V008");
            Assert.Contains(plan, m => m.Id == "V002");
            Assert.Contains(plan, m => m.Id == "V003");
            Assert.Contains(plan, m => m.Id == "V005");
            Assert.Contains(plan, m => m.Id == "V006");

            await ApplyAsync(admin, settings, plan);

            var after = await admin.InspectAsync(settings, null, CancellationToken.None);
            Assert.True(after.CanProcess && after.CanSearch, string.Join("; ", after.ProcessingBlockers.Concat(after.SearchBlockers)));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(settings, "SELECT COUNT(*) FROM dbo.SourceFiles")));
            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(settings, "SELECT COUNT(*) FROM dbo.PersonFacts")));
            Assert.Equal("{\"phones\":[\"+79990000002\"]}", await ScalarAsync(settings, "SELECT [ALL] FROM dbo.PersonFacts WHERE [Фамилия] = N'Сидоров'"));
            Assert.Equal("T_000000000000000001", await ScalarAsync(settings, "SELECT FileCode FROM dbo.SourceFiles"));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(settings, "SELECT COUNT(*) FROM dbo.Clients")));
            Assert.Empty(SchemaSetupPlan.AutoApply(after));
        }
        finally
        {
            await DropAsync(name);
        }
    }

    private static async Task ApplyAsync(DatabaseAdmin admin, DatabaseSettings settings, System.Collections.Generic.IReadOnlyList<Fakt.Core.Storage.MigrationInfo> plan)
    {
        foreach (var migration in plan)
        {
            var result = await admin.ApplyMigrationAsync(settings, null, migration.Id, "integration-test", CancellationToken.None);
            Assert.True(result.Success, migration.Id + ": " + result.Message);
        }
    }

    private async Task<DatabaseSettings> CreateDatabaseAsync(string name, string setupSql)
    {
        var master = _sql.NewSettings();
        master.Database = "master";
        using (var connection = new SqlConnection(SqlConnectionFactory.Build(master, null)))
        {
            await connection.OpenAsync();
            using var create = new SqlCommand($"CREATE DATABASE {SqlNames.Quote(name)};", connection);
            await create.ExecuteNonQueryAsync();
        }

        var settings = _sql.NewSettings();
        settings.Database = name;
        using (var connection = new SqlConnection(SqlConnectionFactory.Build(settings, null)))
        {
            await connection.OpenAsync();
            using var command = new SqlCommand(setupSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        return settings;
    }

    private static async Task<object> ScalarAsync(DatabaseSettings settings, string sql)
    {
        using var connection = new SqlConnection(SqlConnectionFactory.Build(settings, null));
        await connection.OpenAsync();
        using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task DropAsync(string name)
    {
        SqlConnection.ClearAllPools();
        var master = _sql.NewSettings();
        master.Database = "master";
        using var connection = new SqlConnection(SqlConnectionFactory.Build(master, null));
        await connection.OpenAsync();
        using var command = new SqlCommand($@"
IF DB_ID({SqlNames.Literal(name)}) IS NOT NULL
BEGIN
    ALTER DATABASE {SqlNames.Quote(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {SqlNames.Quote(name)};
END", connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
