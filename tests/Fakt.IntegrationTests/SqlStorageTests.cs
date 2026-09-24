using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Files;
using Fakt.Core.Processing;
using Fakt.Core.Search;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Infrastructure.Sql;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Fakt.IntegrationTests;

[Trait("Category", "SqlServer")]
public sealed class SqlStorageTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _db;
    private readonly ITestOutputHelper _output;

    public SqlStorageTests(SqlServerFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
    }

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static bool _migrated;

    private async Task EnsureMigratedAsync()
    {
        await MigrationGate.WaitAsync();
        try
        {
            if (!_migrated)
            {
                await _db.ApplyAllMigrationsAsync(_db.Settings);
                _migrated = true;
            }
        }
        finally
        {
            MigrationGate.Release();
        }
    }

    private async Task<(IStorage Storage, SourceFileRow Source, long JobId, long JobFileId)> NewFileAsync(string fileName)
    {
        await EnsureMigratedAsync();
        var storage = new SqlStorageFactory().Create(_db.Settings, null);
        var source = await storage.SourceFiles.RegisterAsync(new SourceFileRegistration
        {
            FileName = fileName,
            FullPath = @"C:\data\" + fileName,
            PathHash = TestData.Sha(@"C:\DATA\" + fileName.ToUpperInvariant() + Guid.NewGuid()),
            Size = 1234,
            LastWriteTimeUtc = DateTime.UtcNow,
            ContentHash = TestData.Sha(fileName + Guid.NewGuid()),
        }, CancellationToken.None);
        var jobId = await storage.Jobs.CreateJobAsync(new JobRecord { Status = JobStatus.Running, CreatedBy = "test", SnapshotJson = "{}" }, CancellationToken.None);
        var jobFileId = await storage.Jobs.AddJobFileAsync(new JobFileRecord
        {
            JobId = jobId,
            SourceFileId = source.Id,
            FileCode = source.FileCode,
            FileName = fileName,
            Status = FileStatus.Processing,
            ExtractionVersion = TestData.Context.ExtractionVersion,
            ExpectedSize = 1234,
            ExpectedLastWriteUtc = DateTime.UtcNow,
        }, CancellationToken.None);
        return (storage, source, jobId, jobFileId);
    }

    [Fact]
    public async Task Migrations_AreRepeatable_AndSchemaReportAllowsProcessingAndSearch()
    {
        await EnsureMigratedAsync();
        // Повторное применение всех миграций безопасно (объекты создаются только при отсутствии).
        await _db.ApplyAllMigrationsAsync(_db.Settings);

        var report = await new DatabaseAdmin(null).InspectAsync(_db.Settings, null, CancellationToken.None);
        foreach (var issue in report.Issues)
        {
            _output.WriteLine($"{issue.Severity}: {issue.Message}");
        }

        Assert.True(report.Connected, report.ConnectionError);
        Assert.StartsWith("17.", report.ProductVersion);
        Assert.True(report.CanProcess, string.Join("; ", report.ProcessingBlockers));
        Assert.True(report.CanSearch, string.Join("; ", report.SearchBlockers));
        Assert.True(report.FullText.Ready, "Полнотекстовый индекс должен быть создан миграцией V007");
        Assert.Equal(1049, report.FullText.Language);
        Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.Blocking);
        Assert.All(report.Migrations, m => Assert.True(m.Applied, m.Id));
        Assert.Equal(200, report.Limits.Surname);
        Assert.Equal(1000, report.Limits.BirthPlace);
    }

    [Fact]
    public async Task ExistingSchemaWithOtherNames_ReportsExactDifferences_ThenWorksAfterMappingAndMigrations()
    {
        await EnsureMigratedAsync();
        // Существующая «чужая» схема: другие имена таблиц и столбцов, без технических полей.
        await _db.ExecuteAsync(@"
CREATE SCHEMA [legacy];");
        await _db.ExecuteAsync(@"
CREATE TABLE [legacy].[Sources] ([Id] BIGINT IDENTITY PRIMARY KEY, [Code] VARCHAR(32) NOT NULL UNIQUE, [Title] NVARCHAR(500) NOT NULL);
CREATE TABLE [legacy].[Facts] (
    [Id] BIGINT IDENTITY PRIMARY KEY, [Fam] NVARCHAR(100) NULL, [Im] NVARCHAR(100) NULL, [Otch] NVARCHAR(100) NULL,
    [Born] DATE NULL, [Place] NVARCHAR(500) NULL, [Extra] NVARCHAR(MAX) NOT NULL, [SourceId] BIGINT NOT NULL REFERENCES [legacy].[Sources]([Id]));
INSERT INTO [legacy].[Sources] ([Code], [Title]) VALUES ('T_000000000000000001', N'old.csv');
INSERT INTO [legacy].[Facts] ([Fam], [Extra], [SourceId]) VALUES (N'Старый', N'{}', 1);");

        var unmapped = _db.NewSettings("legacy", "legacy_aux", sourceFiles: "Sources", personFacts: "Facts");
        var admin = new DatabaseAdmin(null);
        var before = await admin.InspectAsync(unmapped, null, CancellationToken.None);
        Assert.False(before.CanProcess);
        Assert.Contains(before.Issues, i => i.LogicalField == "FileCode" && i.Severity == IssueSeverity.Blocking);
        Assert.Contains(before.Issues, i => i.LogicalField == "Фамилия" && i.Message.Contains("не найден"));

        var mapped = _db.NewSettings("legacy", "legacy_aux", new ColumnMap
        {
            SourceFilesId = "Id", FileCode = "Code", FileName = "Title",
            PersonFactsId = "Id", Surname = "Fam", Name = "Im", Patronymic = "Otch", BirthDate = "Born", BirthPlace = "Place", All = "Extra", FileId = "SourceId",
        }, "Sources", "Facts");
        var afterMapping = await admin.InspectAsync(mapped, null, CancellationToken.None);
        Assert.DoesNotContain(afterMapping.Issues, i => i.Message.Contains("не найден") && i.LogicalField != null);
        Assert.Contains(afterMapping.Issues, i => i.MigrationId == "V002" && i.Severity == IssueSeverity.Blocking);
        Assert.Contains(afterMapping.Issues, i => i.MigrationId == "V003" && i.Severity == IssueSeverity.Blocking);
        Assert.Contains(afterMapping.Issues, i => i.Message.Contains("длина 100 меньше рекомендуемой 200"));
        Assert.Equal(100, afterMapping.Limits.Surname);

        foreach (var id in new[] { "V002", "V003", "V004", "V005", "V006", "V007" })
        {
            var result = await admin.ApplyMigrationAsync(mapped, null, id, "test", CancellationToken.None);
            Assert.True(result.Success, result.Message);
        }

        var after = await admin.InspectAsync(mapped, null, CancellationToken.None);
        Assert.True(after.CanProcess, string.Join("; ", after.ProcessingBlockers));
        // Существующие данные не изменены.
        Assert.Equal(1, Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM [legacy].[Facts] WHERE [Fam] = N'Старый'")));
    }

    [Fact]
    public async Task RegisterSource_SameVersionReused_ChangedContentGetsNewCode()
    {
        await EnsureMigratedAsync();
        var storage = new SqlStorageFactory().Create(_db.Settings, null);
        var pathHash = TestData.Sha(@"C:\DATA\VERSIONED.CSV");
        var registration = new SourceFileRegistration
        {
            FileName = "versioned.csv", FullPath = @"C:\data\versioned.csv", PathHash = pathHash, Size = 10,
            LastWriteTimeUtc = DateTime.UtcNow, ContentHash = TestData.Sha("content-1"),
        };
        var first = await storage.SourceFiles.RegisterAsync(registration, CancellationToken.None);
        var again = await storage.SourceFiles.RegisterAsync(registration, CancellationToken.None);
        registration.ContentHash = TestData.Sha("content-2");
        var changed = await storage.SourceFiles.RegisterAsync(registration, CancellationToken.None);

        Assert.True(first.Created);
        Assert.True(FileCodeGenerator.IsValid(first.FileCode), first.FileCode);
        Assert.False(again.Created);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.FileCode, again.FileCode);
        Assert.True(changed.Created);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.NotEqual(first.FileCode, changed.FileCode);
    }

    [Fact]
    public async Task Commit_IsIdempotent_ForReplayedCommitAndForRepeatedRows()
    {
        var (storage, source, jobId, jobFileId) = await NewFileAsync("idempotent.csv");
        var r1 = TestData.Record(1, ("ФИО", "Тестов Иван Петрович; Примерова Анна"), ("Телефон", "+7 000 111-22-33"));
        var r2 = TestData.Record(2, ("ФИО", "нет данных"));
        var r3 = TestData.Record(3, ("ФИО", "сломано"));
        var rows = new[]
        {
            TestData.Extracted(r1,
                TestData.Person("Тестов", "Иван", "Петрович", new DateTime(1990, 2, 15), "г. Тестовск", TestData.Fact(FactTypes.Phone, "+7 000 111-22-33", "Телефон")),
                TestData.Person("Примерова", "Анна", null, null, null)),
            TestData.NoFacts(r2),
            Fakt.Core.Extraction.RowOutcome.Error(3, 4, r3.Hash, "invalid_response", "Модель не вернула корректный результат"),
        };
        var unit = TestData.Unit(jobId, jobFileId, source.Id, "idempotent.csv", source.FileCode, 3, rows);

        using var writer = storage.CreateWriter(new FieldLimits());
        var first = await writer.CommitAsync(unit, CancellationToken.None);
        Assert.False(first.Duplicate);
        Assert.Equal(2, first.ObservationsInserted);
        Assert.Equal(1, first.ExtractedRows);
        Assert.Equal(1, first.NoFactsRows);
        Assert.Equal(1, first.ErrorRows);
        Assert.Equal(3, first.ConfirmedOrdinal);

        // Та же фиксация повторно (потеря подтверждения): ничего не меняется.
        var replay = await writer.CommitAsync(unit, CancellationToken.None);
        Assert.True(replay.Duplicate);

        // Те же строки другой фиксацией: реестр строк не даёт дубликатов.
        var again = TestData.Unit(jobId, jobFileId, source.Id, "idempotent.csv", source.FileCode, 3, rows);
        var repeated = await writer.CommitAsync(again, CancellationToken.None);
        Assert.False(repeated.Duplicate);
        Assert.Equal(0, repeated.ObservationsInserted);
        Assert.Equal(3, repeated.RowsAlreadyPresent);

        Assert.Equal(2, Convert.ToInt32(await _db.ScalarAsync($"SELECT COUNT(*) FROM [dbo].[PersonFacts] WHERE [ID_FileName] = {source.Id}")));
        Assert.Equal(3, Convert.ToInt32(await _db.ScalarAsync($"SELECT COUNT(*) FROM [dbo].[FaktRowOutcomes] WHERE [SourceFileId] = {source.Id}")));
        Assert.Equal(1, Convert.ToInt32(await _db.ScalarAsync($"SELECT COUNT(*) FROM [dbo].[FaktRowErrors] WHERE [SourceFileId] = {source.Id}")));
        Assert.Equal(2, Convert.ToInt32(await _db.ScalarAsync($"SELECT COUNT(*) FROM [dbo].[FaktSearchDocs] WHERE [SourceFileId] = {source.Id}")));

        var progress = await storage.Jobs.GetFileProgressAsync(source.Id, TestData.Context.ExtractionVersion, CancellationToken.None);
        Assert.Equal(3, progress.ConfirmedOrdinal);
        Assert.Equal(1, progress.Extracted);
        Assert.Equal(1, progress.NoFacts);
        Assert.Equal(1, progress.Errors);
        Assert.Equal(2, progress.Observations);

        // Порядок идентификаторов следует порядку записей и лиц.
        var order = Convert.ToString(await _db.ScalarAsync($@"
SELECT STRING_AGG(CONCAT([SourceRecordKey], ':', [PersonIndex]), ',') WITHIN GROUP (ORDER BY [ID])
FROM [dbo].[PersonFacts] WHERE [ID_FileName] = {source.Id}"));
        Assert.Equal("1:0,1:1", order);

        // Строковые значения и JSON сохранены без изменений; [ALL] — валидный JSON.
        var all = JObject.Parse(Convert.ToString(await _db.ScalarAsync($"SELECT [ALL] FROM [dbo].[PersonFacts] WHERE [ID_FileName] = {source.Id} AND [PersonIndex] = 0")));
        Assert.Equal("+70001112233", (string)all["facts"][0]["normalized_value"]);
        Assert.Equal("r1", (string)all["provenance"]["source_row_id"]);

        var jobFile = (await storage.Jobs.ListJobFilesAsync(jobId, CancellationToken.None)).Single();
        Assert.Equal(1, jobFile.RecordsExtracted);
        Assert.Equal(2, jobFile.ObservationsSaved);
        Assert.Equal(2, jobFile.Requests); // запросы учтены у двух фиксаций, выполненных как новые
    }

    [Fact]
    public async Task CrashAfterCommitBeforeAcknowledgement_RetryDoesNotDuplicate()
    {
        var (storage, source, jobId, jobFileId) = await NewFileAsync("crash.csv");
        var record = TestData.Record(1, ("ФИО", "Шаблонов Олег"));
        var unit = TestData.Unit(jobId, jobFileId, source.Id, "crash.csv", source.FileCode, 1,
            TestData.Extracted(record, TestData.Person("Шаблонов", "Олег", null, null, null)));

        var writer = (SqlFactWriter)storage.CreateWriter(new FieldLimits());
        writer.AfterCommitHook = () => throw new InvalidOperationException("Сбой после COMMIT до подтверждения");
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(unit, CancellationToken.None));
        writer.Dispose();

        using var retryWriter = storage.CreateWriter(new FieldLimits());
        var retry = await retryWriter.CommitAsync(unit, CancellationToken.None);
        Assert.True(retry.Duplicate);
        Assert.Equal(1, Convert.ToInt32(await _db.ScalarAsync($"SELECT COUNT(*) FROM [dbo].[PersonFacts] WHERE [ID_FileName] = {source.Id}")));
    }

    [Fact]
    public async Task Search_CombinesWordsFromBothTables_TypedFilters_AndNormalizedIdentifiers()
    {
        var (storage, source, jobId, jobFileId) = await NewFileAsync("clients_2024.csv");
        var rows = new[]
        {
            TestData.Extracted(TestData.Record(1, ("ФИО", "Синтетиков Глеб Романович"), ("Телефон", "+7 (000) 123-45-67")),
                TestData.Person("Синтетиков", "Глеб", "Романович", new DateTime(1985, 3, 1), "г. Новотестовск",
                    TestData.Fact(FactTypes.Phone, "+7 (000) 123-45-67", "Телефон"),
                    TestData.Fact(FactTypes.BankAccount, "00123456789012345678", "Счёт"))),
            TestData.Extracted(TestData.Record(2, ("ФИО", "Столбцова Вера"), ("Телефон", "8 (000) 123-45-67")),
                TestData.Person("Столбцова", "Вера", null, new DateTime(1999, 12, 31), "с. Опытное",
                    TestData.Fact(FactTypes.Phone, "8 (000) 123-45-67", "Телефон"),
                    TestData.Fact(FactTypes.LicensePlate, "А000ВС000", "Госномер"))),
        };
        using (var writer = storage.CreateWriter(new FieldLimits()))
        {
            await writer.CommitAsync(TestData.Unit(jobId, jobFileId, source.Id, "clients_2024.csv", source.FileCode, 2, rows), CancellationToken.None);
        }

        await _db.WaitForFullTextAsync();
        var search = storage.Search;

        // Слова из обеих таблиц в одном запросе: фамилия + часть имени файла.
        var combined = await search.SearchAsync(new SearchQuery { Text = "Синтетиков clients", Mode = SearchMode.AllWords }, CancellationToken.None);
        Assert.Single(combined.Rows);
        Assert.Equal(source.FileCode, combined.Rows[0].FileCode);
        Assert.Equal("clients_2024.csv", combined.Rows[0].FileName);
        Assert.Equal(source.Id, combined.Rows[0].FileId);

        // Код файла ищется полнотекстово.
        var byCode = await search.SearchAsync(new SearchQuery { Text = source.FileCode, Mode = SearchMode.AllWords }, CancellationToken.None);
        Assert.Equal(2, byCode.Rows.Count);

        // Любое слово и точная фраза.
        var any = await search.SearchAsync(new SearchQuery { Text = "Синтетиков Столбцова", Mode = SearchMode.AnyWord }, CancellationToken.None);
        Assert.Equal(2, any.Rows.Count);
        var phrase = await search.SearchAsync(new SearchQuery { Text = "Глеб Романович", Mode = SearchMode.ExactPhrase }, CancellationToken.None);
        Assert.Single(phrase.Rows);

        // Телефон в другом формате находится по нормализованному значению; «8…» — другой номер.
        var phone = await search.SearchAsync(new SearchQuery { Identifier = "+7 000 123 45 67", FactType = FactTypes.Phone }, CancellationToken.None);
        Assert.Single(phone.Rows);
        Assert.Equal("Синтетиков", phone.Rows[0].Surname);
        var prefix = await search.SearchAsync(new SearchQuery { Identifier = "7000123", IdentifierPrefix = true }, CancellationToken.None);
        Assert.Single(prefix.Rows);

        // Счёт с ведущими нулями и госномер латиницей.
        var account = await search.SearchAsync(new SearchQuery { Identifier = "0012 3456 7890 1234 5678" }, CancellationToken.None);
        Assert.Single(account.Rows);
        var plate = await search.SearchAsync(new SearchQuery { Identifier = "A000BC000", FactType = FactTypes.LicensePlate }, CancellationToken.None);
        Assert.Single(plate.Rows);

        // Типизированные предикаты: диапазон дат, ФИО по началу, место рождения, ID.
        var dates = await search.SearchAsync(new SearchQuery { BirthDateFrom = new DateTime(1990, 1, 1), BirthDateTo = new DateTime(2000, 1, 1), FileId = source.Id }, CancellationToken.None);
        Assert.Equal("Столбцова", Assert.Single(dates.Rows).Surname);
        var surname = await search.SearchAsync(new SearchQuery { Surname = "Синтет", FileId = source.Id }, CancellationToken.None);
        Assert.Single(surname.Rows);
        var place = await search.SearchAsync(new SearchQuery { BirthPlace = "Новотестовск" }, CancellationToken.None);
        Assert.Single(place.Rows);
        var byId = await search.SearchAsync(new SearchQuery { PersonFactId = combined.Rows[0].PersonFactId }, CancellationToken.None);
        Assert.Single(byId.Rows);

        // Подстрока — явно отдельный режим.
        var substring = await search.SearchAsync(new SearchQuery { Text = "интетико", Mode = SearchMode.Substring }, CancellationToken.None);
        Assert.Single(substring.Rows);
        Assert.Contains(substring.Notices, n => n.Contains("LIKE"));

        // Пагинация и отдельный подсчёт.
        var paged = await search.SearchAsync(new SearchQuery { FileId = source.Id, PageSize = 1, Sort = SearchSort.IdAscending }, CancellationToken.None);
        Assert.Single(paged.Rows);
        Assert.True(paged.HasMore);
        Assert.Equal(2, await search.CountAsync(new SearchQuery { FileId = source.Id }, CancellationToken.None));

        // Карточка: все поля обеих таблиц, включая технические.
        var details = await search.GetObservationAsync(combined.Rows[0].PersonFactId, CancellationToken.None);
        Assert.Equal(source.FileCode, details.FileCode);
        Assert.Contains(details.PersonFactsColumns, c => c.Key == "SourceRecordKey" && c.Value == "1");
        Assert.Contains(details.SourceFilesColumns, c => c.Key == "ContentHash");
        Assert.Contains("\"bank_account\"", details.AllJson);

        // Недопустимый синтаксис отклоняется до SQL.
        await Assert.ThrowsAsync<SearchValidationException>(() => search.SearchAsync(new SearchQuery { Text = "*тетик" }, CancellationToken.None));
    }

    [Fact]
    public async Task RebuildSearchProjection_RestoresDocumentsAndIdentifierIndex()
    {
        var (storage, source, jobId, jobFileId) = await NewFileAsync("rebuild.csv");
        using (var writer = storage.CreateWriter(new FieldLimits()))
        {
            await writer.CommitAsync(TestData.Unit(jobId, jobFileId, source.Id, "rebuild.csv", source.FileCode, 1,
                TestData.Extracted(TestData.Record(1, ("ФИО", "Реестров Павел"), ("Email", "p.reestrov@example.com")),
                    TestData.Person("Реестров", "Павел", null, null, null, TestData.Fact(FactTypes.Email, "p.reestrov@example.com", "Email")))), CancellationToken.None);
        }

        var before = Convert.ToString(await _db.ScalarAsync($"SELECT [AllText] FROM [dbo].[FaktSearchDocs] WHERE [SourceFileId] = {source.Id}"));
        await _db.ExecuteAsync($"DELETE FROM [dbo].[FaktSearchDocs] WHERE [SourceFileId] = {source.Id}; DELETE v FROM [dbo].[FaktFactValues] v JOIN [dbo].[PersonFacts] p ON p.[ID] = v.[PersonFactId] WHERE p.[ID_FileName] = {source.Id};");

        var rebuilt = await new DatabaseAdmin(null).RebuildSearchProjectionAsync(_db.Settings, null, null, CancellationToken.None);
        Assert.True(rebuilt >= 1);
        var after = Convert.ToString(await _db.ScalarAsync($"SELECT [AllText] FROM [dbo].[FaktSearchDocs] WHERE [SourceFileId] = {source.Id}"));
        Assert.Contains("Реестров", after);
        Assert.Contains("p.reestrov@example.com", after);
        Assert.Contains(source.FileCode, after);
        Assert.Contains(source.FileCode, before);
        Assert.Equal(1, Convert.ToInt32(await _db.ScalarAsync($@"
SELECT COUNT(*) FROM [dbo].[FaktFactValues] v JOIN [dbo].[PersonFacts] p ON p.[ID] = v.[PersonFactId]
WHERE p.[ID_FileName] = {source.Id} AND v.[FactType] = 'email' AND v.[NormalizedValue] = N'p.reestrov@example.com'")));
    }
}
