using System;
using System.Collections.Generic;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Structure;

namespace Fakt.Core.Processing;

public enum JobStatus
{
    Created,
    Running,
    Paused,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled,
    Interrupted,
}

public static class JobStatusText
{
    public static string ToText(this JobStatus status)
    {
        switch (status)
        {
            case JobStatus.Created: return "Создано";
            case JobStatus.Running: return "Выполняется";
            case JobStatus.Paused: return "На паузе";
            case JobStatus.Completed: return "Завершено";
            case JobStatus.CompletedWithErrors: return "Завершено с ошибками";
            case JobStatus.Failed: return "Ошибка";
            case JobStatus.Cancelled: return "Остановлено";
            case JobStatus.Interrupted: return "Прервано";
            default: return status.ToString();
        }
    }

    public static StatusTone ToTone(this JobStatus status)
    {
        switch (status)
        {
            case JobStatus.Completed: return StatusTone.Success;
            case JobStatus.Running: return StatusTone.Info;
            case JobStatus.Failed: return StatusTone.Error;
            case JobStatus.Created: return StatusTone.Neutral;
            default: return StatusTone.Warning;
        }
    }
}

/// <summary>
/// Снимок конфигурации, фиксируемый при запуске задания (без секретов). Изменение настроек после запуска
/// не влияет на выполняющееся задание; возобновление использует этот снимок.
/// </summary>
public sealed class JobSnapshot
{
    public string AppVersion { get; set; }
    public string WorkerVersion { get; set; }
    public string PythonVersion { get; set; }
    public string PandasVersion { get; set; }
    public LlmProfileSnapshot Llm { get; set; }
    public ProcessingSnapshot Processing { get; set; }
    public string StructurePromptVersion { get; set; }
    public string FactsPromptVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; }
}

public sealed class LlmProfileSnapshot
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; }
    public string ProviderId { get; set; }
    public string BaseUrl { get; set; }
    public string ModelId { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
    public List<string> HeaderNames { get; set; } = new();
    public int TimeoutSeconds { get; set; }
    public int MaxOutputTokens { get; set; }
    public int MaxConcurrentRequests { get; set; }
    public int RequestsPerMinute { get; set; }
    public int TokensPerMinute { get; set; }
    public int BatchRows { get; set; }
    public int MaxInputTokensPerRequest { get; set; }
    public double? Temperature { get; set; }
    public StructuredOutputMode OutputMode { get; set; }
    public bool ExternalProvider { get; set; }
    public PricingInfo Pricing { get; set; }

    public static LlmProfileSnapshot From(LlmProfile profile, StructuredOutputMode resolvedMode, bool external)
    {
        var snapshot = new LlmProfileSnapshot
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProviderId = profile.ProviderId,
            BaseUrl = profile.BaseUrl,
            ModelId = profile.ModelId,
            Options = new Dictionary<string, string>(profile.Options ?? new Dictionary<string, string>()),
            TimeoutSeconds = profile.TimeoutSeconds,
            MaxOutputTokens = profile.MaxOutputTokens,
            MaxConcurrentRequests = profile.MaxConcurrentRequests,
            RequestsPerMinute = profile.RequestsPerMinute,
            TokensPerMinute = profile.TokensPerMinute,
            BatchRows = profile.BatchRows,
            MaxInputTokensPerRequest = profile.MaxInputTokensPerRequest,
            Temperature = profile.Capabilities?.Temperature == true ? profile.Temperature : null,
            OutputMode = resolvedMode,
            ExternalProvider = external,
            Pricing = profile.Pricing,
        };
        foreach (var header in profile.ExtraHeaders ?? new List<HeaderSetting>())
        {
            snapshot.HeaderNames.Add(header.Name);
        }

        return snapshot;
    }

    /// <summary>Профиль для выполнения по снимку: параметры из снимка, секреты — из хранилища текущего профиля.</summary>
    public LlmProfile ToProfile(LlmProfile current)
    {
        var profile = current.Clone();
        profile.BaseUrl = BaseUrl;
        profile.ModelId = ModelId;
        profile.Options = new Dictionary<string, string>(Options ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        profile.TimeoutSeconds = TimeoutSeconds;
        profile.MaxOutputTokens = MaxOutputTokens;
        profile.MaxConcurrentRequests = MaxConcurrentRequests;
        profile.RequestsPerMinute = RequestsPerMinute;
        profile.TokensPerMinute = TokensPerMinute;
        profile.BatchRows = BatchRows;
        profile.MaxInputTokensPerRequest = MaxInputTokensPerRequest;
        profile.Temperature = Temperature;
        profile.OutputMode = OutputMode;
        return profile;
    }
}

public sealed class ProcessingSnapshot
{
    public int ChunkSize { get; set; }
    public int SqlBatchSize { get; set; }
    public int QueueCapacity { get; set; }
    public int MaxAttemptsPerRequest { get; set; }
    public int BudgetMaxRequests { get; set; }
    public long BudgetMaxTokens { get; set; }
}

public sealed class JobRecord
{
    public long JobId { get; set; }
    public Guid JobGuid { get; set; } = Guid.NewGuid();
    public JobStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string CreatedBy { get; set; }
    public string SnapshotJson { get; set; }
    public string Summary { get; set; }
    public int FileCount { get; set; }
    public long RecordsProcessed { get; set; }
    public long ObservationsSaved { get; set; }
    public long RecordsNoFacts { get; set; }
    public long RecordsError { get; set; }
    public long Requests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

public sealed class JobFileRecord
{
    public long JobFileId { get; set; }
    public long JobId { get; set; }
    public long SourceFileId { get; set; }
    public string FileCode { get; set; }
    public string FileName { get; set; }
    public string SourcePath { get; set; }
    public FileStatus Status { get; set; }
    public string StructureJson { get; set; }
    public string ExtractionVersion { get; set; }
    public long ExpectedSize { get; set; }
    public DateTime ExpectedLastWriteUtc { get; set; }
    public string ContentHashHex { get; set; }
    public long CheckpointOrdinal { get; set; }
    public long RecordsRead { get; set; }
    public long RecordsExtracted { get; set; }
    public long RecordsNoFacts { get; set; }
    public long RecordsError { get; set; }
    public long ObservationsSaved { get; set; }
    public long Requests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string LastError { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public long RecordsProcessed => RecordsExtracted + RecordsNoFacts + RecordsError;
}

/// <summary>Оценка большого запуска по небольшому образцу. Денежная оценка — только при заданных тарифах с датой.</summary>
public sealed class ProcessingEstimate
{
    public int Files { get; set; }
    public long TotalBytes { get; set; }
    public long? EstimatedRecords { get; set; }
    public long EstimatedRequests { get; set; }
    public long EstimatedInputTokens { get; set; }
    public long EstimatedOutputTokens { get; set; }
    public TimeSpan? EstimatedDuration { get; set; }
    public decimal? EstimatedCost { get; set; }
    public string CostText { get; set; }
    public bool MeasuredOnSample { get; set; }
    public string SampleNote { get; set; }
    public List<string> Assumptions { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>Файл, подготовленный к извлечению: структура проверена парсером.</summary>
public sealed class PreparedFile
{
    public ScannedFile File { get; set; }
    public StructureDescriptor Structure { get; set; }
    public long SourceFileId { get; set; }
    public string FileCode { get; set; }
    public byte[] ContentHash { get; set; }
    public string ExtractionVersion { get; set; }
    public long JobFileId { get; set; }
}
