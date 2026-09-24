using System;

namespace Fakt.Core.Files;

/// <summary>Статус файла в таблице страницы «Обработка» (задание, раздел 6).</summary>
public enum FileStatus
{
    Discovered,
    Queued,
    AnalyzingStructure,
    Tabular,
    NotTabular,
    NeedsConfiguration,
    Processing,
    Paused,
    Completed,
    CompletedWithErrors,
    Error,
    Cancelled,
    NotSupported,
}

/// <summary>Визуальная категория статуса; цвет всегда дополняет текст, а не заменяет его.</summary>
public enum StatusTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Error,
}

public static class FileStatusText
{
    public static string ToText(this FileStatus status)
    {
        switch (status)
        {
            case FileStatus.Discovered: return "Обнаружен";
            case FileStatus.Queued: return "В очереди";
            case FileStatus.AnalyzingStructure: return "Анализ структуры";
            case FileStatus.Tabular: return "Табличный";
            case FileStatus.NotTabular: return "Не табличный";
            case FileStatus.NeedsConfiguration: return "Требует настройки";
            case FileStatus.Processing: return "Обработка";
            case FileStatus.Paused: return "На паузе";
            case FileStatus.Completed: return "Завершён";
            case FileStatus.CompletedWithErrors: return "Завершён с ошибками";
            case FileStatus.Error: return "Ошибка";
            case FileStatus.Cancelled: return "Отменён";
            case FileStatus.NotSupported: return "Не поддерживается";
            default: throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }

    public static StatusTone ToTone(this FileStatus status)
    {
        switch (status)
        {
            case FileStatus.Tabular:
            case FileStatus.Completed:
                return StatusTone.Success;
            case FileStatus.NotTabular:
            case FileStatus.NeedsConfiguration:
            case FileStatus.Paused:
            case FileStatus.CompletedWithErrors:
            case FileStatus.NotSupported:
            case FileStatus.Cancelled:
                return StatusTone.Warning;
            case FileStatus.Error:
                return StatusTone.Error;
            case FileStatus.Queued:
            case FileStatus.AnalyzingStructure:
            case FileStatus.Processing:
                return StatusTone.Info;
            default:
                return StatusTone.Neutral;
        }
    }

    /// <summary>Статусы, при которых файл можно отметить для этапа «Обработка».</summary>
    public static bool IsSelectable(this FileStatus status)
    {
        switch (status)
        {
            case FileStatus.Discovered:
            case FileStatus.Tabular:
            case FileStatus.Completed:
            case FileStatus.CompletedWithErrors:
            case FileStatus.Error:
            case FileStatus.Cancelled:
            case FileStatus.Paused:
                return true;
            default:
                return false;
        }
    }

    public static bool IsBusy(this FileStatus status) =>
        status == FileStatus.Queued || status == FileStatus.AnalyzingStructure || status == FileStatus.Processing;
}
