using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Llm;
using Fakt.Core.Extraction;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Settings;
using Fakt.Core.Structure;
using Fakt.Core.Worker;
using Newtonsoft.Json;

namespace Fakt.Application.Structure;

public sealed class StructureDetectionResult
{
    public FileStatus Status { get; set; }

    /// <summary>Краткая причина для колонки «Ошибки и краткая причина».</summary>
    public string Message { get; set; }

    /// <summary>Проверенная парсером структура (effective_structure), пригодная для чтения.</summary>
    public StructureDescriptor Structure { get; set; }

    /// <summary>Исходный ответ модели после проверки контракта (для редактирования вручную).</summary>
    public StructureDescriptor ModelStructure { get; set; }

    public SampleResult Sample { get; set; }
    public ValidateResult Validation { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> SuggestedColumns { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string RequestId { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool UsedLlm { get; set; }

    public string FormatText => Status == FileStatus.Tabular || Status == FileStatus.NeedsConfiguration
        ? Structure?.Describe() ?? ModelStructure?.Describe()
        : null;
}

/// <summary>
/// Определение структуры по первым пяти физическим строкам: образец (без чтения всего файла) → модель
/// → проверка контракта → проверка парсером на ограниченном фрагменте. Двоичные и пустые файлы модели
/// не отправляются. Недостаточный образец даёт «Требует настройки», а не «Не табличный»; увеличение
/// образца — только отдельным действием пользователя.
/// </summary>
public sealed class StructureDetectionService
{
    private readonly IAppLogger _logger;

    public StructureDetectionService(IAppLogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<StructureDetectionResult> DetectAsync(ScannedFile file, IWorkerClient worker, ResilientLlmClient llm, StructuredOutputMode mode,
        ProcessingSettings settings, bool extendedSample, string encodingOverride, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new StructureDetectionResult();
        var hint = FileKindClassifier.Classify(file.Extension, out var hintReason);
        if (hint == FileKindHint.Binary || hint == FileKindHint.AdapterRequired)
        {
            result.Status = FileStatus.NotSupported;
            result.Message = hintReason;
            return result;
        }

        var lines = extendedSample ? settings.ExtendedSampleLines : settings.SampleLines;
        var bytes = extendedSample ? settings.ExtendedSampleMaxBytes : settings.SampleMaxBytes;
        result.Sample = await worker.SampleAsync(file.LongPath, lines, bytes, encodingOverride, cancellationToken).ConfigureAwait(false);
        var sample = result.Sample;
        if (sample.IsEmpty)
        {
            result.Status = FileStatus.NotTabular;
            result.Message = "Файл пуст — записей нет";
            return result;
        }

        if (sample.IsBinary)
        {
            result.Status = FileStatus.NotSupported;
            result.Message = sample.BinaryReason ?? "Двоичный файл: не отправляется модели";
            return result;
        }

        if (sample.Truncated)
        {
            result.Warnings.Add($"Образец усечён лимитом {bytes / 1024} КиБ (строка {(sample.TruncatedLineIndex ?? 0) + 1} длиннее доступного объёма).");
        }

        if (sample.ReplacementCount > 0)
        {
            result.Warnings.Add($"При декодировании образца в кодировке {EncodingNames.DisplayName(sample.Encoding)} заменено символов: {sample.ReplacementCount}. Проверьте кодировку.");
        }

        var request = new LlmJsonRequest
        {
            SystemPrompt = Prompts.StructureSystem,
            UserContent = Prompts.StructureUser(file.Name, file.Extension, sample, lines),
            SchemaName = JsonSchemas.StructureSchemaName,
            Schema = JsonSchemas.Structure(),
            MaxOutputTokens = llm.Config.Profile.MaxOutputTokens,
            Temperature = llm.Config.Profile.Temperature,
            Mode = mode,
            Purpose = "structure",
        };

        StructureDescriptor proposed = null;
        for (var attempt = 1; attempt <= 2 && proposed == null; attempt++)
        {
            var response = await llm.CompleteAsync(request, TokenEstimator.EstimateRaw(request.SystemPrompt + request.UserContent) + 300, cancellationToken).ConfigureAwait(false);
            result.UsedLlm = true;
            result.InputTokens = (result.InputTokens ?? 0) + (response.InputTokens ?? 0);
            result.OutputTokens = (result.OutputTokens ?? 0) + (response.OutputTokens ?? 0);
            result.RequestId = response.RequestId;
            if (response.IsTruncated)
            {
                result.Errors.Add("Ответ модели об определении структуры усечён пределом выходных токенов.");
                continue;
            }

            try
            {
                var text = ExtractionValidator.StripCodeFence((response.Text ?? string.Empty).Trim());
                proposed = JsonConvert.DeserializeObject<StructureWire>(text)?.ToDescriptor();
            }
            catch (JsonException ex)
            {
                _logger.Warn("structure.invalid_response", "Ответ модели о структуре не является корректным JSON: " + ex.Message, e =>
                {
                    e.Stage = "structure";
                    e.ProviderRequestId = response.RequestId;
                    e.Category = ErrorCategory.ModelResponse;
                });
            }
        }

        if (proposed == null)
        {
            result.Status = FileStatus.NeedsConfiguration;
            result.Message = "Модель не вернула корректное описание структуры; задайте параметры вручную";
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        var check = StructureContractValidator.Validate(proposed, encodingOverride ?? sample.Encoding);
        result.ModelStructure = check.Normalized;
        result.Warnings.AddRange(check.Warnings);
        switch (check.Normalized?.Classification)
        {
            case StructureDescriptor.ClassUnstructured:
                result.Status = FileStatus.NotTabular;
                result.Message = check.Normalized.Reason ?? "Модель не обнаружила табличной структуры";
                result.Elapsed = stopwatch.Elapsed;
                return result;
            case StructureDescriptor.ClassInsufficientSample:
                result.Status = FileStatus.NeedsConfiguration;
                result.Message = "Недостаточно образца: " + (check.Normalized.Reason ?? "первых строк не хватает для определения структуры") +
                                 (extendedSample ? string.Empty : ". Доступен расширенный образец.");
                result.Elapsed = stopwatch.Elapsed;
                return result;
        }

        if (!check.IsValid)
        {
            result.Errors.AddRange(check.Errors);
            result.Status = FileStatus.NeedsConfiguration;
            result.Message = "Ответ модели нарушает контракт структуры: " + check.Errors.First();
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        var effective = check.Normalized;
        if (effective.Format == StructureDescriptor.FormatFixedWidth && effective.FixedWidths?.Count > 1)
        {
            // Модель определила фиксированную ширину; границы колонок выравниваются по образцу (±3 символа).
            var refined = FixedWidthRefiner.Refine(effective.FixedWidths, (sample.Lines ?? new List<string>()).Skip(effective.SkipRows ?? 0), out var changed);
            if (changed)
            {
                result.Warnings.Add($"Ширины колонок уточнены по выравниванию строк образца: {string.Join(", ", effective.FixedWidths)} → {string.Join(", ", refined)}.");
                effective = effective.Clone();
                effective.FixedWidths = refined;
            }
        }

        await ValidateWithParserAsync(file, effective, worker, settings, result, cancellationToken).ConfigureAwait(false);
        ReconcileInferredColumnNames(result);
        result.Elapsed = stopwatch.Elapsed;
        return result;
    }

    /// <summary>
    /// Файл без заголовка: имена колонок, подобранные моделью, сверяются со значениями предпросмотра, и явные
    /// противоречия формату (например, «СНИЛС» у 12-значных номеров) исправляются с предупреждением.
    /// </summary>
    private static void ReconcileInferredColumnNames(StructureDetectionResult result)
    {
        var structure = result.Structure;
        var preview = result.Validation?.Preview;
        if (structure?.HasInferredColumnNames != true || structure.Columns == null || preview == null || preview.Rows.Count == 0)
        {
            return;
        }

        var rows = preview.Rows.Where(r => r.Values != null).Select(r => (IReadOnlyList<string>)r.Values).ToList();
        foreach (var rename in InferredColumnNames.Reconcile(structure.Columns, rows))
        {
            if (rename.Index < preview.Columns.Count && preview.Columns[rename.Index] == rename.OldName)
            {
                preview.Columns[rename.Index] = rename.NewName;
            }

            result.Warnings.Add($"Имя колонки «{rename.OldName}», подобранное моделью, исправлено на «{rename.NewName}»: {rename.Reason}.");
        }
    }

    /// <summary>Проверка структуры, заданной или исправленной пользователем, — без обращения к модели.</summary>
    public async Task<StructureDetectionResult> ValidateManualAsync(ScannedFile file, StructureDescriptor structure, IWorkerClient worker,
        ProcessingSettings settings, CancellationToken cancellationToken)
    {
        var result = new StructureDetectionResult();
        var draft = structure.Clone();
        draft.Classification = StructureDescriptor.ClassStructured;
        var check = StructureContractValidator.Validate(draft, draft.Encoding);
        result.ModelStructure = check.Normalized;
        result.Warnings.AddRange(check.Warnings);
        if (!check.IsValid)
        {
            result.Errors.AddRange(check.Errors);
            result.Status = FileStatus.NeedsConfiguration;
            result.Message = check.Errors.First();
            return result;
        }

        await ValidateWithParserAsync(file, check.Normalized, worker, settings, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task ValidateWithParserAsync(ScannedFile file, StructureDescriptor structure, IWorkerClient worker, ProcessingSettings settings,
        StructureDetectionResult result, CancellationToken cancellationToken)
    {
        var validation = await worker.ValidateAsync(file.LongPath, structure, settings.PreviewRecords, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        result.Validation = validation;
        result.Warnings.AddRange(validation.Warnings.Select(w => w.ToString()));
        result.SuggestedColumns = validation.Suggestions?.Columns;
        if (!validation.Ok)
        {
            result.Errors.AddRange(validation.Errors.Select(e => e.ToString()));
            result.Status = FileStatus.NeedsConfiguration;
            result.Structure = validation.EffectiveStructure ?? structure;
            result.Message = "Проверка парсером не пройдена: " + (validation.Errors.FirstOrDefault()?.Message ?? "структура не согласуется с данными") +
                             (result.SuggestedColumns != null ? " Есть предложение исправления." : string.Empty);
            return;
        }

        result.Structure = validation.EffectiveStructure ?? structure;
        result.Status = FileStatus.Tabular;
        result.Message = validation.BadRecords > 0
            ? $"Структура подтверждена; в проверенном фрагменте записей с ошибками: {validation.BadRecords}"
            : null;
    }
}
