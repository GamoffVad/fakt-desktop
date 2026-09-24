using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fakt.Core.Structure;

public sealed class StructureCheckResult
{
    public StructureCheckResult(StructureDescriptor normalized, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        Normalized = normalized;
        Errors = errors;
        Warnings = warnings;
    }

    /// <summary>Нормализованная структура (синонимы разделителей и кодировок приведены к каноническому виду).</summary>
    public StructureDescriptor Normalized { get; }

    /// <summary>Блокирующие нарушения контракта: чтение запрещено до исправления.</summary>
    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Проверка ответа модели по контракту определения структуры: допустимые значения, типы и согласованность.
/// Уверенность модели (confidence) — самооценка и не влияет на допуск; окончательно структуру подтверждает
/// парсер worker на ограниченном фрагменте.
/// </summary>
public static class StructureContractValidator
{
    public const int MaxColumns = 2000;
    public const int MaxColumnNameLength = 256;
    public const int MaxSkipRows = 1000;
    public const int MaxFixedWidth = 100000;

    // Имена XML могут начинаться с любой буквы Unicode («/Файл/Документ» в выгрузках на русском языке) или «_».
    private static readonly Regex XmlPathPattern = new(@"^/?([\p{L}_][\w\-.]*:)?[\p{L}_][\w\-.]*(/([\p{L}_][\w\-.]*:)?[\p{L}_][\w\-.]*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex NcNamePattern = new(@"^[\p{L}_][\w\-.]*$", RegexOptions.CultureInvariant);

    public static StructureCheckResult Validate(StructureDescriptor input, string detectedEncoding)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (input == null)
        {
            errors.Add("Ответ модели не содержит структуры.");
            return new StructureCheckResult(null, errors, warnings);
        }

        var s = input.Clone();
        s.Classification = s.Classification?.Trim().ToLowerInvariant();
        if (s.Classification != StructureDescriptor.ClassStructured &&
            s.Classification != StructureDescriptor.ClassUnstructured &&
            s.Classification != StructureDescriptor.ClassInsufficientSample)
        {
            errors.Add($"Недопустимое значение classification: «{input.Classification}». Допустимо: structured, unstructured, insufficient_sample.");
            return new StructureCheckResult(s, errors, warnings);
        }

        if (s.Confidence.HasValue && (double.IsNaN(s.Confidence.Value) || s.Confidence < 0 || s.Confidence > 1))
        {
            warnings.Add("Значение confidence вне диапазона 0..1 проигнорировано.");
            s.Confidence = null;
        }

        if (s.Reason != null && s.Reason.Length > 2000)
        {
            s.Reason = s.Reason.Substring(0, 2000) + "…";
        }

        NormalizeEncoding(s, detectedEncoding, warnings);

        if (s.Classification != StructureDescriptor.ClassStructured)
        {
            return new StructureCheckResult(s, errors, warnings);
        }

        s.Format = s.Format?.Trim().ToLowerInvariant();
        switch (s.Format)
        {
            case StructureDescriptor.FormatDelimited:
                ValidateDelimited(s, errors, warnings);
                break;
            case StructureDescriptor.FormatFixedWidth:
                ValidateFixedWidth(s, errors, warnings);
                break;
            case StructureDescriptor.FormatXml:
                ValidateXml(s, errors);
                break;
            case StructureDescriptor.FormatJsonl:
            case StructureDescriptor.FormatJsonArray:
                if (!string.IsNullOrWhiteSpace(s.JsonRecordPath))
                {
                    errors.Add($"json_record_path «{s.JsonRecordPath}» не поддерживается в версии 1: записи должны быть объектами верхнего уровня.");
                }

                s.JsonRecordPath = null;
                break;
            default:
                errors.Add($"Недопустимый формат «{input.Format}». Допустимо: delimited, fixed_width, xml, jsonl, json_array.");
                break;
        }

        return new StructureCheckResult(s, errors, warnings);
    }

    private static void NormalizeEncoding(StructureDescriptor s, string detectedEncoding, List<string> warnings)
    {
        var detected = EncodingNames.Normalize(detectedEncoding);
        var proposed = EncodingNames.Normalize(s.Encoding);
        if (!string.IsNullOrWhiteSpace(s.Encoding) && proposed == null)
        {
            warnings.Add($"Модель указала неподдерживаемую кодировку «{s.Encoding}»; используется определённая по файлу.");
        }

        if (detected != null && proposed != null && !SameFamily(detected, proposed))
        {
            warnings.Add($"Модель предположила кодировку {EncodingNames.DisplayName(proposed)}, по байтам файла определена {EncodingNames.DisplayName(detected)}. Используется определённая по файлу; при искажённом тексте задайте кодировку вручную.");
        }

        s.Encoding = detected ?? proposed;
    }

    private static bool SameFamily(string a, string b)
    {
        string Family(string e) => e == "utf-8-sig" || e == "ascii" ? "utf-8" : e;
        return Family(a) == Family(b);
    }

    public static string NormalizeDelimiter(string value)
    {
        if (value == null)
        {
            return null;
        }

        if (value.Length == 1)
        {
            return value;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "\\t":
            case "tab":
            case "tabulation":
            case "табуляция":
                return "\t";
            case "comma":
            case "запятая":
                return ",";
            case "semicolon":
            case "точка с запятой":
                return ";";
            case "pipe":
            case "vertical bar":
                return "|";
            case "space":
            case "пробел":
                return " ";
            default:
                return value.Trim().Length == 1 ? value.Trim() : value;
        }
    }

    private static void ValidateDelimited(StructureDescriptor s, List<string> errors, List<string> warnings)
    {
        s.Delimiter = NormalizeDelimiter(s.Delimiter);
        if (string.IsNullOrEmpty(s.Delimiter))
        {
            errors.Add("Для формата delimited не указан разделитель.");
        }
        else if (s.Delimiter.Length != 1)
        {
            errors.Add($"Разделитель «{s.Delimiter}» должен быть одним символом; многосимвольные разделители в версии 1 не поддерживаются.");
        }
        else if (s.Delimiter == "\r" || s.Delimiter == "\n")
        {
            errors.Add("Перевод строки не может быть разделителем полей.");
        }

        if (s.QuoteChar != null && s.QuoteChar.Length == 0)
        {
            s.QuoteChar = null;
        }

        if (s.QuoteChar != null && s.QuoteChar.Length != 1)
        {
            errors.Add($"Символ кавычки «{s.QuoteChar}» должен быть одним символом или null.");
        }

        if (s.EscapeChar != null && s.EscapeChar.Length == 0)
        {
            s.EscapeChar = null;
        }

        if (s.EscapeChar != null && s.EscapeChar.Length != 1)
        {
            errors.Add($"Escape-символ «{s.EscapeChar}» должен быть одним символом или null.");
        }

        if (s.QuoteChar != null && s.QuoteChar == s.Delimiter)
        {
            errors.Add("Символ кавычки совпадает с разделителем.");
        }

        if (s.EscapeChar != null && s.EscapeChar == s.Delimiter)
        {
            errors.Add("Escape-символ совпадает с разделителем.");
        }

        ValidateHeaderAndSkip(s, errors, warnings);
        ValidateColumns(s, errors, warnings, expectedCount: null);
    }

    private static void ValidateFixedWidth(StructureDescriptor s, List<string> errors, List<string> warnings)
    {
        ValidateHeaderAndSkip(s, errors, warnings);
        if (s.FixedWidths == null || s.FixedWidths.Count == 0)
        {
            errors.Add("Для фиксированной ширины не заданы ширины колонок (fixed_widths).");
            return;
        }

        if (s.FixedWidths.Count > MaxColumns)
        {
            errors.Add($"Слишком много колонок фиксированной ширины: {s.FixedWidths.Count} (предел {MaxColumns}).");
        }

        if (s.FixedWidths.Any(w => w <= 0 || w > MaxFixedWidth))
        {
            errors.Add($"Ширины колонок должны быть положительными и не больше {MaxFixedWidth} символов.");
        }

        ValidateColumns(s, errors, warnings, s.FixedWidths.Count);
    }

    private static void ValidateXml(StructureDescriptor s, List<string> errors)
    {
        s.XmlRecordPath = s.XmlRecordPath?.Trim();
        if (string.IsNullOrEmpty(s.XmlRecordPath))
        {
            errors.Add("Для XML не указан путь к повторяющимся записям (xml_record_path).");
            return;
        }

        if (s.XmlRecordPath.Length > 512 || !XmlPathPattern.IsMatch(s.XmlRecordPath))
        {
            errors.Add($"Путь XML «{s.XmlRecordPath}» недопустим: разрешены только имена элементов через «/», необязательные префиксы вида «ns:» и ведущая «/». Предикаты, атрибуты и шаблоны не поддерживаются.");
            return;
        }

        s.XmlNamespaces ??= new Dictionary<string, string>();
        foreach (var pair in s.XmlNamespaces)
        {
            if (!NcNamePattern.IsMatch(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 1024)
            {
                errors.Add($"Некорректное пространство имён XML: «{pair.Key}» = «{pair.Value}».");
            }
        }

        foreach (var step in s.XmlRecordPath.Trim('/').Split('/'))
        {
            var colon = step.IndexOf(':');
            if (colon > 0)
            {
                var prefix = step.Substring(0, colon);
                if (!s.XmlNamespaces.ContainsKey(prefix))
                {
                    errors.Add($"Префикс «{prefix}» в пути XML не объявлен в xml_namespaces.");
                }
            }
        }
    }

    private static void ValidateHeaderAndSkip(StructureDescriptor s, List<string> errors, List<string> warnings)
    {
        s.SkipRows ??= 0;
        if (s.SkipRows < 0 || s.SkipRows > MaxSkipRows)
        {
            errors.Add($"skip_rows = {s.SkipRows} вне допустимого диапазона 0..{MaxSkipRows}.");
            return;
        }

        if (s.HasHeader == null)
        {
            errors.Add("Не указано, есть ли строка заголовка (has_header).");
            return;
        }

        if (s.HasHeader == true)
        {
            if (s.HeaderRow == null)
            {
                s.HeaderRow = s.SkipRows;
                warnings.Add("header_row не указан; принят равным skip_rows.");
            }
            else if (s.HeaderRow != s.SkipRows)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture,
                    "Несогласованная структура: header_row = {0}, skip_rows = {1}. Заголовок должен быть первой строкой после пропущенной преамбулы.",
                    s.HeaderRow, s.SkipRows));
            }
        }
        else
        {
            if (s.HeaderRow != null)
            {
                warnings.Add("header_row указан при отсутствии заголовка и проигнорирован.");
            }

            s.HeaderRow = null;
        }
    }

    private static void ValidateColumns(StructureDescriptor s, List<string> errors, List<string> warnings, int? expectedCount)
    {
        if (s.Columns == null || s.Columns.Count == 0)
        {
            if (expectedCount.HasValue)
            {
                s.Columns = Enumerable.Range(1, expectedCount.Value).Select(i => "Колонка " + i).ToList();
                warnings.Add("Имена колонок не указаны; назначены «Колонка 1…N».");
                return;
            }

            errors.Add("Не указан список колонок.");
            return;
        }

        if (s.Columns.Count > MaxColumns)
        {
            errors.Add($"Слишком много колонок: {s.Columns.Count} (предел {MaxColumns}).");
            return;
        }

        if (expectedCount.HasValue && s.Columns.Count != expectedCount.Value)
        {
            errors.Add($"Число имён колонок ({s.Columns.Count}) не совпадает с числом ширин ({expectedCount.Value}).");
            return;
        }

        var normalized = new List<string>(s.Columns.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamed = false;
        for (var i = 0; i < s.Columns.Count; i++)
        {
            var name = (s.Columns[i] ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                name = "Колонка " + (i + 1);
                renamed = true;
            }

            if (name.Length > MaxColumnNameLength)
            {
                name = name.Substring(0, MaxColumnNameLength);
                warnings.Add($"Имя колонки {i + 1} длиннее {MaxColumnNameLength} символов и сокращено для отображения.");
            }

            // «X», «X (2)», «X (3)»… до первого свободного имени, как в worker (dedupe_columns): новое имя
            // не должно совпасть ни с исходным, ни с уже присвоенным (регистр не различается).
            var candidate = name;
            for (var number = 2; used.Contains(candidate); number++)
            {
                candidate = $"{name} ({number})";
                renamed = true;
            }

            used.Add(candidate);
            normalized.Add(candidate);
        }

        if (renamed)
        {
            warnings.Add("Пустые или повторяющиеся имена колонок переименованы.");
        }

        s.Columns = normalized;
    }
}
