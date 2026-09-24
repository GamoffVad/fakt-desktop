using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Structure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Core.Worker;

/// <summary>Клиент Python worker (протокол версии 1, worker/protocol/PROTOCOL.md).</summary>
public interface IWorkerClient : IDisposable
{
    Task<WorkerHello> HelloAsync(CancellationToken cancellationToken);

    Task<SampleResult> SampleAsync(string path, int maxLines, int maxBytes, string encoding, CancellationToken cancellationToken);

    Task<ValidateResult> ValidateAsync(string path, StructureDescriptor structure, int maxRecords, int maxBytes, CancellationToken cancellationToken);

    Task<string> OpenReaderAsync(OpenReaderRequest request, CancellationToken cancellationToken);

    Task<RecordChunk> ReadChunkAsync(string readerId, CancellationToken cancellationToken);

    Task CloseReaderAsync(string readerId, CancellationToken cancellationToken);

    /// <summary>Процесс worker завершился или канал разорван; клиент нужно пересоздать.</summary>
    bool IsFaulted { get; }
}

public interface IWorkerClientFactory
{
    IWorkerClient Create();

    /// <summary>Проверка готовности: путь к Python и worker, версия протокола и библиотек.</summary>
    Task<WorkerHello> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class WorkerException : Exception
{
    public WorkerException(string code, string message, JObject details = null, Exception inner = null)
        : base(message, inner)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JObject Details { get; }

    public bool IsFileChanged => Code == "file_changed";

    public bool IsProcessFailure => Code == "worker_crashed" || Code == "worker_start_failed" || Code == "protocol_error" || Code == "worker_timeout";
}

public sealed class WorkerHello
{
    [JsonProperty("protocol_version")] public int ProtocolVersion { get; set; }
    [JsonProperty("worker_version")] public string WorkerVersion { get; set; }
    [JsonProperty("python_version")] public string PythonVersion { get; set; }
    [JsonProperty("pandas_version")] public string PandasVersion { get; set; }
    [JsonProperty("numpy_version")] public string NumpyVersion { get; set; }
    [JsonProperty("defusedxml_version")] public string DefusedXmlVersion { get; set; }
    [JsonProperty("platform")] public string Platform { get; set; }
    [JsonProperty("pid")] public int Pid { get; set; }
}

public sealed class SampleResult
{
    [JsonProperty("file_size")] public long FileSize { get; set; }
    [JsonProperty("is_empty")] public bool IsEmpty { get; set; }
    [JsonProperty("is_binary")] public bool IsBinary { get; set; }
    [JsonProperty("binary_kind")] public string BinaryKind { get; set; }
    [JsonProperty("binary_reason")] public string BinaryReason { get; set; }
    [JsonProperty("encoding")] public string Encoding { get; set; }
    [JsonProperty("encoding_source")] public string EncodingSource { get; set; }
    [JsonProperty("encoding_confidence")] public double EncodingConfidence { get; set; }
    [JsonProperty("bom")] public string Bom { get; set; }
    [JsonProperty("lines")] public List<string> Lines { get; set; } = new();
    [JsonProperty("line_count")] public int LineCount { get; set; }
    [JsonProperty("bytes_read")] public long BytesRead { get; set; }
    [JsonProperty("eof_reached")] public bool EofReached { get; set; }
    [JsonProperty("truncated")] public bool Truncated { get; set; }
    [JsonProperty("truncated_line_index")] public int? TruncatedLineIndex { get; set; }
    [JsonProperty("fewer_lines")] public bool FewerLines { get; set; }
    [JsonProperty("line_terminator")] public string LineTerminator { get; set; }
    [JsonProperty("replacement_count")] public int ReplacementCount { get; set; }
}

public sealed class WorkerIssue
{
    [JsonProperty("code")] public string Code { get; set; }
    [JsonProperty("message")] public string Message { get; set; }
    [JsonProperty("ordinal")] public long? Ordinal { get; set; }

    public override string ToString() => Ordinal.HasValue ? $"Запись {Ordinal}: {Message}" : Message;
}

public sealed class ValidateResult
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("errors")] public List<WorkerIssue> Errors { get; set; } = new();
    [JsonProperty("warnings")] public List<WorkerIssue> Warnings { get; set; } = new();
    [JsonProperty("columns")] public List<string> Columns { get; set; } = new();
    [JsonProperty("effective_structure")] public StructureDescriptor EffectiveStructure { get; set; }
    [JsonProperty("suggestions")] public ValidateSuggestions Suggestions { get; set; }
    [JsonProperty("records_checked")] public long RecordsChecked { get; set; }
    [JsonProperty("bad_records")] public long BadRecords { get; set; }
    [JsonProperty("blank_lines")] public long BlankLines { get; set; }
    [JsonProperty("eof_reached")] public bool EofReached { get; set; }
    [JsonProperty("preview")] public PreviewData Preview { get; set; }
}

public sealed class ValidateSuggestions
{
    [JsonProperty("columns")] public List<string> Columns { get; set; }
}

public sealed class PreviewData
{
    [JsonProperty("columns")] public List<string> Columns { get; set; } = new();
    [JsonProperty("rows")] public List<PreviewRow> Rows { get; set; } = new();
}

public sealed class PreviewRow
{
    [JsonProperty("ordinal")] public long Ordinal { get; set; }
    [JsonProperty("line")] public long? Line { get; set; }
    [JsonProperty("values")] public List<string> Values { get; set; }
    [JsonProperty("error")] public WorkerIssue Error { get; set; }
}

public sealed class OpenReaderRequest
{
    public string Path { get; set; }
    public StructureDescriptor Structure { get; set; }
    public int ChunkSize { get; set; } = 5000;
    public long StartAfterOrdinal { get; set; }
    public int MaxChunkBytes { get; set; } = 16 * 1024 * 1024;
    public long? ExpectedSize { get; set; }
    public long? ExpectedMtimeNs { get; set; }
}

public sealed class RecordChunk
{
    [JsonProperty("columns")] public List<string> Columns { get; set; } = new();
    [JsonProperty("records")] public List<WireRecord> Records { get; set; } = new();
    [JsonProperty("eof")] public bool Eof { get; set; }
    [JsonProperty("stats")] public ChunkStats Stats { get; set; } = new();
    [JsonProperty("warnings")] public List<WorkerIssue> Warnings { get; set; } = new();
}

public sealed class WireRecord
{
    [JsonProperty("ordinal")] public long Ordinal { get; set; }
    [JsonProperty("line")] public long? Line { get; set; }
    [JsonProperty("end_line")] public long? EndLine { get; set; }
    [JsonProperty("values")] public List<string> Values { get; set; }
    [JsonProperty("hash")] public string Hash { get; set; }
    [JsonProperty("error")] public WorkerIssue Error { get; set; }
}

public sealed class ChunkStats
{
    [JsonProperty("records_emitted")] public long RecordsEmitted { get; set; }
    [JsonProperty("records_skipped")] public long RecordsSkipped { get; set; }
    [JsonProperty("bad_records")] public long BadRecords { get; set; }
    [JsonProperty("blank_lines")] public long BlankLines { get; set; }
    [JsonProperty("replacement_count")] public long ReplacementCount { get; set; }
}
