using System;

namespace Fakt.Core.Files;

/// <summary>Результат сканирования: метаданные файла без чтения содержимого.</summary>
public sealed class ScannedFile
{
    public ScannedFile(string fullPath, string relativePath, string name, string extension, long size, DateTime lastWriteTimeUtc, FileAttributesInfo attributes)
    {
        FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
        RelativePath = relativePath ?? string.Empty;
        Name = name ?? string.Empty;
        Extension = extension ?? string.Empty;
        Size = size;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Attributes = attributes;
    }

    /// <summary>Полный путь без префикса \\?\ (для отображения); для доступа к длинным путям используйте <see cref="LongPath"/>.</summary>
    public string FullPath { get; }

    public string RelativePath { get; }

    public string Name { get; }

    public string Extension { get; }

    public long Size { get; }

    public DateTime LastWriteTimeUtc { get; }

    public FileAttributesInfo Attributes { get; }

    /// <summary>Путь, пригодный для открытия файла при длине более MAX_PATH.</summary>
    public string LongPath => PathUtil.ToLongPath(FullPath);

    public FileFingerprint Fingerprint => new(Size, LastWriteTimeUtc);
}

[Flags]
public enum FileAttributesInfo
{
    None = 0,
    Hidden = 1,
    System = 2,
    ReadOnly = 4,
    ReparsePoint = 8,
    Offline = 16,
}

/// <summary>Быстрый отпечаток версии файла: размер и время изменения. Полный хеш содержимого хранится отдельно.</summary>
public readonly struct FileFingerprint : IEquatable<FileFingerprint>
{
    public FileFingerprint(long size, DateTime lastWriteTimeUtc)
    {
        Size = size;
        LastWriteTimeUtc = DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc);
    }

    public long Size { get; }

    public DateTime LastWriteTimeUtc { get; }

    /// <summary>Время изменения в наносекундах Unix — формат, который ожидает worker (st_mtime_ns).</summary>
    public long LastWriteUnixNanoseconds => (LastWriteTimeUtc.Ticks - UnixEpochTicks) * 100;

    private const long UnixEpochTicks = 621355968000000000L;

    public bool Equals(FileFingerprint other) => Size == other.Size && LastWriteTimeUtc.Ticks == other.LastWriteTimeUtc.Ticks;

    public override bool Equals(object obj) => obj is FileFingerprint other && Equals(other);

    public override int GetHashCode() => unchecked((Size.GetHashCode() * 397) ^ LastWriteTimeUtc.Ticks.GetHashCode());

    public override string ToString() => $"{Size} байт, {LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss.fffffff} UTC";
}
