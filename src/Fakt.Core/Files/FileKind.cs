using System;
using System.Collections.Generic;

namespace Fakt.Core.Files;

/// <summary>
/// Предварительная оценка вида файла по расширению. Расширение — только подсказка:
/// окончательное решение о табличности принимается по содержимому (образец + модель + парсер).
/// </summary>
public enum FileKindHint
{
    /// <summary>Текстовый кандидат с известным табличным расширением (csv, tsv, txt, xml, jsonl...).</summary>
    TextCandidate,
    /// <summary>Расширение неизвестно; содержимое будет проверено по сигнатуре при анализе.</summary>
    Unknown,
    /// <summary>Формат требует отдельного адаптера, в версии 1 не подключён (xlsx, xls, ods, docx...).</summary>
    AdapterRequired,
    /// <summary>Двоичный формат, модели не отправляется (pdf, изображения, архивы, исполняемые файлы).</summary>
    Binary,
}

public static class FileKindClassifier
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".tab", ".txt", ".dat", ".prn", ".psv", ".dsv", ".xml", ".jsonl", ".ndjson", ".json", ".log", ".asc", ".lst", ".out", ".list",
    };

    private static readonly Dictionary<string, string> AdapterRequired = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xlsx"] = "XLSX: адаптер электронных таблиц не подключён в версии 1",
        [".xlsm"] = "XLSM: адаптер электронных таблиц не подключён в версии 1",
        [".xls"] = "XLS: адаптер электронных таблиц не подключён в версии 1",
        [".ods"] = "ODS: адаптер электронных таблиц не подключён в версии 1",
        [".docx"] = "DOCX: документ, не табличный текстовый формат",
        [".doc"] = "DOC: двоичный документ",
        [".rtf"] = "RTF: форматированный документ, адаптер не подключён",
        [".odt"] = "ODT: документ, адаптер не подключён",
        [".mdb"] = "MDB: база данных Access, адаптер не подключён",
        [".accdb"] = "ACCDB: база данных Access, адаптер не подключён",
        [".dbf"] = "DBF: двоичная таблица, адаптер не подключён",
    };

    private static readonly Dictionary<string, string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "PDF не отправляется модели как текст",
        [".png"] = "Изображение", [".jpg"] = "Изображение", [".jpeg"] = "Изображение", [".gif"] = "Изображение",
        [".bmp"] = "Изображение", [".tif"] = "Изображение", [".tiff"] = "Изображение", [".webp"] = "Изображение",
        [".heic"] = "Изображение", [".ico"] = "Изображение", [".svgz"] = "Сжатое изображение",
        [".zip"] = "Архив", [".rar"] = "Архив", [".7z"] = "Архив", [".gz"] = "Архив", [".tgz"] = "Архив",
        [".bz2"] = "Архив", [".xz"] = "Архив", [".tar"] = "Архив", [".cab"] = "Архив", [".iso"] = "Образ диска",
        [".exe"] = "Исполняемый файл", [".dll"] = "Исполняемый файл", [".sys"] = "Исполняемый файл",
        [".msi"] = "Установочный пакет", [".bin"] = "Двоичный файл", [".dmp"] = "Двоичный дамп",
        [".mp3"] = "Аудио", [".wav"] = "Аудио", [".flac"] = "Аудио", [".ogg"] = "Аудио", [".m4a"] = "Аудио",
        [".mp4"] = "Видео", [".avi"] = "Видео", [".mkv"] = "Видео", [".mov"] = "Видео", [".wmv"] = "Видео",
        [".sqlite"] = "База данных SQLite", [".db"] = "Двоичная база данных", [".mdf"] = "Файл базы SQL Server",
        [".ldf"] = "Журнал SQL Server", [".bak"] = "Резервная копия", [".pst"] = "Почтовый архив",
    };

    public static FileKindHint Classify(string extension, out string reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(extension))
        {
            return FileKindHint.Unknown;
        }

        if (TextExtensions.Contains(extension))
        {
            return FileKindHint.TextCandidate;
        }

        if (AdapterRequired.TryGetValue(extension, out var adapterReason))
        {
            reason = adapterReason;
            return FileKindHint.AdapterRequired;
        }

        if (BinaryExtensions.TryGetValue(extension, out var binaryReason))
        {
            reason = binaryReason + ": не поддерживается";
            return FileKindHint.Binary;
        }

        return FileKindHint.Unknown;
    }
}
