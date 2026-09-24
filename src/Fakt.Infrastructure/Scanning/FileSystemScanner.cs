using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Files;

namespace Fakt.Infrastructure.Scanning;

/// <summary>
/// Потоковый рекурсивный обход через FindFirstFileExW (Windows 7+: FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH).
/// Содержимое файлов не читается. Недоступные каталоги, длинные пути и точки повторной обработки
/// обрабатываются по отдельности; переходы по junction/symlink по умолчанию отключены, при включении
/// циклы отсекаются по паре (серийный номер тома, ID каталога).
/// </summary>
public sealed class FileSystemScanner : IFileScanner
{
    public Task<ScanSummary> ScanAsync(ScanOptions options, Action<IReadOnlyList<ScannedFile>> onBatch, Action<ScanIssue> onIssue,
        IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return Task.Factory.StartNew(() => Scan(options, onBatch, onIssue, progress, cancellationToken), cancellationToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static ScanSummary Scan(ScanOptions options, Action<IReadOnlyList<ScannedFile>> onBatch, Action<ScanIssue> onIssue,
        IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        var summary = new ScanSummary();
        var stopwatch = Stopwatch.StartNew();
        var root = PathUtil.StripLongPrefix(Path.GetFullPath(PathUtil.StripLongPrefix(options.RootPath))).TrimEnd('\\');
        if (root.Length == 2 && root[1] == ':')
        {
            root += "\\";
        }

        if (!Directory.Exists(PathUtil.ToLongPath(root)))
        {
            throw new DirectoryNotFoundException($"Папка не найдена: {root}");
        }

        var batch = new List<ScannedFile>(options.BatchSize);
        var lastFlush = Stopwatch.StartNew();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (options.FollowReparsePoints)
        {
            var rootId = DirectoryId(root);
            if (rootId != null)
            {
                visited.Add(rootId);
            }
        }

        void Issue(ScanIssue issue)
        {
            summary.Issues.Add(issue);
            onIssue?.Invoke(issue);
        }

        void Flush()
        {
            if (batch.Count == 0)
            {
                return;
            }

            onBatch?.Invoke(batch.ToArray());
            batch.Clear();
            lastFlush.Restart();
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                summary.Cancelled = true;
                break;
            }

            var directory = stack.Pop();
            summary.DirectoriesVisited++;
            progress?.Report(new ScanProgress { FilesFound = summary.FilesFound, DirectoriesVisited = summary.DirectoriesVisited, CurrentDirectory = directory });

            var pattern = PathUtil.ToLongPath(Path.Combine(directory, "*"));
            if (!pattern.StartsWith(@"\\?\", StringComparison.Ordinal) && pattern.Length >= 250)
            {
                pattern = @"\\?\" + pattern;
            }

            using (var handle = NativeMethods.FindFirstFileEx(pattern, NativeMethods.FindExInfoBasic, out var data,
                       NativeMethods.FindExSearchNameMatch, IntPtr.Zero, NativeMethods.FindFirstExLargeFetch))
            {
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ErrorFileNotFound || error == NativeMethods.ErrorNoMoreFiles)
                    {
                        continue;
                    }

                    if (error == NativeMethods.ErrorAccessDenied)
                    {
                        summary.InaccessibleDirectories++;
                        Issue(new ScanIssue(directory, ScanIssueKind.AccessDenied, "Нет доступа к папке — содержимое не просмотрено"));
                    }
                    else if (error == NativeMethods.ErrorFilenameExcedRange)
                    {
                        Issue(new ScanIssue(directory, ScanIssueKind.PathTooLong, "Слишком длинный путь"));
                    }
                    else
                    {
                        summary.InaccessibleDirectories++;
                        Issue(new ScanIssue(directory, ScanIssueKind.IoError, $"Ошибка чтения папки (код Win32 {error})"));
                    }

                    continue;
                }

                do
                {
                    var name = data.cFileName;
                    if (name == "." || name == "..")
                    {
                        continue;
                    }

                    var attributes = data.dwFileAttributes;
                    var fullPath = Path.Combine(directory, name);
                    var hidden = (attributes & (NativeMethods.FileAttributeHidden | NativeMethods.FileAttributeSystem)) != 0;
                    if (hidden && !options.IncludeHidden)
                    {
                        continue;
                    }

                    var isReparse = (attributes & NativeMethods.FileAttributeReparsePoint) != 0;
                    if ((attributes & NativeMethods.FileAttributeDirectory) != 0)
                    {
                        if (!options.Recursive)
                        {
                            continue;
                        }

                        if (isReparse && !options.FollowReparsePoints)
                        {
                            summary.SkippedReparsePoints++;
                            Issue(new ScanIssue(fullPath, ScanIssueKind.ReparsePointSkipped, "Точка соединения (junction/symlink) пропущена: переход отключён в настройках"));
                            continue;
                        }

                        if (options.FollowReparsePoints)
                        {
                            var id = DirectoryId(fullPath);
                            if (id != null && !visited.Add(id))
                            {
                                summary.CyclesDetected++;
                                Issue(new ScanIssue(fullPath, ScanIssueKind.CycleDetected, "Цикл: папка уже просмотрена по другому пути"));
                                continue;
                            }
                        }

                        stack.Push(fullPath);
                        continue;
                    }

                    if (isReparse && !options.FollowReparsePoints)
                    {
                        summary.SkippedReparsePoints++;
                        Issue(new ScanIssue(fullPath, ScanIssueKind.ReparsePointSkipped, "Символическая ссылка на файл пропущена"));
                        continue;
                    }

                    var size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    var info = FileAttributesInfo.None;
                    if ((attributes & NativeMethods.FileAttributeHidden) != 0) info |= FileAttributesInfo.Hidden;
                    if ((attributes & NativeMethods.FileAttributeSystem) != 0) info |= FileAttributesInfo.System;
                    if ((attributes & NativeMethods.FileAttributeReadOnly) != 0) info |= FileAttributesInfo.ReadOnly;
                    if (isReparse) info |= FileAttributesInfo.ReparsePoint;
                    if ((attributes & NativeMethods.FileAttributeOffline) != 0) info |= FileAttributesInfo.Offline;

                    batch.Add(new ScannedFile(fullPath, PathUtil.GetRelativePath(root, fullPath), name, Path.GetExtension(name),
                        size, NativeMethods.ToDateTimeUtc(data.ftLastWriteTime), info));
                    summary.FilesFound++;
                    if (batch.Count >= options.BatchSize || lastFlush.ElapsedMilliseconds > 150)
                    {
                        Flush();
                    }
                }
                while (!cancellationToken.IsCancellationRequested && NativeMethods.FindNextFile(handle, out data));
            }
        }

        Flush();
        summary.Elapsed = stopwatch.Elapsed;
        progress?.Report(new ScanProgress { FilesFound = summary.FilesFound, DirectoriesVisited = summary.DirectoriesVisited });
        return summary;
    }

    /// <summary>Идентификатор каталога «том:индекс» с разрешением ссылки — для отсечения циклов.</summary>
    private static string DirectoryId(string path)
    {
        using var handle = NativeMethods.CreateFile(PathUtil.ToLongPath(path), NativeMethods.FileReadAttributes, NativeMethods.FileShareAll,
            IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return info.VolumeSerialNumber.ToString("X8") + ":" + info.FileIndexHigh.ToString("X8") + info.FileIndexLow.ToString("X8");
    }
}
