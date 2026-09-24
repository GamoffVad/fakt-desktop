using System;
using System.IO;

namespace Fakt.Core.Files;

/// <summary>Работа с длинными путями на Windows 7: префикс \\?\ вместо системного параметра LongPathsEnabled (Windows 10+).</summary>
public static class PathUtil
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>Порог, после которого используется расширенный синтаксис (с запасом до MAX_PATH = 260).</summary>
    public const int LongPathThreshold = 240;

    public static string ToLongPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (path.Length < LongPathThreshold)
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ExtendedUncPrefix + path.Substring(2);
        }

        return ExtendedPrefix + path;
    }

    public static string StripLongPrefix(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path.Substring(ExtendedUncPrefix.Length);
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path.Substring(ExtendedPrefix.Length);
        }

        return path;
    }

    public static string GetRelativePath(string root, string fullPath)
    {
        var normalizedRoot = StripLongPrefix(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = StripLongPrefix(fullPath);
        if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedPath.Substring(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Length == 0 ? "." : relative;
        }

        return normalizedPath;
    }

    /// <summary>Каноническая форма пути для хеша SourcePathHash: без префикса \\?\, обратные косые, верхний регистр.</summary>
    public static string NormalizeForIdentity(string fullPath)
    {
        return StripLongPrefix(fullPath).Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }
}
