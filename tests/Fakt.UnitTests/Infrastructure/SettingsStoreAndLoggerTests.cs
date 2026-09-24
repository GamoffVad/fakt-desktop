using System;
using System.IO;
using System.Linq;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Logging;
using Fakt.Infrastructure.Settings;
using Fakt.UnitTests.TestSupport;
using Xunit;

namespace Fakt.UnitTests.Infrastructure;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly JsonSettingsStore _store;

    public JsonSettingsStoreTests()
    {
        _paths = new AppPaths(_temp.Combine("machine"), _temp.Combine("user"));
        _store = new JsonSettingsStore(_paths);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        var settings = _store.Load();

        Assert.Empty(settings.LlmProfiles);
        Assert.Equal("dbo", settings.Database.Schema);
        Assert.False(settings.Access.IsInitialized);
    }

    [Fact]
    public void Settings_RoundTripWithEnumsAsNames()
    {
        var profile = new LlmProfile { Name = "Профиль", ProviderId = "openai", BaseUrl = "https://api.example.test/v1", OutputMode = StructuredOutputMode.JsonObject };
        profile.Options["protocol"] = "responses";
        _store.Save(new AppSettings
        {
            LlmProfiles = { profile },
            ActiveLlmProfileId = profile.Id,
            Database = { Server = "SQL01", Authentication = SqlAuthMode.Sql, UserName = "fakt_app" },
        });

        var loaded = _store.Load();

        var json = File.ReadAllText(_paths.SettingsFile);
        Assert.Contains("\"JsonObject\"", json);
        Assert.Contains("\"Sql\"", json);
        var loadedProfile = Assert.Single(loaded.LlmProfiles);
        Assert.Equal(profile.Id, loadedProfile.Id);
        Assert.Equal("responses", loadedProfile.GetOption("protocol"));
        Assert.Equal(StructuredOutputMode.JsonObject, loadedProfile.OutputMode);
        Assert.Equal(SqlAuthMode.Sql, loaded.Database.Authentication);
        Assert.Equal(profile.Id, loaded.ActiveLlmProfileId);
    }

    [Fact]
    public void SecondSave_KeepsBackupOfPreviousVersion()
    {
        _store.Save(new AppSettings { UpdatedBy = "first" });
        _store.Save(new AppSettings { UpdatedBy = "second" });

        Assert.Equal("second", _store.Load().UpdatedBy);
        Assert.Contains("first", File.ReadAllText(_paths.SettingsFile + ".bak"));
        Assert.False(File.Exists(_paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void CorruptedFile_IsReportedWithBackupHint()
    {
        Directory.CreateDirectory(_paths.MachineDirectory);
        File.WriteAllText(_paths.SettingsFile, "{ \"LlmProfiles\": [ { \"Name\": ");

        var ex = Assert.Throws<InvalidDataException>(() => _store.Load());
        Assert.Contains("settings.json.bak", ex.Message);
    }

    [Fact]
    public void Paths_AreDerivedFromOverriddenDirectories()
    {
        Assert.Equal(_temp.Combine("machine", "settings.json"), _paths.SettingsFile);
        Assert.Equal(_temp.Combine("user", "secrets"), _paths.UserSecretsDirectory);
        Assert.Equal(_temp.Combine("machine", "secrets"), _paths.MachineSecretsDirectory);
        Assert.Equal(_temp.Combine("user", "logs"), _paths.LogDirectory);
    }
}

public sealed class JsonLineLoggerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void WrittenEntries_AreSanitizedOnDisk()
    {
        using (var logger = new JsonLineLogger(_temp.Path, () => false))
        {
            logger.Error("llm.error", "Отказ: sk-proj-AbCdEf1234567890XYZ, телефон +7 (900) 123-45-67", ErrorCategory.Connection, configure: e =>
                e.Data = new System.Collections.Generic.Dictionary<string, object> { ["detail"] = "Authorization: Bearer abcdefgh12345678" });
        }

        var text = string.Join("\n", Directory.GetFiles(_temp.Path, "fakt-*.jsonl").Select(File.ReadAllText));
        Assert.Contains("llm.error", text);
        Assert.DoesNotContain("sk-proj-AbCdEf1234567890XYZ", text);
        Assert.DoesNotContain("123-45-67", text);
        Assert.DoesNotContain("abcdefgh12345678", text);
    }

    [Fact]
    public void ReadLatest_OrdersEntriesWithinOneSecondByMilliseconds()
    {
        var start = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
        using (var writer = new JsonLineLogger(_temp.Path, () => false))
        {
            for (var i = 1; i <= 3; i++)
            {
                writer.Write(new LogEntry { TimestampUtc = start.AddMilliseconds(100 * i), Event = "event." + i, Message = "m" });
            }
        }

        using var reader = new JsonLineLogger(_temp.Path, () => false);
        var latest = reader.ReadLatest(10, includeVerbose: false);

        Assert.Equal(new[] { "event.3", "event.2", "event.1" }, latest.Select(e => e.Event));
    }

    [Fact]
    public void Entries_RoundTripThroughSerialization()
    {
        var entry = new LogEntry
        {
            TimestampUtc = new DateTime(2026, 9, 24, 10, 0, 0, 123, DateTimeKind.Utc),
            Level = LogLevel.Warning,
            Event = "llm.retry",
            Message = "Повтор",
            Category = ErrorCategory.Connection,
            JobId = 42,
            SourceRowId = "r7",
            ErrorCode = "RateLimited",
        };

        var parsed = JsonLineLogger.Parse(JsonLineLogger.Serialize(entry));

        Assert.Equal(entry.TimestampUtc, parsed.TimestampUtc);
        Assert.Equal(LogLevel.Warning, parsed.Level);
        Assert.Equal("llm.retry", parsed.Event);
        Assert.Equal(ErrorCategory.Connection, parsed.Category);
        Assert.Equal(42, parsed.JobId);
        Assert.Equal("r7", parsed.SourceRowId);
        Assert.Equal("RateLimited", parsed.ErrorCode);
    }
}
