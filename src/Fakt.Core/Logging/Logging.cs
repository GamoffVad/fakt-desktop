using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Fakt.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Категории ошибок, которые интерфейс показывает раздельно (задание, раздел 13).</summary>
public enum ErrorCategory
{
    None,
    Connection,
    DataFormat,
    ModelResponse,
    Configuration,
    Access,
    Internal,
}

public sealed class LogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Event { get; set; }
    public string Message { get; set; }
    public ErrorCategory Category { get; set; }
    public long? JobId { get; set; }
    public long? FileId { get; set; }
    public string SourceRowId { get; set; }
    public string Stage { get; set; }
    public long? DurationMs { get; set; }
    public long? Count { get; set; }
    public string ErrorCode { get; set; }
    public string ProviderRequestId { get; set; }
    public string User { get; set; }
    public Dictionary<string, object> Data { get; set; }
}

public interface IAppLogger
{
    /// <summary>Подробный режим (включается администратором на ограниченный срок).</summary>
    bool IsVerbose { get; }

    void Write(LogEntry entry);
}

public static class LoggerExtensions
{
    public static void Info(this IAppLogger logger, string eventName, string message, Action<LogEntry> configure = null) =>
        logger.Write(Create(LogLevel.Info, eventName, message, configure));

    public static void Warn(this IAppLogger logger, string eventName, string message, Action<LogEntry> configure = null) =>
        logger.Write(Create(LogLevel.Warning, eventName, message, configure));

    public static void Error(this IAppLogger logger, string eventName, string message, ErrorCategory category, Exception exception = null, Action<LogEntry> configure = null)
    {
        var entry = Create(LogLevel.Error, eventName, message, configure);
        entry.Category = category;
        if (exception != null)
        {
            entry.Data ??= new Dictionary<string, object>();
            entry.Data["exception_type"] = exception.GetType().FullName;
            entry.Data["exception_message"] = exception.Message;
        }

        logger.Write(entry);
    }

    public static void Debug(this IAppLogger logger, string eventName, string message, Action<LogEntry> configure = null)
    {
        if (logger.IsVerbose)
        {
            logger.Write(Create(LogLevel.Debug, eventName, message, configure));
        }
    }

    private static LogEntry Create(LogLevel level, string eventName, string message, Action<LogEntry> configure)
    {
        var entry = new LogEntry { Level = level, Event = eventName, Message = message };
        configure?.Invoke(entry);
        return entry;
    }
}

public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    public bool IsVerbose => false;

    public void Write(LogEntry entry)
    {
    }
}

/// <summary>
/// Маскирование секретов и персональных данных в тексте журнала. По умолчанию журнал не содержит
/// содержимого записей; санитайзер — дополнительная защита для сообщений об ошибках провайдеров и SQL.
/// </summary>
public static class LogSanitizer
{
    private static readonly Regex[] SecretPatterns =
    {
        new(@"(?i)\b(sk-(?:ant-|proj-|or-)?[A-Za-z0-9_\-]{12,})", RegexOptions.CultureInvariant),
        new(@"\bAIza[0-9A-Za-z_\-]{20,}", RegexOptions.CultureInvariant),
        new(@"\b(?:xai|gsk|fw|tgp|pplx)[-_][A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant),
        new(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.CultureInvariant),
        new(@"(?i)(api[-_ ]?key|x-api-key|api-key|x-goog-api-key|authorization|token|secret)(""?\s*[:=]\s*""?)[^\s"",;]{6,}", RegexOptions.CultureInvariant),
        new(@"(?i)\b(password|pwd)\s*=\s*[^;]*", RegexOptions.CultureInvariant),
    };

    private static readonly Regex Email = new(@"\b([A-Za-z0-9._%+\-])([A-Za-z0-9._%+\-]*)@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})\b", RegexOptions.CultureInvariant);

    // Последовательности из 7+ цифр с разделителями: телефоны, счета, карты, документы.
    private static readonly Regex DigitRun = new(@"\+?\d[\d\s\-().]{5,}\d", RegexOptions.CultureInvariant);

    private static readonly Regex DatePrefix = new(@"^(?:\d{4}-\d{2}-\d{2}|\d{2}\.\d{2}\.\d{4})", RegexOptions.CultureInvariant);

    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        foreach (var pattern in SecretPatterns)
        {
            result = pattern.Replace(result, m =>
            {
                if (m.Groups.Count >= 3 && m.Groups[2].Success && m.Groups[1].Success && !m.Value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                {
                    return m.Groups[1].Value + m.Groups[2].Value + "***";
                }

                return "***";
            });
        }

        result = Email.Replace(result, m => m.Groups[1].Value + "***@" + m.Groups[3].Value);
        result = DigitRun.Replace(result, MaskDigits);
        return result;
    }

    private static string MaskDigits(Match match)
    {
        var value = match.Value;
        var digitCount = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                digitCount++;
            }
        }

        if (digitCount < 7)
        {
            return value;
        }

        // Даты и время (2026-09-24, 10:00:00) не маскируются: у них короткие группы и есть разделители дат.
        // Цифры после даты в той же последовательности (например, номер счёта через пробел) маскируются отдельно.
        var date = DatePrefix.Match(value);
        if (date.Success)
        {
            return date.Value + DigitRun.Replace(value.Substring(date.Length), MaskDigits);
        }

        var keep = digitCount >= 12 ? 4 : 2;
        var builder = new StringBuilder(value.Length);
        var seen = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                seen++;
                builder.Append(seen > digitCount - keep ? ch : '*');
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
