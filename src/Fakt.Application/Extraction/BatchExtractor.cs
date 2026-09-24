using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Llm;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Records;

namespace Fakt.Application.Extraction;

/// <summary>Общий размер пакета задания: уменьшается при усечении ответа или переполнении контекста.</summary>
public sealed class AdaptiveBatchSize
{
    private int _rows;

    public AdaptiveBatchSize(int initialRows)
    {
        _rows = Math.Max(1, initialRows);
    }

    public int Rows => Volatile.Read(ref _rows);

    public void Shrink(int failedBatchSize)
    {
        var target = Math.Max(1, failedBatchSize / 2);
        int current;
        do
        {
            current = Volatile.Read(ref _rows);
            if (current <= target)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _rows, target, current) != current);
    }
}

/// <summary>
/// Извлечение фактов из пакета записей: каждая запись получает собственный проверенный результат.
/// Усечённый ответ или переполнение контекста — пакет делится пополам и повторяется (усечённый JSON
/// не считается успешным). Пропущенные и повторённые source_row_id запрашиваются повторно отдельно,
/// затем переводятся в ошибку записи. Отказ модели по одной записи не блокирует остальные.
/// </summary>
public sealed class BatchExtractor
{
    private const int MaxRetryRounds = 2;
    private readonly ResilientLlmClient _llm;
    private readonly ExtractionValidator _validator = new();
    private readonly ExtractionContext _context;
    private readonly TokenEstimator _estimator;
    private readonly AdaptiveBatchSize _batchSize;
    private readonly IAppLogger _logger;

    public BatchExtractor(ResilientLlmClient llm, ExtractionContext context, TokenEstimator estimator, AdaptiveBatchSize batchSize, IAppLogger logger)
    {
        _llm = llm;
        _context = context;
        _estimator = estimator;
        _batchSize = batchSize;
        _logger = logger ?? NullLogger.Instance;
    }

    public StructuredOutputMode Mode { get; set; } = StructuredOutputMode.JsonSchema;

    public async Task<List<RowOutcome>> ExtractAsync(IReadOnlyList<SourceRecord> records, CancellationToken cancellationToken)
    {
        var results = new Dictionary<long, RowOutcome>();
        await ExtractIntoAsync(records, results, 0, cancellationToken).ConfigureAwait(false);
        return records.Select(r => results.TryGetValue(r.Ordinal, out var outcome)
                ? outcome
                : RowOutcome.Error(r.Ordinal, r.Line, r.Hash, "missing_result", "Модель не вернула результат для записи после повторных запросов."))
            .ToList();
    }

    private async Task ExtractIntoAsync(IReadOnlyList<SourceRecord> records, Dictionary<long, RowOutcome> results, int round, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        var request = new LlmJsonRequest
        {
            SystemPrompt = Prompts.FactsSystem,
            UserContent = Prompts.FactsUser(records),
            SchemaName = JsonSchemas.ExtractionSchemaName,
            Schema = JsonSchemas.Extraction(),
            MaxOutputTokens = _llm.Config.Profile.MaxOutputTokens,
            Temperature = _llm.Config.Profile.Temperature,
            Mode = Mode,
            Purpose = "extraction",
        };
        var estimatedRaw = TokenEstimator.EstimateRaw(request.SystemPrompt) + TokenEstimator.EstimateRaw(request.UserContent) + 400;

        LlmResponse response;
        try
        {
            response = await _llm.CompleteAsync(request, (int)Math.Ceiling(estimatedRaw * _estimator.Calibration), cancellationToken).ConfigureAwait(false);
        }
        catch (LlmException ex) when (ex.Kind == LlmErrorKind.ContextLengthExceeded)
        {
            await SplitOrFailAsync(records, results, round, "record_too_large",
                "Запись не помещается в контекст модели вместе с инструкциями; запись не обрезается. Выберите модель с большим контекстом.", cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (LlmException ex) when (ex.Kind == LlmErrorKind.ContentFiltered)
        {
            await SplitOrFailAsync(records, results, round, "content_filtered", "Ответ модели заблокирован фильтром содержимого провайдера.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (response.InputTokens.HasValue)
        {
            _estimator.Observe(estimatedRaw, response.InputTokens.Value);
        }

        if (response.IsTruncated)
        {
            _batchSize.Shrink(records.Count);
            await SplitOrFailAsync(records, results, round, "output_truncated",
                "Ответ модели на эту запись не помещается в предел выходных токенов профиля; увеличьте «Максимальный объём ответа».", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (response.FinishReason == LlmFinishReason.Refusal || response.FinishReason == LlmFinishReason.ContentFilter)
        {
            await SplitOrFailAsync(records, results, round, response.FinishReason == LlmFinishReason.Refusal ? "model_refused" : "content_filtered",
                "Модель отказалась обрабатывать запись.", cancellationToken).ConfigureAwait(false);
            return;
        }

        BatchValidationResult validation;
        try
        {
            validation = _validator.Validate(response.Text, records, _context);
        }
        catch (ExtractionFormatException ex)
        {
            _logger.Warn("extraction.invalid_response", ex.Message, e =>
            {
                e.Stage = "validation";
                e.ProviderRequestId = response.RequestId;
                e.Category = ErrorCategory.ModelResponse;
            });
            if (round < MaxRetryRounds)
            {
                await SplitOrRetryAsync(records, results, round + 1, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var record in records)
                {
                    results[record.Ordinal] = RowOutcome.Error(record.Ordinal, record.Line, record.Hash, "invalid_response", "Модель вернула некорректный JSON: " + Shorten(ex.Message));
                }
            }

            return;
        }

        foreach (var pair in validation.Accepted)
        {
            results[pair.Key] = pair.Value;
        }

        if (validation.Warnings.Count > 0)
        {
            _logger.Warn("extraction.batch_warnings", string.Join(" ", validation.Warnings), e =>
            {
                e.Stage = "validation";
                e.Count = validation.RetryOrdinals.Count;
                e.ProviderRequestId = response.RequestId;
            });
        }

        if (validation.RetryOrdinals.Count > 0)
        {
            var missing = records.Where(r => validation.RetryOrdinals.Contains(r.Ordinal)).ToList();
            if (round < MaxRetryRounds)
            {
                // Пропущенные записи — отдельным запросом.
                await ExtractIntoAsync(missing, results, round + 1, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var record in missing)
                {
                    results[record.Ordinal] = RowOutcome.Error(record.Ordinal, record.Line, record.Hash, "missing_result",
                        "Модель не вернула результат для записи (пропуск или повтор source_row_id) после повторных запросов.");
                }
            }
        }
    }

    private async Task SplitOrRetryAsync(IReadOnlyList<SourceRecord> records, Dictionary<long, RowOutcome> results, int round, CancellationToken cancellationToken)
    {
        if (records.Count == 1)
        {
            await ExtractIntoAsync(records, results, round, cancellationToken).ConfigureAwait(false);
            return;
        }

        var half = records.Count / 2;
        await ExtractIntoAsync(records.Take(half).ToList(), results, round, cancellationToken).ConfigureAwait(false);
        await ExtractIntoAsync(records.Skip(half).ToList(), results, round, cancellationToken).ConfigureAwait(false);
    }

    private async Task SplitOrFailAsync(IReadOnlyList<SourceRecord> records, Dictionary<long, RowOutcome> results, int round, string code, string message,
        CancellationToken cancellationToken)
    {
        if (records.Count == 1)
        {
            var record = records[0];
            results[record.Ordinal] = RowOutcome.Error(record.Ordinal, record.Line, record.Hash, code, message);
            return;
        }

        var half = records.Count / 2;
        await ExtractIntoAsync(records.Take(half).ToList(), results, round, cancellationToken).ConfigureAwait(false);
        await ExtractIntoAsync(records.Skip(half).ToList(), results, round, cancellationToken).ConfigureAwait(false);
    }

    private static string Shorten(string text) => text == null ? null : text.Length <= 300 ? text : text.Substring(0, 300) + "…";
}
