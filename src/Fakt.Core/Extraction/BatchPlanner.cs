using System;
using System.Collections.Generic;
using Fakt.Core.Records;

namespace Fakt.Core.Extraction;

/// <summary>
/// Консервативная оценка токенов без токенизатора провайдера: латиница ≈ 3,6 символа на токен,
/// кириллица и прочее ≈ 1,8. Оценка уточняется по фактическому usage ответов (коэффициент калибровки).
/// </summary>
public sealed class TokenEstimator
{
    private readonly object _gate = new();
    private double _calibration = 1.0;
    private int _samples;

    public double Calibration
    {
        get
        {
            lock (_gate)
            {
                return _calibration;
            }
        }
    }

    public static int EstimateRaw(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ascii = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (ch < 128)
            {
                ascii++;
            }
            else
            {
                other++;
            }
        }

        return (int)Math.Ceiling(ascii / 3.6 + other / 1.8) + 1;
    }

    public int Estimate(string text) => (int)Math.Ceiling(EstimateRaw(text) * Calibration);

    /// <summary>Учесть фактическое число входных токенов из ответа провайдера (скользящее среднее, 0,5..3).</summary>
    public void Observe(int estimatedRaw, int actual)
    {
        if (estimatedRaw <= 0 || actual <= 0)
        {
            return;
        }

        var ratio = Math.Max(0.5, Math.Min(3.0, actual / (double)estimatedRaw));
        lock (_gate)
        {
            _samples++;
            var weight = Math.Max(0.05, 1.0 / _samples);
            _calibration = _calibration * (1 - weight) + ratio * weight;
        }
    }
}

public sealed class PlannedBatch
{
    public PlannedBatch(IReadOnlyList<SourceRecord> records, int estimatedInputTokens)
    {
        Records = records;
        EstimatedInputTokens = estimatedInputTokens;
    }

    public IReadOnlyList<SourceRecord> Records { get; }

    public int EstimatedInputTokens { get; }
}

/// <summary>
/// Разбиение записей на пакеты LLM: ограничение и по числу записей, и по оценке токенов.
/// Запись, которая сама превышает предел, не обрезается: она возвращается как отдельная ошибка с объяснением.
/// </summary>
public sealed class BatchPlanner
{
    private readonly TokenEstimator _estimator;
    private readonly int _overheadTokens;

    public BatchPlanner(TokenEstimator estimator, int overheadTokens)
    {
        _estimator = estimator;
        _overheadTokens = overheadTokens;
    }

    public int OverheadTokens => _overheadTokens;

    public int EstimateRecord(SourceRecord record) => _estimator.Estimate(Prompts.RecordJson(record)) + 2;

    public void Plan(IReadOnlyList<SourceRecord> records, int maxRows, int maxInputTokens,
        ICollection<PlannedBatch> batches, ICollection<RowOutcome> oversized)
    {
        maxRows = Math.Max(1, maxRows);
        var current = new List<SourceRecord>();
        var currentTokens = _overheadTokens;
        foreach (var record in records)
        {
            var tokens = EstimateRecord(record);
            if (_overheadTokens + tokens > maxInputTokens)
            {
                oversized.Add(RowOutcome.Error(record.Ordinal, record.Line, record.Hash, "record_too_large",
                    $"Запись оценивается в ~{tokens} токенов и вместе с инструкциями превышает предел {maxInputTokens} токенов запроса. " +
                    "Запись не обрезается; увеличьте предел входных токенов профиля (если модель допускает) или обработайте запись отдельно."));
                continue;
            }

            if (current.Count > 0 && (current.Count >= maxRows || currentTokens + tokens > maxInputTokens))
            {
                batches.Add(new PlannedBatch(current, currentTokens));
                current = new List<SourceRecord>();
                currentTokens = _overheadTokens;
            }

            current.Add(record);
            currentTokens += tokens;
        }

        if (current.Count > 0)
        {
            batches.Add(new PlannedBatch(current, currentTokens));
        }
    }
}
