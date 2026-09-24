using System;
using System.Collections.Generic;
using Fakt.Testing;

namespace Fakt.LoadTest;

/// <summary>Машиночитаемый результат прогона (сериализуется в JSON рядом с кратким Markdown-отчётом).</summary>
public sealed class LoadTestReport
{
    public string Tool { get; set; } = "Fakt.LoadTest";
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public string CommandLine { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; }
    public List<string> Notes { get; set; } = new();
    public EnvironmentInfo Environment { get; set; } = new();
    public SettingsInfo Settings { get; set; } = new();
    public DataInfo Data { get; set; } = new();
    public ParseOnlyResult ParseOnly { get; set; }
    public PipelineResult Pipeline { get; set; }
    public SqlResult Sql { get; set; }
    public List<CheckResult> Verification { get; set; } = new();
    public FullTextResult FullText { get; set; }
    public List<SearchResult> Search { get; set; } = new();
}

public sealed class EnvironmentInfo
{
    public string Machine { get; set; }
    public string Os { get; set; }
    public string Cpu { get; set; }
    public int LogicalProcessors { get; set; }
    public double TotalRamGb { get; set; }
    public string DotNetRuntime { get; set; }
    public bool Is64BitProcess { get; set; }
    public string GcMode { get; set; }
    public string SqlServer { get; set; }
    public string SqlServerVersion { get; set; }
    public string SqlServerEdition { get; set; }
    public string SqlTransport { get; set; }
    public string Database { get; set; }
    public string DatabaseRecoveryModel { get; set; }
    public string PythonPath { get; set; }
    public string WorkerDirectory { get; set; }
    public string WorkerVersion { get; set; }
    public string PythonVersion { get; set; }
    public string PandasVersion { get; set; }
    public string NumpyVersion { get; set; }
    public string WorkerPlatform { get; set; }
}

public sealed class SettingsInfo
{
    public int Records { get; set; }
    public string Format { get; set; }
    public int ChunkSize { get; set; }
    public int BatchRows { get; set; }
    public int Concurrency { get; set; }
    public int QueueCapacity { get; set; }
    public int SqlBatchSize { get; set; }
    public int MaxInputTokens { get; set; }
    public string Simulator { get; set; }
    public List<string> MigrationsApplied { get; set; } = new();
}

public sealed class DataInfo
{
    public string Path { get; set; }
    public long SizeBytes { get; set; }
    public double SizeMb { get; set; }
    public bool Generated { get; set; }
    public double GenerationSeconds { get; set; }
    public string Generator { get; set; }
}

public sealed class ParseOnlyResult
{
    public double Seconds { get; set; }
    public long Records { get; set; }
    public double RecordsPerSecond { get; set; }
    public double MegabytesPerSecond { get; set; }
    public long Chunks { get; set; }
    public long BadRecords { get; set; }
    public long BlankLines { get; set; }
    public bool OrdinalsContiguous { get; set; }
    public double ReadChunkP50Ms { get; set; }
    public double ReadChunkP95Ms { get; set; }
    public double ReadChunkMaxMs { get; set; }
    public PhaseMemory Memory { get; set; }
}

public sealed class PipelineResult
{
    public string JobStatus { get; set; }
    public string StatusMessage { get; set; }
    public double TotalSeconds { get; set; }
    public double HashAndRegisterSeconds { get; set; }
    public double ProcessingSeconds { get; set; }
    public double RecordsPerSecondTotal { get; set; }
    public double RecordsPerSecondProcessing { get; set; }
    public long LlmRequests { get; set; }
    public long RecordsSentToLlm { get; set; }
    public Dictionary<string, long> SimulatedFaults { get; set; }
    public long Commits { get; set; }
    public long FailedCommits { get; set; }
    public long DuplicateCommits { get; set; }
    public double AverageRowsPerCommit { get; set; }
    public long MaxRowsPerCommit { get; set; }
    public double CommitP50Ms { get; set; }
    public double CommitP95Ms { get; set; }
    public double CommitMaxMs { get; set; }
    public double CommitTotalSeconds { get; set; }

    /// <summary>Доля времени обработки, занятая SQL-фиксациями (цикл фиксации однопоточный).</summary>
    public double CommitBusyShare { get; set; }

    public long WorkerChunks { get; set; }
    public double WorkerReadTotalSeconds { get; set; }
    public double WorkerBusyShare { get; set; }
    public double ReadChunkP50Ms { get; set; }
    public double ReadChunkP95Ms { get; set; }
    public PhaseMemory Memory { get; set; }
    public double ManagedHeapAfterFullGcMb { get; set; }
    public double DotNetPrivateAfterRunMb { get; set; }
    public Dictionary<string, long> LogCounts { get; set; }
    public List<string> RecentWarnings { get; set; }
    public List<ProgressPoint> ProgressByShare { get; set; } = new();
}

/// <summary>Состояние на долях пути (10 %, 25 %, …): рост памяти с объёмом обработанного виден сразу.</summary>
public sealed class ProgressPoint
{
    public string Share { get; set; }
    public double Seconds { get; set; }
    public long RecordsCommitted { get; set; }
    public double DotNetPrivateMb { get; set; }
    public double ManagedHeapMb { get; set; }
    public double PythonPrivateMb { get; set; }
    public long InFlightRecords { get; set; }
}

public sealed class SqlResult
{
    public StoredFileResults Stored { get; set; }
    public long CommitLogRows { get; set; }
    public long JobFileRecordsRead { get; set; }
    public long JobFileExtracted { get; set; }
    public long JobFileObservations { get; set; }
    public long JobFileRequests { get; set; }
    public double DatabaseSizeMb { get; set; }
    public double DataUsedMb { get; set; }
    public double LogSizeMb { get; set; }
    public double VerificationQuerySeconds { get; set; }

    /// <summary>Место по таблицам (с индексами, LOB и внутренними таблицами полнотекстового индекса).</summary>
    public List<TableSize> Tables { get; set; } = new();
}

public sealed class TableSize
{
    public string Table { get; set; }
    public long Rows { get; set; }
    public double ReservedMb { get; set; }
}

public sealed class CheckResult
{
    public string Name { get; set; }
    public bool Passed { get; set; }
    public string Details { get; set; }
}

public sealed class FullTextResult
{
    public long PendingAtPipelineEnd { get; set; }
    public double WaitSecondsAfterPipeline { get; set; }
    public bool TimedOut { get; set; }
    public long IndexedItems { get; set; }
    public int? PopulateStatus { get; set; }
}

public sealed class SearchResult
{
    public string Name { get; set; }
    public string Description { get; set; }
    public List<double> RunsMs { get; set; } = new();
    public double FirstMs { get; set; }
    public double WarmMedianMs { get; set; }
    public int Rows { get; set; }
    public bool HasMore { get; set; }
    public long? TotalCount { get; set; }
    public bool UsedFullText { get; set; }
    public string Error { get; set; }
}
