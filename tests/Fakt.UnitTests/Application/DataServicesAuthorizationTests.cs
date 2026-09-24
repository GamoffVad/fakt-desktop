using System;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Security;
using Fakt.Application.Services;
using Fakt.Core.Search;
using Fakt.Core.Security;
using Fakt.Core.Settings;
using Fakt.UnitTests.TestSupport;
using Xunit;

namespace Fakt.UnitTests.Application;

/// <summary>Сервисы данных проверяют роль до обращения к базе (Demand), а не только видимостью кнопок.</summary>
public sealed class DataServicesAuthorizationTests
{
    [Fact]
    public async Task Search_WithoutRole_IsDenied()
    {
        using var h = new SettingsHarness(FakeIdentityProvider.Outsider());
        var search = new SearchService(h.Service, h.Service.Authorization, null, null);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => search.SearchAsync(new SearchQuery { Text = "Иванов" }, CancellationToken.None));
        Assert.Equal(Permission.SearchData, ex.Permission);
        await Assert.ThrowsAsync<AccessDeniedException>(() => search.CountAsync(new SearchQuery { Text = "Иванов" }, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => search.GetObservationAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Search_ByOperator_PassesAuthorization()
    {
        using var h = new SettingsHarness(FakeIdentityProvider.Operator());
        var search = new SearchService(h.Service, h.Service.Authorization, null, null);

        // Права есть: дальше сервис сообщает, что база не настроена.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync(new SearchQuery { Text = "Иванов" }, CancellationToken.None));
        Assert.Contains("не настроено", ex.Message);
    }

    [Fact]
    public async Task History_WithoutRole_IsDenied()
    {
        using var h = new SettingsHarness(FakeIdentityProvider.Outsider());
        var history = new HistoryService(h.Service, h.Service.Authorization, null);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => history.ListJobsAsync(0, 10, CancellationToken.None));
        Assert.Equal(Permission.ViewHistory, ex.Permission);
    }

    [Fact]
    public async Task Migrations_RequireAdministrator()
    {
        using var h = new SettingsHarness(FakeIdentityProvider.Operator());
        var admin = new RecordingDatabaseAdmin();
        var database = new DatabaseService(h.Service, h.Service.Authorization, admin, null);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => database.ApplyMigrationAsync("V001", CancellationToken.None));
        Assert.Equal(Permission.ManageDatabase, ex.Permission);
        await Assert.ThrowsAsync<AccessDeniedException>(() => database.RebuildSearchProjectionAsync(null, CancellationToken.None));
        Assert.Equal(0, admin.Calls);
    }

    [Fact]
    public async Task Migration_ByAdministrator_UsesPasswordBoundToCurrentServer()
    {
        using var h = new SettingsHarness(FakeIdentityProvider.Admin());
        h.Service.SaveDatabase(new DatabaseSettings { Server = "SQL01", Database = "FaktDb", Authentication = SqlAuthMode.Sql, UserName = "fakt_app" }, "синтетический-пароль");
        var admin = new RecordingDatabaseAdmin();
        var database = new DatabaseService(h.Service, h.Service.Authorization, admin, null);

        await database.ApplyMigrationAsync("V001", CancellationToken.None);
        Assert.Equal("синтетический-пароль", admin.LastPassword);

        // «Проверить соединение» для другого, ещё не сохранённого сервера не получает сохранённый пароль.
        var other = Newtonsoft.Json.JsonConvert.DeserializeObject<DatabaseSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(h.Service.Current.Database));
        other.Server = "SQL-OTHER";
        await database.InspectAsync(other, null, CancellationToken.None);
        Assert.Null(admin.LastPassword);
    }
}
