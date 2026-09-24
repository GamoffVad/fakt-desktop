using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Processing;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Processing;
using Fakt.Core.Search;
using Fakt.Core.Structure;
using Fakt.Infrastructure.Sql;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Fakt.IntegrationTests.RealLlm;

/// <summary>
/// Реальный интеграционный тест на небольшом наборе синтетических файлов: настоящий провайдер LLM (без имитатора),
/// настоящий Python worker и SQL Server. Результат модели сравнивается с эталоном testdata/_expected; точность
/// модели не принимается за 100 % — пороги и фактические значения выводятся в отчёт теста.
/// </summary>
[Trait("Category", "RealLlm")]
public sealed class RealLlmTests : IClassFixture<RealLlmEnvironment>
{
    private static readonly string[] StructureFiles =
    {
        "structured/csv/clients_utf8_semicolon.csv",
        "structured/csv/contacts_windows1251.csv",
        "structured/tsv/vehicles.tsv",
        "structured/txt/no_header_comma.txt",
        "structured/txt/no_header_mixed.txt",
        "structured/txt/registry_fixed_width.txt",
        "structured/xml/persons_namespaces.xml",
        "structured/jsonl/events.jsonl",
        "unstructured/letter_to_department.txt",
        "binary/photo.png",
    };

    private static readonly string[] ProcessingFiles =
    {
        "structured/csv/applicants_two_persons.csv",
        "structured/csv/prompt_injection_rows.csv",
        "structured/csv/leading_zeros_ids.csv",
        "structured/txt/no_header_comma.txt",
        "structured/txt/no_header_mixed.txt",
    };

    private readonly RealLlmEnvironment _env;
    private readonly ITestOutputHelper _output;

    public RealLlmTests(RealLlmEnvironment env, ITestOutputHelper output)
    {
        _env = env;
        _output = output;
    }

    [RealLlmFact]
    public async Task ConnectionModelListAndExtractionProbe()
    {
        Assert.Null(_env.InitializationError);
        var runtime = _env.Runtime();
        _output.WriteLine($"Провайдер: {_env.Profile.ProviderId}, адрес: {_env.Profile.BaseUrl}, модель: {_env.Profile.ModelId}");

        var connection = await _env.Profiles.TestConnectionAsync(runtime, CancellationToken.None);
        _output.WriteLine($"Проверка соединения: {(connection.Success ? "успешно" : "ошибка")} · {connection.Message} · {connection.Elapsed.TotalMilliseconds:0} мс");
        Assert.True(connection.Success, connection.Message);

        var models = await _env.Profiles.ListModelsAsync(runtime, CancellationToken.None);
        _output.WriteLine($"Список моделей: {models.Status} · {models.Models.Count} · {models.Message}");
        if (models.Status == ModelListStatus.Loaded)
        {
            Assert.Contains(models.Models, m => string.Equals(m.Id, RealLlmConfig.Model, StringComparison.OrdinalIgnoreCase));
        }

        var probe = _env.Probe;
        _output.WriteLine($"Тест извлечения: {(probe.Success ? "успешно" : "ошибка")} · режим {probe.ModeUsed?.ToString() ?? "—"} · {probe.Elapsed.TotalSeconds:0.0} с · токены {probe.InputTokens}/{probe.OutputTokens}");
        foreach (var line in probe.ProbeLog)
        {
            _output.WriteLine("  " + line);
        }

        foreach (var check in probe.Checks)
        {
            _output.WriteLine($"  [{(check.Passed ? "OK" : "FAIL")}] {check.Title}{(string.IsNullOrEmpty(check.Details) ? string.Empty : " — " + check.Details)}");
        }

        Assert.True(probe.ModeUsed.HasValue, probe.Message);
        Assert.True(probe.Success, probe.Message);
    }

    [RealLlmFact]
    public async Task StructureDetectionOnDifferentFormats()
    {
        Assert.Null(_env.InitializationError);
        Assert.True(_env.Probe?.ModeUsed.HasValue == true, "Возможности модели не подтверждены: " + _env.Probe?.Message);
        var root = Path.Combine(_env.RepositoryRoot, "testdata");
        var manifest = LoadManifest(root);
        var scanned = (await RealLlmEnvironment.ScanAsync(root)).ToDictionary(f => f.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        var llm = _env.Client();
        var settings = _env.Settings.Current.Processing;
        var problems = new List<string>();
        using var worker = _env.Workers.Create();
        foreach (var relative in StructureFiles)
        {
            var file = scanned[relative];
            var expected = manifest[relative];
            var result = await _env.Structure.DetectAsync(file, worker, llm, _env.Probe.ModeUsed.Value, settings, false, null, CancellationToken.None);
            if (result.Status == FileStatus.NeedsConfiguration && result.ModelStructure?.Classification == StructureDescriptor.ClassInsufficientSample)
            {
                // Как пользователь по кнопке «Расширенный образец»: модель сочла первых строк недостаточно.
                _output.WriteLine($"{relative}: {result.Message} → повтор с расширенным образцом");
                result = await _env.Structure.DetectAsync(file, worker, llm, _env.Probe.ModeUsed.Value, settings, true, null, CancellationToken.None);
            }

            var structure = result.Structure ?? result.ModelStructure;
            _output.WriteLine($"{relative}: {result.Status.ToText()} · {structure?.Describe() ?? "—"} · {result.Elapsed.TotalSeconds:0.0} с" +
                              (string.IsNullOrEmpty(result.Message) ? string.Empty : " · " + result.Message));
            if (structure?.HasInferredColumnNames == true || structure?.Format == StructureDescriptor.FormatFixedWidth)
            {
                _output.WriteLine("   колонки: " + string.Join(" | ", structure.Columns ?? new List<string>()) +
                                  (structure.FixedWidths == null ? string.Empty : "; ширины: " + string.Join(",", structure.FixedWidths)));
            }

            foreach (var warning in result.Warnings)
            {
                _output.WriteLine("   предупреждение: " + warning);
            }
            foreach (var mismatch in Compare(expected, result.Status, result.Structure))
            {
                problems.Add($"{relative}: {mismatch}");
                _output.WriteLine("   ✗ " + mismatch);
            }
        }

        Assert.True(problems.Count == 0, "Расхождения с эталоном:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [RealLlmFact]
    public async Task ProcessingSavesPersonsAndSearchFindsThem()
    {
        Assert.Null(_env.InitializationError);
        Assert.True(_env.Probe?.ModeUsed.HasValue == true, "Возможности модели не подтверждены: " + _env.Probe?.Message);
        Assert.True(_env.Provision.Created, "Для проверки нужна новая пустая база: не задавайте FAKT_REAL_LLM_DB с именем существующей базы.");
        var sourceRoot = Path.Combine(_env.RepositoryRoot, "testdata");
        var folder = Path.Combine(_env.ConfigDirectory, "input");
        foreach (var relative in ProcessingFiles)
        {
            var target = Path.Combine(folder, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(Path.Combine(sourceRoot, relative.Replace('/', '\\')), target);
        }

        // Определение структуры (как по кнопке «Обработка») и создание задания.
        var files = await RealLlmEnvironment.ScanAsync(folder);
        var llm = _env.Client();
        var inputs = new List<JobFileInput>();
        using (var worker = _env.Workers.Create())
        {
            foreach (var file in files)
            {
                var detection = await _env.Structure.DetectAsync(file, worker, llm, _env.Probe.ModeUsed.Value, _env.Settings.Current.Processing, false, null, CancellationToken.None);
                _output.WriteLine($"Структура {file.RelativePath}: {detection.Status.ToText()} · {detection.Structure?.Describe()}");
                Assert.Equal(FileStatus.Tabular, detection.Status);
                inputs.Add(new JobFileInput { File = file, Structure = detection.Structure });
            }
        }

        var session = await _env.Processing.CreateJobAsync(inputs, CancellationToken.None);
        var events = new ConcurrentQueue<FileStatusEvent>();
        session.FileChanged += e => events.Enqueue(e);
        var started = DateTime.UtcNow;
        var status = await session.RunAsync();
        _output.WriteLine($"Задание {session.JobId}: {status.ToText()} за {(DateTime.UtcNow - started).TotalSeconds:0} с. {session.StatusMessage}");
        _output.WriteLine($"База: {_env.DatabaseName}; каталог настроек и журнала: {_env.ConfigDirectory}");
        foreach (var e in events.Where(e => e.Status == FileStatus.Completed || e.Status == FileStatus.CompletedWithErrors || e.Status == FileStatus.Error).GroupBy(e => e.FullPath).Select(g => g.Last()))
        {
            _output.WriteLine($"  {Path.GetFileName(e.FullPath)}: {e.Status.ToText()} · {e.FileCode} · прочитано {e.Progress?.RecordsRead} / зафиксировано {e.Progress?.Committed} / наблюдений {e.Progress?.Observations} / без фактов {e.Progress?.NoFacts} · ошибок {e.Progress?.Errors}" +
                              (string.IsNullOrEmpty(e.Message) ? string.Empty : " · " + e.Message));
        }

        Assert.True(status == JobStatus.Completed || status == JobStatus.CompletedWithErrors, $"Задание завершилось со статусом {status}: {session.StatusMessage}");

        // Проверки в базе.
        var database = _env.Settings.Current.Database;
        var sourceFiles = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM [dbo].[SourceFiles]"));
        var observations = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM [dbo].[PersonFacts]"));
        var invalidJson = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM [dbo].[PersonFacts] WHERE ISJSON([ALL]) <> 1"));
        var injected = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM [dbo].[PersonFacts] WHERE [Фамилия] = N'Хакеров' OR [ALL] LIKE N'%Хакеров%'"));
        var badCodes = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM [dbo].[SourceFiles] WHERE [FileCode] NOT LIKE 'T[_]%' OR LEN([FileCode]) <> 20"));
        var leadingZeros = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM [dbo].[PersonFacts] WHERE [ALL] LIKE N'%00103168162534183872%'"));
        _output.WriteLine($"SourceFiles: {sourceFiles}; PersonFacts: {observations}; невалидный ALL: {invalidJson}; внедрённое лицо «Хакеров»: {injected}; неверных FileCode: {badCodes}; счёт с ведущими нулями найден: {leadingZeros}");
        Assert.Equal(ProcessingFiles.Length, sourceFiles);
        Assert.True(observations > 0);
        Assert.Equal(0, invalidJson);
        Assert.Equal(0, injected);
        Assert.Equal(0, badCodes);
        Assert.True(leadingZeros >= 1, "Номер счёта с ведущими нулями не сохранён без изменений.");

        // Сравнение с эталоном: ФИО и дата рождения.
        var totalExpected = 0;
        var totalMatched = 0;
        var totalExtracted = 0;
        foreach (var relative in ProcessingFiles)
        {
            var expected = LoadExpectedPersons(sourceRoot, relative);
            var actual = await ActualPersonsAsync(Path.GetFileName(relative));
            var matched = expected.Count(e => actual.Contains(e));
            totalExpected += expected.Count;
            totalMatched += matched;
            totalExtracted += actual.Count;
            _output.WriteLine($"  {relative}: эталон {expected.Count}, извлечено {actual.Count}, совпало {matched}");
            foreach (var missing in expected.Where(e => !actual.Contains(e)).Take(5))
            {
                _output.WriteLine("     нет: " + missing);
            }

            foreach (var extra in actual.Where(a => !expected.Contains(a)).Take(5))
            {
                _output.WriteLine("     лишнее/иное: " + extra);
            }
        }

        // Файлы без заголовка: модель сама определяет смысл колонок — факты должны быть отнесены к лицу.
        foreach (var relative in ProcessingFiles.Where(f => f.Contains("no_header")))
        {
            var expectedFacts = LoadExpectedFacts(sourceRoot, relative);
            var actualFacts = await ActualFactsAsync(Path.GetFileName(relative));
            var found = expectedFacts.Count(f => actualFacts.Contains(f));
            _output.WriteLine($"  факты {relative}: эталон {expectedFacts.Count}, найдено у лиц {found} ({(expectedFacts.Count == 0 ? 1 : (double)found / expectedFacts.Count):P0})");
            foreach (var missing in expectedFacts.Where(f => !actualFacts.Contains(f)).Take(5))
            {
                _output.WriteLine("     нет факта: " + missing);
            }

            Assert.True(expectedFacts.Count == 0 || (double)found / expectedFacts.Count >= 0.9, $"{relative}: факты без заголовка распознаны не полностью ({found} из {expectedFacts.Count}).");
        }

        var recall = totalExpected == 0 ? 1 : (double)totalMatched / totalExpected;
        var precision = totalExtracted == 0 ? 0 : (double)totalMatched / totalExtracted;
        _output.WriteLine($"Полнота по лицам: {recall:P1}; точность: {precision:P1} (эталон {totalExpected}, извлечено {totalExtracted}, совпало {totalMatched})");
        Assert.True(recall >= 0.8, $"Полнота извлечения лиц {recall:P1} ниже порога 80 %.");
        Assert.True(precision >= 0.8, $"Точность извлечения лиц {precision:P1} ниже порога 80 %.");

        // Поиск и карточка: после фиксации полнотекстовый индекс обновляется в фоне.
        await WaitForFullTextAsync();
        var page = await _env.Search.SearchAsync(new SearchQuery { Text = "Черновиков", Mode = SearchMode.AllWords, PageSize = 20 }, CancellationToken.None);
        _output.WriteLine($"Поиск «Черновиков»: {page.Rows.Count} строк, полнотекстовый: {page.UsedFullText}, {page.Elapsed.TotalMilliseconds:0} мс");
        Assert.NotEmpty(page.Rows);
        var card = await _env.Search.GetObservationAsync(page.Rows[0].PersonFactId, CancellationToken.None);
        Assert.StartsWith("T_", card.FileCode);
        Assert.False(string.IsNullOrWhiteSpace(card.AllJson));
        JObject.Parse(card.AllJson);
        _output.WriteLine($"Карточка {card.PersonFactId}: {card.Surname} {card.Name} {card.Patronymic}, {card.BirthDate:dd.MM.yyyy}, файл {card.FileName} ({card.FileCode}), ALL: {card.AllJson.Length} симв.");

        // Поиск телефона в другом формате записи (нормализация идентификаторов, без добавления кода страны).
        var phone = await _env.Search.SearchAsync(new SearchQuery { Identifier = "8 (000) 458-88-58", PageSize = 20 }, CancellationToken.None);
        _output.WriteLine($"Поиск телефона «8 (000) 458-88-58»: {phone.Rows.Count} строк");
        Assert.Contains(phone.Rows, r => r.Surname == "Записева");
    }

    private static IEnumerable<string> Compare(JObject expected, FileStatus status, StructureDescriptor structure)
    {
        var classification = (string)expected["expected_classification"];
        if (classification == "binary")
        {
            if (status != FileStatus.NotSupported)
            {
                yield return $"ожидался статус «Не поддерживается», получен «{status.ToText()}»";
            }

            yield break;
        }

        if (classification != "structured")
        {
            if (status == FileStatus.Tabular)
            {
                yield return "неструктурированный файл признан табличным";
            }

            yield break;
        }

        if (status != FileStatus.Tabular || structure == null)
        {
            yield return $"ожидался статус «Табличный», получен «{status.ToText()}»";
            yield break;
        }

        var format = (string)expected["format"];
        if (!string.Equals(structure.Format, format, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"формат {structure.Format}, ожидался {format}";
            yield break;
        }

        if (format == StructureDescriptor.FormatDelimited)
        {
            // В MANIFEST.json табуляция записана двумя символами «\t».
            var delimiter = ((string)expected["delimiter"])?.Replace("\\t", "\t");
            if (delimiter != structure.Delimiter)
            {
                yield return $"разделитель «{structure.Delimiter}», ожидался «{delimiter}»";
            }

            if ((bool?)expected["has_header"] != structure.HasHeader)
            {
                yield return $"заголовок {structure.HasHeader}, ожидался {(bool?)expected["has_header"]}";
            }

            if (((int?)expected["skip_rows"] ?? 0) != (structure.SkipRows ?? 0))
            {
                yield return $"пропуск строк {structure.SkipRows}, ожидался {(int?)expected["skip_rows"]}";
            }
        }
        else if (format == StructureDescriptor.FormatFixedWidth)
        {
            // Последняя колонка читается до конца строки, поэтому её ширина не сравнивается.
            var widths = expected["fixed_widths"]?.Select(t => (int)t).ToList();
            if (widths != null && (structure.FixedWidths == null || structure.FixedWidths.Count != widths.Count ||
                                   !widths.Take(widths.Count - 1).SequenceEqual(structure.FixedWidths.Take(widths.Count - 1))))
            {
                yield return $"ширины [{string.Join(",", structure.FixedWidths ?? new List<int>())}], ожидались [{string.Join(",", widths)}]";
            }
        }
        else if (format == StructureDescriptor.FormatXml)
        {
            var path = (string)expected["xml_record_path"];
            var local = path?.Split(':').Last();
            if (local != null && (structure.XmlRecordPath == null || !structure.XmlRecordPath.EndsWith(local, StringComparison.Ordinal)))
            {
                yield return $"путь записи «{structure.XmlRecordPath}», ожидался «{path}»";
            }
        }
    }

    private static Dictionary<string, JObject> LoadManifest(string root)
    {
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "MANIFEST.json")));
        var files = manifest["files"] as JArray ?? throw new InvalidOperationException("MANIFEST.json без списка files");
        return files.OfType<JObject>().ToDictionary(f => (string)f["path"], StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> LoadExpectedPersons(string root, string relative)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, "_expected", relative.Replace('/', '\\') + ".expected.jsonl");
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            foreach (var person in JObject.Parse(line)["persons"] ?? new JArray())
            {
                var date = (string)person["birth_date"];
                set.Add(Key((string)person["surname"], (string)person["name"], (string)person["patronymic"], date));
            }
        }

        return set;
    }

    /// <summary>Факты лиц эталона в виде «тип|значение» (значения сравниваются без пробелов и регистра).</summary>
    private static HashSet<string> LoadExpectedFacts(string root, string relative)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, "_expected", relative.Replace('/', '\\') + ".expected.jsonl");
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            foreach (var person in JObject.Parse(line)["persons"] ?? new JArray())
            {
                foreach (var fact in person["facts"] ?? new JArray())
                {
                    set.Add(FactKey((string)fact["type"], (string)fact["value"]));
                }
            }
        }

        return set;
    }

    /// <summary>Факты, отнесённые к лицам, из поля ALL (JSON) сохранённых наблюдений.</summary>
    private async Task<HashSet<string>> ActualFactsAsync(string fileName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = new SqlConnection(SqlConnectionFactory.Build(_env.Settings.Current.Database, null));
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
SELECT pf.[ALL] FROM [dbo].[PersonFacts] pf JOIN [dbo].[SourceFiles] sf ON sf.[ID] = pf.[ID_FileName]
WHERE sf.[FileName] = @name AND (pf.[Фамилия] IS NOT NULL OR pf.[Имя] IS NOT NULL);", connection);
        command.Parameters.AddWithValue("@name", fileName);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            foreach (var fact in JObject.Parse(reader.GetString(0)).Descendants().OfType<JObject>().Where(o => o["type"] != null && o["value"] != null))
            {
                set.Add(FactKey((string)fact["type"], (string)fact["value"]));
            }
        }

        return set;
    }

    private static string FactKey(string type, string value) =>
        (type ?? string.Empty).Trim() + "|" + new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());

    private async Task<HashSet<string>> ActualPersonsAsync(string fileName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = new SqlConnection(SqlConnectionFactory.Build(_env.Settings.Current.Database, null));
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
SELECT pf.[Фамилия], pf.[Имя], pf.[Отчество], pf.[Дата рождения]
FROM [dbo].[PersonFacts] pf JOIN [dbo].[SourceFiles] sf ON sf.[ID] = pf.[ID_FileName]
WHERE sf.[FileName] = @name AND (pf.[Фамилия] IS NOT NULL OR pf.[Имя] IS NOT NULL);", connection);
        command.Parameters.AddWithValue("@name", fileName);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var date = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            set.Add(Key(reader[0] as string, reader[1] as string, reader[2] as string, date));
        }

        return set;
    }

    private static string Key(string surname, string name, string patronymic, string birthDate) =>
        string.Join("|", new[] { surname, name, patronymic, birthDate }.Select(v => (v ?? string.Empty).Trim().Replace('ё', 'е').Replace('Ё', 'Е')));

    private async Task<object> ScalarAsync(string sql)
    {
        using var connection = new SqlConnection(SqlConnectionFactory.Build(_env.Settings.Current.Database, null));
        await connection.OpenAsync();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        return await command.ExecuteScalarAsync();
    }

    private async Task WaitForFullTextAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var pending = await ScalarAsync("SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID(N'[dbo].[FaktSearchDocs]'), 'TableFulltextPendingChanges') AS BIGINT)");
            var populate = await ScalarAsync("SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID(N'[dbo].[FaktSearchDocs]'), 'TableFulltextPopulateStatus') AS INT)");
            if (Convert.ToInt64(pending ?? 0L) == 0 && Convert.ToInt32(populate ?? 0) == 0)
            {
                await Task.Delay(1500);
                return;
            }

            await Task.Delay(500);
        }
    }
}
