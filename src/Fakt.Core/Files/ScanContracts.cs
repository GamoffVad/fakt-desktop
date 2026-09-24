using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fakt.Core.Files;

public sealed class ScanOptions
{
    public string RootPath { get; set; }

    public bool Recursive { get; set; } = true;

    /// <summary>Переходить по junction/symlink. По умолчанию выключено; при включении циклы отсекаются по ID каталога.</summary>
    public bool FollowReparsePoints { get; set; }

    public bool IncludeHidden { get; set; }

    /// <summary>Сколько файлов собирать перед передачей пакета в интерфейс.</summary>
    public int BatchSize { get; set; } = 250;
}

public enum ScanIssueKind
{
    AccessDenied,
    PathTooLong,
    ReparsePointSkipped,
    CycleDetected,
    IoError,
}

public sealed class ScanIssue
{
    public ScanIssue(string path, ScanIssueKind kind, string message)
    {
        Path = path;
        Kind = kind;
        Message = message;
    }

    public string Path { get; }

    public ScanIssueKind Kind { get; }

    public string Message { get; }
}

public sealed class ScanProgress
{
    public long FilesFound { get; set; }

    public long DirectoriesVisited { get; set; }

    public string CurrentDirectory { get; set; }
}

public sealed class ScanSummary
{
    public long FilesFound { get; set; }

    public long DirectoriesVisited { get; set; }

    public long InaccessibleDirectories { get; set; }

    public long SkippedReparsePoints { get; set; }

    public long CyclesDetected { get; set; }

    public TimeSpan Elapsed { get; set; }

    public bool Cancelled { get; set; }

    public List<ScanIssue> Issues { get; } = new();
}

public interface IFileScanner
{
    /// <summary>
    /// Потоковый обход каталога без чтения содержимого файлов. Найденные файлы передаются пакетами,
    /// недоступные каталоги и пропущенные точки повторной обработки — как отдельные сообщения.
    /// </summary>
    Task<ScanSummary> ScanAsync(ScanOptions options, Action<IReadOnlyList<ScannedFile>> onBatch, Action<ScanIssue> onIssue,
        IProgress<ScanProgress> progress, CancellationToken cancellationToken);
}

public interface IFileHasher
{
    /// <summary>Потоковый SHA-256 всего содержимого файла.</summary>
    Task<byte[]> ComputeSha256Async(string path, IProgress<long> bytesProgress, CancellationToken cancellationToken);
}
