using System.Collections.Generic;
using System.Linq;
using Fakt.Core.Files;
using Fakt.Core.Structure;
using Fakt.Desktop.Mvvm;

namespace Fakt.Desktop.ViewModels;

/// <summary>Ячейка предпросмотра: пустая строка, пробелы и NULL различаются.</summary>
public sealed class PreviewCell
{
    public PreviewCell(string value)
    {
        Value = value;
    }

    public string Value { get; }
    public bool IsNull => Value == null;
    public bool IsEmpty => Value != null && Value.Length == 0;
    public bool IsWhitespace => Value != null && Value.Length > 0 && Value.Trim().Length == 0;

    public string Text => IsNull ? "NULL" : IsEmpty ? "пусто" : IsWhitespace ? "␣ × " + Value.Length : Value;

    public string ToolTip => IsNull
        ? "NULL: значение отсутствует в источнике (нет поля, элемента или ключа)"
        : IsEmpty ? "Пустая строка: поле есть, но не содержит символов"
        : IsWhitespace ? "Строка только из пробелов" : Value.Length > 60 ? Value : null;

    public bool IsSpecial => IsNull || IsEmpty || IsWhitespace;
}

public sealed class PreviewRowViewModel
{
    public long Ordinal { get; set; }
    public long? Line { get; set; }
    public List<PreviewCell> Cells { get; set; }
    public string Error { get; set; }
}

/// <summary>Предпросмотр выбранного файла: ограниченный объём, не весь файл и не весь DataFrame.</summary>
public sealed class PreviewViewModel : ObservableObject
{
    public PreviewViewModel(FileItemViewModel file)
    {
        Title = "Структура: " + file.Name;
        var detection = file.Detection;
        var preview = detection?.Validation?.Preview;
        if (preview != null && preview.Rows.Count > 0)
        {
            Columns = preview.Columns.ToList();
            Rows = preview.Rows.Select(r => new PreviewRowViewModel
            {
                Ordinal = r.Ordinal,
                Line = r.Line,
                Cells = Columns.Select((_, i) => new PreviewCell(r.Values != null && i < r.Values.Count ? r.Values[i] : null)).ToList(),
                Error = r.Error?.Message,
            }).ToList();
            Subtitle = $"Показаны первые {Rows.Count} записей (предпросмотр ограничен и не заменяет полную проверку)";
        }
        else
        {
            Columns = new List<string>();
            Rows = new List<PreviewRowViewModel>();
            SampleLines = detection?.Sample?.Lines?.ToList() ?? new List<string>();
            Subtitle = detection == null
                ? file.Status == FileStatus.NotSupported ? "Файл не читается как текст: " + (file.Message ?? "формат не поддерживается") : "Структура ещё не определена — нажмите «Обработка»."
                : SampleLines.Count > 0 ? $"Первые {SampleLines.Count} строк образца (кодировка {EncodingNames.DisplayName(detection.Sample?.Encoding)})" : "Нет данных для предпросмотра";
        }

        var structure = detection?.Structure ?? detection?.ModelStructure;
        var parts = new List<string>();
        if (detection?.Validation != null)
        {
            parts.Add($"Проверено записей: {detection.Validation.RecordsChecked:N0}" + (detection.Validation.EofReached ? " (весь файл)" : string.Empty));
            if (detection.Validation.BadRecords > 0)
            {
                parts.Add($"с ошибками: {detection.Validation.BadRecords:N0}");
            }
        }

        if (Columns.Count > 0)
        {
            parts.Add($"Столбцов: {Columns.Count}");
        }

        if (detection?.Validation != null && !detection.Validation.EofReached)
        {
            parts.Add("Всего записей в файле: подсчитывается при обработке");
        }

        var encoding = structure?.Encoding ?? detection?.Sample?.Encoding;
        if (encoding != null)
        {
            parts.Add("Кодировка: " + EncodingNames.DisplayName(encoding) + (detection?.Sample?.EncodingSource == "detected" ? $" (определена, {detection.Sample.EncodingConfidence:P0})" : string.Empty));
        }

        if (structure?.Format == StructureDescriptor.FormatDelimited)
        {
            parts.Add("Разделитель: " + StructureDescriptor.DelimiterName(structure.Delimiter));
        }
        else if (structure?.Format == StructureDescriptor.FormatXml)
        {
            parts.Add("Путь записей: " + structure.XmlRecordPath);
        }
        else if (structure?.Format == StructureDescriptor.FormatFixedWidth && structure.FixedWidths != null)
        {
            parts.Add("Ширины: " + string.Join(", ", structure.FixedWidths));
        }

        if (detection?.Sample?.Truncated == true)
        {
            parts.Add("образец усечён лимитом объёма");
        }

        InfoText = string.Join("  |  ", parts);
        SuggestedColumns = detection?.SuggestedColumns == null ? null : string.Join(", ", detection.SuggestedColumns);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public List<string> Columns { get; }
    public List<PreviewRowViewModel> Rows { get; }
    public List<string> SampleLines { get; } = new();
    public string InfoText { get; }
    public string SuggestedColumns { get; }
    public bool HasTable => Rows.Count > 0;
    public bool HasSample => !HasTable && SampleLines.Count > 0;
}
