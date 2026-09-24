using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Fakt.LoadTest;

/// <summary>Параметры командной строки нагрузочного теста.</summary>
public sealed class LoadTestOptions
{
    public const string Usage = @"Нагрузочный тест FAKT: синтетический файл → Python worker → имитатор LLM → проверка → SQL Server.

Использование:
  Fakt.LoadTest.exe [параметры]

Данные:
  --records N               число записей (по умолчанию 1000000; для пробного прогона, например, 50000)
  --format csv|fixed|jsonl  формат файла (по умолчанию csv)
  --data-dir DIR            каталог файлов (по умолчанию <репозиторий>\testdata\large, не хранится в Git)
  --regenerate              пересоздать файл, даже если он уже есть
  --generator-python PATH   python для генератора tools\testdata\generate_testdata.py (по умолчанию — python worker)

Окружение:
  --server NAME             SQL Server (по умолчанию FAKT_TEST_SQL_SERVER или localhost; Windows Authentication)
  --python PATH             python.exe worker (по умолчанию FAKT_PYTHON или собранный worker)
  --keep-db                 не удалять временную базу FaktLoad_… после прогона
  --skip-v008               не применять необязательную миграцию V008 (индексы фильтров поиска)

Конвейер (значения по умолчанию = настройки приложения по умолчанию):
  --chunk-size N            записей в chunk Pandas (5000)
  --batch-rows N            записей в запросе LLM (10)
  --concurrency N           одновременных запросов LLM (2)
  --queue N                 предел очереди пакетов (8)
  --sql-batch N             предел строк в SQL-транзакции (500)
  --max-input-tokens N      предел входных токенов запроса (6000)

Имитатор LLM (не провайдер):
  --latency-ms N            задержка ответа (0)
  --per-record-latency-ms N добавка на запись пакета (0)
  --jitter-ms N             детерминированная добавка 0..N (0)
  --fault-429 R --fault-503 R --fault-timeout R --fault-invalid-json R
  --fault-missing-id R --fault-duplicate-id R --fault-unknown-id R   доли запросов с ошибкой (0..1, по умолчанию 0)

Этапы и отчёт:
  --skip-parse-only         не измерять чтение без конвейера
  --skip-search             не ждать полнотекстовый индекс и не измерять поиск
  --fts-timeout-min N       предел ожидания полнотекстового индекса, минут (60)
  --sample-ms N             период опроса памяти (250)
  --out DIR                 каталог отчётов (по умолчанию <репозиторий>\docs\test-results)
  --label TEXT              суффикс имени отчёта (по умолчанию <число записей>-<формат>)
  --help";

    public int Records { get; set; } = 1_000_000;
    public string Format { get; set; } = "csv";
    public string DataDirectory { get; set; }
    public bool Regenerate { get; set; }
    public string GeneratorPython { get; set; }
    public string Server { get; set; } = Environment.GetEnvironmentVariable("FAKT_TEST_SQL_SERVER") ?? "localhost";
    public string Python { get; set; }
    public bool KeepDatabase { get; set; }
    public bool SkipV008 { get; set; }
    public int ChunkSize { get; set; } = 5000;
    public int BatchRows { get; set; } = 10;
    public int Concurrency { get; set; } = 2;
    public int QueueCapacity { get; set; } = 8;
    public int SqlBatchSize { get; set; } = 500;
    public int MaxInputTokens { get; set; } = 6000;
    public int LatencyMs { get; set; }
    public int PerRecordLatencyMs { get; set; }
    public int JitterMs { get; set; }
    public double Fault429 { get; set; }
    public double Fault503 { get; set; }
    public double FaultTimeout { get; set; }
    public double FaultInvalidJson { get; set; }
    public double FaultMissingId { get; set; }
    public double FaultDuplicateId { get; set; }
    public double FaultUnknownId { get; set; }
    public bool SkipParseOnly { get; set; }
    public bool SkipSearch { get; set; }
    public int FullTextTimeoutMinutes { get; set; } = 60;
    public int SampleMs { get; set; } = 250;
    public string OutputDirectory { get; set; }
    public string Label { get; set; }
    public bool Help { get; set; }
    public string CommandLine { get; set; }

    public string EffectiveLabel => string.IsNullOrWhiteSpace(Label)
        ? (Records % 1000 == 0 ? (Records / 1000).ToString(CultureInfo.InvariantCulture) + "k" : Records.ToString(CultureInfo.InvariantCulture)) + "-" + Format
        : Label.Trim();

    public static LoadTestOptions Parse(string[] args)
    {
        var options = new LoadTestOptions { CommandLine = "Fakt.LoadTest.exe " + string.Join(" ", args.Select(Quote)) };
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string Value()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Параметр {name} требует значения.");
                }

                return args[++i];
            }

            int Int() => int.Parse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            double Rate()
            {
                var value = double.Parse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture);
                if (value < 0 || value > 1)
                {
                    throw new ArgumentException($"{name}: доля должна быть от 0 до 1.");
                }

                return value;
            }

            switch (name)
            {
                case "--records": options.Records = Int(); break;
                case "--format": options.Format = Value().ToLowerInvariant(); break;
                case "--data-dir": options.DataDirectory = Value(); break;
                case "--regenerate": options.Regenerate = true; break;
                case "--generator-python": options.GeneratorPython = Value(); break;
                case "--server": options.Server = Value(); break;
                case "--python": options.Python = Value(); break;
                case "--keep-db": options.KeepDatabase = true; break;
                case "--skip-v008": options.SkipV008 = true; break;
                case "--chunk-size": options.ChunkSize = Int(); break;
                case "--batch-rows": options.BatchRows = Int(); break;
                case "--concurrency": options.Concurrency = Int(); break;
                case "--queue": options.QueueCapacity = Int(); break;
                case "--sql-batch": options.SqlBatchSize = Int(); break;
                case "--max-input-tokens": options.MaxInputTokens = Int(); break;
                case "--latency-ms": options.LatencyMs = Int(); break;
                case "--per-record-latency-ms": options.PerRecordLatencyMs = Int(); break;
                case "--jitter-ms": options.JitterMs = Int(); break;
                case "--fault-429": options.Fault429 = Rate(); break;
                case "--fault-503": options.Fault503 = Rate(); break;
                case "--fault-timeout": options.FaultTimeout = Rate(); break;
                case "--fault-invalid-json": options.FaultInvalidJson = Rate(); break;
                case "--fault-missing-id": options.FaultMissingId = Rate(); break;
                case "--fault-duplicate-id": options.FaultDuplicateId = Rate(); break;
                case "--fault-unknown-id": options.FaultUnknownId = Rate(); break;
                case "--skip-parse-only": options.SkipParseOnly = true; break;
                case "--skip-search": options.SkipSearch = true; break;
                case "--fts-timeout-min": options.FullTextTimeoutMinutes = Int(); break;
                case "--sample-ms": options.SampleMs = Int(); break;
                case "--out": options.OutputDirectory = Value(); break;
                case "--label": options.Label = Value(); break;
                case "--help":
                case "-h":
                case "/?":
                    options.Help = true;
                    break;
                default:
                    throw new ArgumentException("Неизвестный параметр: " + name);
            }
        }

        if (options.Records < 1)
        {
            throw new ArgumentException("--records: число записей должно быть положительным.");
        }

        if (!new[] { "csv", "fixed", "jsonl" }.Contains(options.Format))
        {
            throw new ArgumentException("--format: допустимо csv, fixed или jsonl.");
        }

        if (options.ChunkSize < 1 || options.ChunkSize > 100000 || options.BatchRows < 1 || options.BatchRows > 200 || options.Concurrency < 1 || options.Concurrency > 64 ||
            options.QueueCapacity < 1 || options.QueueCapacity > 256 || options.SqlBatchSize < 1 || options.SqlBatchSize > 10000)
        {
            throw new ArgumentException("Параметры конвейера вне допустимых диапазонов приложения (chunk 1..100000, batch 1..200, concurrency 1..64, queue 1..256, sql-batch 1..10000).");
        }

        return options;
    }

    private static string Quote(string arg) => arg.IndexOf(' ') >= 0 ? "\"" + arg + "\"" : arg;

    public static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (var i = 0; i < 10 && current != null; i++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Fakt.sln")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (Fakt.sln) выше каталога " + AppDomain.CurrentDomain.BaseDirectory);
    }

    public IEnumerable<string> Describe()
    {
        yield return $"записей: {Records:N0}, формат: {Format}";
        yield return $"chunk {ChunkSize}, пакет LLM {BatchRows} записей, параллельно {Concurrency}, очередь {QueueCapacity}, SQL-пакет {SqlBatchSize}";
    }
}
