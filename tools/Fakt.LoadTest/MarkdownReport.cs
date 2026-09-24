using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Fakt.LoadTest;

/// <summary>Краткий отчёт прогона в Markdown (полные данные — в JSON рядом).</summary>
public static class MarkdownReport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string N(double value, int decimals = 0) => value.ToString("N" + decimals, Ru);

    private static string N(long value) => value.ToString("N0", Ru);

    public static string Render(LoadTestReport r, string jsonName)
    {
        var b = new StringBuilder();
        b.AppendLine($"# Нагрузочный тест FAKT — {r.Settings.Records.ToString("N0", Ru)} записей ({r.Settings.Format}), {r.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        b.AppendLine();
        b.AppendLine($"Итог: **{(r.Success ? "успешно, все проверки пройдены" : "НЕ успешно")}**. Полные данные (включая временной ряд памяти): `{jsonName}`.");
        b.AppendLine();
        foreach (var note in r.Notes)
        {
            b.AppendLine("- " + note);
        }

        if (!string.IsNullOrEmpty(r.Error))
        {
            b.AppendLine();
            b.AppendLine("**Ошибка прогона:**");
            b.AppendLine();
            b.AppendLine("```");
            b.AppendLine(r.Error.Length > 3000 ? r.Error.Substring(0, 3000) + "…" : r.Error);
            b.AppendLine("```");
        }

        b.AppendLine();
        b.AppendLine("## Окружение и параметры");
        b.AppendLine();
        var e = r.Environment;
        b.AppendLine("| Параметр | Значение |");
        b.AppendLine("|---|---|");
        b.AppendLine($"| ОС | {e.Os} |");
        b.AppendLine($"| Процессор | {e.Cpu}, логических процессоров {e.LogicalProcessors} |");
        b.AppendLine($"| Память | {N(e.TotalRamGb, 1)} ГБ |");
        b.AppendLine($"| .NET | {e.DotNetRuntime}, {(e.Is64BitProcess ? "x64" : "x86")}, GC {e.GcMode} |");
        b.AppendLine($"| Python worker | Python {e.PythonVersion}, pandas {e.PandasVersion}, numpy {e.NumpyVersion}, worker {e.WorkerVersion} |");
        b.AppendLine($"| SQL Server | {e.SqlServerVersion} {e.SqlServerEdition}, транспорт {e.SqlTransport}, модель восстановления {e.DatabaseRecoveryModel} (временная база, удалена после прогона) |");
        b.AppendLine($"| Миграции | {string.Join(", ", r.Settings.MigrationsApplied)} |");
        b.AppendLine($"| Файл | `{r.Data.Path}`, {N(r.Data.SizeMb, 1)} МБ{(r.Data.Generated ? $", создан генератором за {N(r.Data.GenerationSeconds, 1)} с" : ", существующий")} |");
        b.AppendLine($"| Конвейер | chunk {r.Settings.ChunkSize}, пакет LLM {r.Settings.BatchRows} записей, параллельно {r.Settings.Concurrency}, очередь {r.Settings.QueueCapacity}, SQL-пакет ≤ {r.Settings.SqlBatchSize}, предел токенов {r.Settings.MaxInputTokens} |");
        b.AppendLine($"| Имитатор LLM | {r.Settings.Simulator} |");
        b.AppendLine();
        b.AppendLine("Команда:");
        b.AppendLine();
        b.AppendLine("```");
        b.AppendLine(r.CommandLine);
        b.AppendLine("```");

        b.AppendLine();
        b.AppendLine("## Результаты");
        b.AppendLine();
        b.AppendLine("| Показатель | Значение |");
        b.AppendLine("|---|---|");
        if (r.ParseOnly != null)
        {
            var p = r.ParseOnly;
            b.AppendLine($"| Только чтение (worker, open_reader/read_chunk + разбор ответа в .NET) | {N(p.Records)} записей за {N(p.Seconds, 1)} с = **{N(p.RecordsPerSecond)} зап/с** ({N(p.MegabytesPerSecond, 1)} МБ/с), chunk: p50 {N(p.ReadChunkP50Ms)} мс, p95 {N(p.ReadChunkP95Ms)} мс |");
            b.AppendLine($"| Память при чтении | Python: пик {N(p.Memory.PythonOsPeakPrivateMb)} МБ private / {N(p.Memory.PythonOsPeakWorkingSetMb)} МБ WS; .NET: пик {N(p.Memory.DotNetPeakPrivateMb)} МБ private / {N(p.Memory.DotNetPeakWorkingSetMb)} МБ WS |");
        }

        if (r.Pipeline != null)
        {
            var p = r.Pipeline;
            b.AppendLine($"| Полный конвейер | {p.JobStatus} за {N(p.TotalSeconds, 1)} с (из них хеш SHA-256 и регистрация источника {N(p.HashAndRegisterSeconds, 1)} с) |");
            b.AppendLine($"| Пропускная способность конвейера | **{N(p.RecordsPerSecondProcessing)} зап/с** обработки ({N(p.RecordsPerSecondTotal)} зап/с с учётом хеша) |");
            b.AppendLine($"| Запросы к имитатору LLM | {N(p.LlmRequests)} (записей в запросах {N(p.RecordsSentToLlm)}){(p.SimulatedFaults?.Count > 0 ? "; внедрённые ошибки: " + string.Join(", ", p.SimulatedFaults.Select(f => f.Key + " " + f.Value)) : string.Empty)} |");
            b.AppendLine($"| SQL-фиксации | {N(p.Commits)} (ошибок {p.FailedCommits}, повторов-дубликатов {p.DuplicateCommits}), в среднем {N(p.AverageRowsPerCommit, 1)} строк (макс. {p.MaxRowsPerCommit}); длительность p50 {N(p.CommitP50Ms)} мс, p95 {N(p.CommitP95Ms)} мс, макс. {N(p.CommitMaxMs)} мс; занятость цикла фиксации {N(p.CommitBusyShare * 100, 0)} % |");
            b.AppendLine($"| Чтение worker внутри конвейера | {N(p.WorkerChunks)} chunk, {N(p.WorkerReadTotalSeconds, 1)} с ({N(p.WorkerBusyShare * 100, 0)} % времени), p50 {N(p.ReadChunkP50Ms)} мс |");
            b.AppendLine($"| Записей «в пути» (прочитано, не зафиксировано) | максимум {N(p.Memory.MaxInFlightRecords)}, в среднем {N(p.Memory.AverageInFlightRecords)} |");
            b.AppendLine($"| Память .NET (конвейер) | пик {N(p.Memory.DotNetPeakPrivateMb)} МБ private / {N(p.Memory.DotNetPeakWorkingSetMb)} МБ WS; управляемая куча: пик {N(p.Memory.ManagedHeapPeakMb)} МБ, после полной сборки {N(p.ManagedHeapAfterFullGcMb)} МБ |");
            b.AppendLine($"| Память Python (конвейер) | пик {N(p.Memory.PythonOsPeakPrivateMb)} МБ private / {N(p.Memory.PythonOsPeakWorkingSetMb)} МБ WS (пиковые счётчики ОС) |");
        }

        if (r.Sql?.Stored != null)
        {
            var s = r.Sql.Stored;
            b.AppendLine($"| Записано в SQL | PersonFacts {N(s.PersonFacts)}, FaktRowOutcomes {N(s.RowOutcomes)}, FaktSearchDocs {N(s.SearchDocs)}, FaktFactValues {N(s.FactValues)}, FaktRowErrors {N(s.RowErrors)}, FaktCommitLog {N(r.Sql.CommitLogRows)} |");
            b.AppendLine($"| Размер базы | данные {N(r.Sql.DatabaseSizeMb)} МБ (занято {N(r.Sql.DataUsedMb)} МБ), журнал {N(r.Sql.LogSizeMb)} МБ |");
            if (r.Sql.Tables.Count > 0)
            {
                b.AppendLine("| Место по таблицам (с индексами) | " + string.Join("; ", r.Sql.Tables.Where(t => t.ReservedMb >= 1).Select(t => $"{t.Table} {N(t.ReservedMb)} МБ")) + " |");
            }
        }

        if (r.FullText != null)
        {
            b.AppendLine($"| Полнотекстовый индекс | готов через {N(r.FullText.WaitSecondsAfterPipeline)} с после окончания конвейера (в очереди на тот момент {N(r.FullText.PendingAtPipelineEnd)}), документов {N(r.FullText.IndexedItems)}{(r.FullText.TimedOut ? " — НЕ дождались" : string.Empty)} |");
        }

        if (r.Pipeline?.ProgressByShare.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("### Память по ходу обработки");
            b.AppendLine();
            b.AppendLine("| Доля файла | Время, с | Зафиксировано | .NET private, МБ | Управляемая куча, МБ | Python private, МБ | В пути, записей |");
            b.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var point in r.Pipeline.ProgressByShare)
            {
                b.AppendLine($"| {point.Share} | {N(point.Seconds)} | {N(point.RecordsCommitted)} | {N(point.DotNetPrivateMb)} | {N(point.ManagedHeapMb)} | {N(point.PythonPrivateMb)} | {N(point.InFlightRecords)} |");
            }
        }

        if (r.Search.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("### Поиск после загрузки (SqlSearchRepository, 3 запуска подряд)");
            b.AppendLine();
            b.AppendLine("| Запрос | Первый, мс | Повтор (медиана), мс | Строк на странице | Примечание |");
            b.AppendLine("|---|---|---|---|---|");
            foreach (var s in r.Search)
            {
                var note = s.Error ?? (s.TotalCount.HasValue ? "всего " + N(s.TotalCount.Value) : s.HasMore ? "есть следующая страница" : string.Empty);
                b.AppendLine($"| {s.Description} | {N(s.FirstMs)} | {N(s.WarmMedianMs)} | {s.Rows} | {note} |");
            }
        }

        b.AppendLine();
        b.AppendLine("## Проверки");
        b.AppendLine();
        foreach (var check in r.Verification)
        {
            b.AppendLine($"- [{(check.Passed ? "x" : " ")}] {check.Name} — {check.Details}");
        }

        if (r.Pipeline?.RecentWarnings?.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("Последние предупреждения журнала конвейера:");
            b.AppendLine();
            foreach (var warning in r.Pipeline.RecentWarnings)
            {
                b.AppendLine("- " + warning.Replace("\r", " ").Replace("\n", " "));
            }
        }

        return b.ToString();
    }
}
