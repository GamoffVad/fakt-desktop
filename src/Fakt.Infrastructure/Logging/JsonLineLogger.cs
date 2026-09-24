using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Fakt.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Logging;

/// <summary>
/// Структурированный журнал JSON Lines: %LOCALAPPDATA%\FAKT\logs\fakt-ГГГГММДД.jsonl.
/// Сообщения очищаются от секретов и персональных данных; содержимое записей и полные запросы к LLM
/// по умолчанию не пишутся. Подробный режим пишется в отдельный файл с коротким сроком хранения.
/// </summary>
public sealed class JsonLineLogger : IAppLogger, IDisposable
{
    private const int RecentCapacity = 2000;
    private readonly string _directory;
    private readonly Func<bool> _isVerbose;
    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _recent = new();
    private StreamWriter _writer;
    private string _currentFile;

    public JsonLineLogger(string directory, Func<bool> isVerbose)
    {
        _directory = directory;
        _isVerbose = isVerbose ?? (() => false);
    }

    public event Action<LogEntry> EntryWritten;

    public string Directory => _directory;

    public bool IsVerbose => _isVerbose();

    public void Write(LogEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        entry.Message = LogSanitizer.Sanitize(entry.Message);
        if (entry.Data != null)
        {
            foreach (var key in entry.Data.Keys.ToList())
            {
                if (entry.Data[key] is string text)
                {
                    entry.Data[key] = LogSanitizer.Sanitize(text);
                }
            }
        }

        var line = Serialize(entry);
        lock (_gate)
        {
            try
            {
                var fileName = (entry.Level == LogLevel.Debug ? "fakt-verbose-" : "fakt-") +
                               entry.TimestampUtc.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl";
                var path = Path.Combine(_directory, fileName);
                if (_writer == null || !string.Equals(_currentFile, path, StringComparison.OrdinalIgnoreCase))
                {
                    _writer?.Dispose();
                    System.IO.Directory.CreateDirectory(_directory);
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _currentFile = path;
                }

                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Журнал не должен останавливать обработку; запись остаётся в памяти.
            }
            catch (UnauthorizedAccessException)
            {
            }

            _recent.AddLast(entry);
            while (_recent.Count > RecentCapacity)
            {
                _recent.RemoveFirst();
            }
        }

        EntryWritten?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_gate)
        {
            return _recent.ToList();
        }
    }

    /// <summary>Удаление файлов журнала старше срока хранения (подробный журнал — отдельный срок).</summary>
    public int ApplyRetention(int retentionDays, int verboseRetentionDays)
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return 0;
        }

        var removed = 0;
        var now = DateTime.Now;
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "fakt-*.jsonl"))
        {
            var verbose = Path.GetFileName(file).StartsWith("fakt-verbose-", StringComparison.OrdinalIgnoreCase);
            var limit = verbose ? verboseRetentionDays : retentionDays;
            try
            {
                if (limit > 0 && File.GetLastWriteTime(file) < now.AddDays(-limit) && !string.Equals(file, _currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    public static string Serialize(LogEntry entry)
    {
        var obj = new JObject
        {
            ["ts"] = entry.TimestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["level"] = entry.Level.ToString().ToLowerInvariant(),
            ["event"] = entry.Event,
            ["message"] = entry.Message,
        };
        if (entry.Category != ErrorCategory.None) obj["category"] = entry.Category.ToString();
        if (entry.JobId.HasValue) obj["job_id"] = entry.JobId;
        if (entry.FileId.HasValue) obj["file_id"] = entry.FileId;
        if (entry.SourceRowId != null) obj["source_row_id"] = entry.SourceRowId;
        if (entry.Stage != null) obj["stage"] = entry.Stage;
        if (entry.DurationMs.HasValue) obj["duration_ms"] = entry.DurationMs;
        if (entry.Count.HasValue) obj["count"] = entry.Count;
        if (entry.ErrorCode != null) obj["error_code"] = entry.ErrorCode;
        if (entry.ProviderRequestId != null) obj["provider_request_id"] = entry.ProviderRequestId;
        if (entry.User != null) obj["user"] = entry.User;
        if (entry.Data != null && entry.Data.Count > 0) obj["data"] = JObject.FromObject(entry.Data);
        return obj.ToString(Formatting.None);
    }

    public static LogEntry Parse(string line)
    {
        // Без автоматического разбора дат: иначе «ts» становится DateTime и при обратном приведении к строке
        // теряет миллисекунды (порядок записей одной секунды на странице «Журнал» нарушается).
        JObject obj;
        using (var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None })
        {
            obj = JObject.Load(reader);
            while (reader.Read())
            {
                // Как JObject.Parse: любое содержимое после объекта, кроме комментария, вызывает исключение читателя.
            }
        }

        var entry = new LogEntry
        {
            TimestampUtc = DateTime.Parse((string)obj["ts"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            Level = Enum.TryParse<LogLevel>((string)obj["level"], true, out var level) ? level : LogLevel.Info,
            Event = (string)obj["event"],
            Message = (string)obj["message"],
            JobId = (long?)obj["job_id"],
            FileId = (long?)obj["file_id"],
            SourceRowId = (string)obj["source_row_id"],
            Stage = (string)obj["stage"],
            DurationMs = (long?)obj["duration_ms"],
            Count = (long?)obj["count"],
            ErrorCode = (string)obj["error_code"],
            ProviderRequestId = (string)obj["provider_request_id"],
            User = (string)obj["user"],
        };
        if (Enum.TryParse<ErrorCategory>((string)obj["category"], true, out var category))
        {
            entry.Category = category;
        }

        if (obj["data"] is JObject data)
        {
            entry.Data = data.Properties().ToDictionary(p => p.Name, p => (object)p.Value.ToString(Formatting.None).Trim('"'));
        }

        return entry;
    }

    /// <summary>Чтение последних записей из файлов журнала (для страницы «Журнал»).</summary>
    public IReadOnlyList<LogEntry> ReadLatest(int maxEntries, bool includeVerbose)
    {
        var result = new List<LogEntry>();
        if (!System.IO.Directory.Exists(_directory))
        {
            return result;
        }

        var files = System.IO.Directory.EnumerateFiles(_directory, "fakt-*.jsonl")
            .Where(f => includeVerbose || !Path.GetFileName(f).StartsWith("fakt-verbose-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(10);
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        result.Add(Parse(line));
                    }
                    catch (JsonException)
                    {
                    }
                    catch (FormatException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }

            if (result.Count >= maxEntries * 2)
            {
                break;
            }
        }

        return result.OrderByDescending(e => e.TimestampUtc).Take(maxEntries).ToList();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
