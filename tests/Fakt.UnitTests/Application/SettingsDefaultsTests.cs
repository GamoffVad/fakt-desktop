using System;
using Fakt.Application.Settings;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Llm;
using Xunit;

namespace Fakt.UnitTests.Application;

/// <summary>
/// Значения по умолчанию: подключение к локальному SQL Server подставляется, если подключение не задано,
/// и не заменяет явно заданные настройки.
/// </summary>
public sealed class SettingsDefaultsTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData(".", true)]
    [InlineData("(local)", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("localhost\\SQLEXPRESS", true)]
    [InlineData("localhost,1433", true)]
    [InlineData("tcp:localhost,1433", true)]
    [InlineData(" localhost ", true)]
    [InlineData("sql01.corp.local", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("localhost.evil.example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LocalServerIsRecognized(string server, bool expected)
    {
        Assert.Equal(expected, DatabaseSettings.IsLocalServer(server));
    }

    [Fact]
    public void MachineNameIsLocal()
    {
        Assert.True(DatabaseSettings.IsLocalServer(Environment.MachineName));
    }

    [Fact]
    public void EmptyConnectionGetsLocalDefaults()
    {
        var settings = new AppSettings();
        settings.Database.Columns.Surname = "LastName";
        settings.Database.CommandTimeoutSeconds = 300;

        SettingsService.WithDefaults(settings);

        var db = settings.Database;
        Assert.Equal("localhost", db.Server);
        Assert.Equal("FAKT", db.Database);
        Assert.Equal(SqlAuthMode.Windows, db.Authentication);
        Assert.Equal(SqlEncryptMode.Optional, db.Encrypt);
        Assert.False(db.TrustServerCertificate); // проверка сертификата не отключается
        Assert.True(db.IsConfigured);
        Assert.Equal("LastName", db.Columns.Surname);
        Assert.Equal(300, db.CommandTimeoutSeconds);
    }

    [Fact]
    public void ExplicitConnectionIsNotChanged()
    {
        var settings = new AppSettings();
        settings.Database.Server = "sql01.corp.local";
        settings.Database.Database = "Facts";
        settings.Database.Authentication = SqlAuthMode.Sql;
        settings.Database.UserName = "fakt_app";

        SettingsService.WithDefaults(settings);

        Assert.Equal("sql01.corp.local", settings.Database.Server);
        Assert.Equal("Facts", settings.Database.Database);
        Assert.Equal(SqlAuthMode.Sql, settings.Database.Authentication);
        Assert.Equal("fakt_app", settings.Database.UserName);
        Assert.Equal(SqlEncryptMode.Mandatory, settings.Database.Encrypt);
    }

    [Fact]
    public void PartiallyFilledConnectionIsNotOverwritten()
    {
        // Пользователь указал только сервер — подстановка не должна менять его выбор.
        var settings = new AppSettings();
        settings.Database.Server = "sql01.corp.local";

        SettingsService.WithDefaults(settings);

        Assert.Equal("sql01.corp.local", settings.Database.Server);
        Assert.Null(settings.Database.Database);
        Assert.Equal(SqlEncryptMode.Mandatory, settings.Database.Encrypt);
    }

    [Fact]
    public void MissingSectionsAreRestored()
    {
        var settings = new AppSettings { Database = null, Processing = null };

        SettingsService.WithDefaults(settings);

        Assert.Equal("localhost", settings.Database.Server);
        Assert.NotNull(settings.Processing);
        Assert.Equal(5, settings.Processing.SampleLines);
    }

    [Fact]
    public void SettingsLoadedFromFileGetDefaults()
    {
        using var harness = new SettingsHarness();

        Assert.Equal("localhost", harness.Service.Current.Database.Server);
        Assert.Equal("FAKT", harness.Service.Current.Database.Database);

        harness.Service.Reload();
        Assert.Equal("FAKT", harness.Service.Current.Database.Database);
    }

    [Fact]
    public void OpenRouterDraftHasVerifiedDefaultModel()
    {
        var openRouter = System.Linq.Enumerable.Single(ProviderCatalog.All, p => p.Id == ProviderCatalog.OpenRouter);

        Assert.Equal("openai/gpt-4.1-mini", openRouter.DefaultModelId);
    }
}
