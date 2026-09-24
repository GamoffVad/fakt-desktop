using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Extraction;
using Fakt.Application.Llm;
using Fakt.Application.Structure;
using Fakt.Core.Extraction;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Processing;
using Fakt.Core.Records;
using Fakt.Core.Structure;

namespace Fakt.Application.Processing;

public sealed class EstimateInput
{
    public ScannedFile File { get; set; }
    public StructureDetectionResult Detection { get; set; }
}

/// <summary>Измерение на небольшом образце: один реальный запрос извлечения (результат не сохраняется).</summary>
public sealed class SampleMeasurement
{
    public int Records { get; set; }
    public TimeSpan Latency { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
}

/// <summary>
/// Оценка большого запуска: число записей (по среднему размеру записи в образце), запросов, токенов и времени.
/// Деньги считаются только при тарифах, заданных пользователем с датой; иначе «Стоимость не рассчитана».
/// Оценка — не обещание: реальные токены и время зависят от содержимого и провайдера.
/// </summary>
public sealed class EstimationService
{
    /// <summary>Допущение о выходных токенах на запись, если нет измерения на образце.</summary>
    public const int AssumedOutputTokensPerRecord = 150;

    public ProcessingEstimate Estimate(IReadOnlyList<EstimateInput> inputs, LlmProfile profile, SampleMeasurement measurement)
    {
        var estimate = new ProcessingEstimate { Files = inputs.Count, TotalBytes = inputs.Sum(i => i.File.Size) };
        var overhead = TokenEstimator.EstimateRaw(Prompts.FactsSystem) + TokenEstimator.EstimateRaw(JsonSchemas.Extraction().ToString(Newtonsoft.Json.Formatting.None)) + 400;
        long records = 0;
        long inputTokens = 0;
        long requests = 0;
        var unknownFiles = 0;
        foreach (var input in inputs)
        {
            var (fileRecords, tokensPerRecord) = EstimateFile(input);
            if (!fileRecords.HasValue)
            {
                unknownFiles++;
                continue;
            }

            var rowsPerRequest = Math.Max(1, Math.Min(profile.BatchRows, (profile.MaxInputTokensPerRequest - overhead) / Math.Max(1, tokensPerRecord)));
            var fileRequests = (long)Math.Ceiling(fileRecords.Value / (double)rowsPerRequest);
            records += fileRecords.Value;
            requests += fileRequests;
            inputTokens += fileRequests * overhead + fileRecords.Value * tokensPerRecord;
        }

        estimate.EstimatedRecords = unknownFiles == inputs.Count ? (long?)null : records;
        estimate.EstimatedRequests = requests;
        estimate.EstimatedInputTokens = inputTokens;
        var outputPerRecord = measurement?.OutputTokens != null && measurement.Records > 0
            ? (double)measurement.OutputTokens.Value / measurement.Records
            : AssumedOutputTokensPerRecord;
        estimate.EstimatedOutputTokens = (long)(records * outputPerRecord);
        if (measurement != null)
        {
            estimate.MeasuredOnSample = true;
            estimate.SampleNote = $"Измерено на образце из {measurement.Records} записей: {measurement.Latency.TotalSeconds:0.0} с на запрос" +
                                  (measurement.OutputTokens.HasValue ? $", {outputPerRecord:0} выходных токенов на запись" : ", провайдер не сообщил расход токенов");
            var parallel = Math.Max(1, profile.MaxConcurrentRequests);
            estimate.EstimatedDuration = TimeSpan.FromSeconds(requests * measurement.Latency.TotalSeconds / parallel);
        }
        else
        {
            estimate.Assumptions.Add($"Выходные токены — допущение {AssumedOutputTokensPerRecord} на запись; время не оценено без измерения на образце.");
        }

        if (unknownFiles > 0)
        {
            estimate.Warnings.Add($"Для файлов без построчной структуры (XML, JSON-массив) число записей по образцу не оценивается: {unknownFiles} файл(ов) не учтены в оценке.");
        }

        estimate.Assumptions.Add("Число записей оценено по среднему размеру записи в образце и размеру файла.");
        estimate.Assumptions.Add("Токены оценены приблизительно (без токенизатора провайдера) и уточняются по фактическому расходу во время обработки.");
        estimate.Assumptions.Add($"Предел задания: {(profile.RequestsPerMinute > 0 ? profile.RequestsPerMinute + " запросов/мин" : "без ограничения запросов в минуту")}, параллельно {profile.MaxConcurrentRequests}.");

        var pricing = profile.Pricing;
        if (pricing != null && pricing.IsComplete)
        {
            var cost = (estimate.EstimatedInputTokens * pricing.InputPerMillionTokens.Value + estimate.EstimatedOutputTokens * pricing.OutputPerMillionTokens.Value) / 1_000_000m;
            estimate.EstimatedCost = cost;
            estimate.CostText = $"≈ {cost:0.##} {pricing.Currency} по тарифам на {pricing.AsOfDate:dd.MM.yyyy}" +
                                (string.IsNullOrWhiteSpace(pricing.Source) ? string.Empty : $" ({pricing.Source})") + "; фактическая стоимость зависит от провайдера.";
        }
        else
        {
            estimate.CostText = "Стоимость не рассчитана: тарифы с датой проверки не заданы в профиле LLM.";
        }

        return estimate;
    }

    private static (long? Records, int TokensPerRecord) EstimateFile(EstimateInput input)
    {
        var detection = input.Detection;
        var structure = detection?.Structure;
        var previewRows = detection?.Validation?.Preview?.Rows ?? new List<Fakt.Core.Worker.PreviewRow>();
        var columns = detection?.Validation?.Preview?.Columns ?? new List<string>();
        var tokens = 60;
        if (previewRows.Count > 0)
        {
            var sampleRecords = previewRows.Take(50).Select(r => new SourceRecord(r.Ordinal, r.Line, r.Line,
                (r.Values ?? new List<string>()).Select((v, i) => new KeyValuePair<string, string>(i < columns.Count ? columns[i] : "c" + i, v)).ToList(), null, null, null)).ToList();
            tokens = (int)Math.Ceiling(sampleRecords.Average(r => TokenEstimator.EstimateRaw(Prompts.RecordJson(r))));
        }

        if (structure == null)
        {
            return (null, tokens);
        }

        if (detection.Validation?.EofReached == true)
        {
            return (detection.Validation.RecordsChecked, tokens);
        }

        var lineBased = structure.Format == StructureDescriptor.FormatDelimited || structure.Format == StructureDescriptor.FormatFixedWidth || structure.Format == StructureDescriptor.FormatJsonl;
        var lines = detection.Sample?.Lines ?? new List<string>();
        var dataLines = lines.Skip((structure.SkipRows ?? 0) + (structure.HasHeader == true ? 1 : 0)).Where(l => l.Length > 0).ToList();
        if (!lineBased || dataLines.Count == 0)
        {
            return (null, tokens);
        }

        var encoding = EncodingFor(detection.Sample?.Encoding);
        var averageBytes = dataLines.Average(l => encoding.GetByteCount(l) + 2);
        return ((long)Math.Max(1, input.File.Size / averageBytes), tokens);
    }

    private static Encoding EncodingFor(string name)
    {
        switch (name)
        {
            case "utf-16-le":
            case "utf-16-be":
                return Encoding.Unicode;
            case "windows-1251":
            case "cp866":
            case "koi8-r":
            case "windows-1252":
            case "iso-8859-1":
            case "ascii":
                return Encoding.GetEncoding(1251);
            default:
                return Encoding.UTF8;
        }
    }

    /// <summary>Один реальный запрос на первых записях предпросмотра — для измерения задержки и токенов.</summary>
    public async Task<SampleMeasurement> MeasureAsync(EstimateInput input, ResilientLlmClient llm, StructuredOutputMode mode, int records, CancellationToken cancellationToken)
    {
        var preview = input.Detection?.Validation?.Preview;
        if (preview == null || preview.Rows.Count == 0)
        {
            return null;
        }

        var sample = preview.Rows.Where(r => r.Values != null).Take(Math.Max(1, records)).Select(r => new SourceRecord(r.Ordinal, r.Line, r.Line,
            r.Values.Select((v, i) => new KeyValuePair<string, string>(i < preview.Columns.Count ? preview.Columns[i] : "c" + i, v)).ToList(), null, null, null)).ToList();
        var extractor = new BatchExtractor(llm, new ExtractionContext
            {
                ProviderId = llm.Config.Profile.ProviderId,
                ModelId = llm.Config.Profile.ModelId,
                ExtractionVersion = "estimate",
                InferredColumnNames = input.Detection?.Structure?.HasInferredColumnNames == true,
            },
            new TokenEstimator(), new AdaptiveBatchSize(sample.Count), null) { Mode = mode };
        var before = (llm.Budget.InputTokens, llm.Budget.OutputTokens);
        var stopwatch = Stopwatch.StartNew();
        await extractor.ExtractAsync(sample, cancellationToken).ConfigureAwait(false);
        var usedInput = (int)(llm.Budget.InputTokens - before.InputTokens);
        var usedOutput = (int)(llm.Budget.OutputTokens - before.OutputTokens);
        return new SampleMeasurement
        {
            Records = sample.Count,
            Latency = stopwatch.Elapsed,
            InputTokens = usedInput > 0 ? usedInput : (int?)null,
            OutputTokens = usedOutput > 0 ? usedOutput : (int?)null,
        };
    }
}
