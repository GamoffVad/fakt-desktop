using System.Collections.Generic;
using Newtonsoft.Json;

namespace Fakt.Core.Structure;

/// <summary>
/// Декларативное описание структуры файла — контракт ответа модели на запрос определения структуры
/// (задание, раздел 7) и параметры разбора для worker. Модель не генерирует исполняемый код: только
/// значения из разрешённого набора, которые проверяет <see cref="StructureContractValidator"/>.
/// </summary>
public sealed class StructureDescriptor
{
    public const string ClassStructured = "structured";
    public const string ClassUnstructured = "unstructured";
    public const string ClassInsufficientSample = "insufficient_sample";

    public const string FormatDelimited = "delimited";
    public const string FormatFixedWidth = "fixed_width";
    public const string FormatXml = "xml";
    public const string FormatJsonl = "jsonl";
    public const string FormatJsonArray = "json_array";

    [JsonProperty("classification")]
    public string Classification { get; set; }

    [JsonProperty("format")]
    public string Format { get; set; }

    [JsonProperty("encoding")]
    public string Encoding { get; set; }

    [JsonProperty("has_header")]
    public bool? HasHeader { get; set; }

    [JsonProperty("header_row")]
    public int? HeaderRow { get; set; }

    [JsonProperty("skip_rows")]
    public int? SkipRows { get; set; }

    [JsonProperty("delimiter")]
    public string Delimiter { get; set; }

    [JsonProperty("quote_char")]
    public string QuoteChar { get; set; }

    [JsonProperty("escape_char")]
    public string EscapeChar { get; set; }

    [JsonProperty("columns")]
    public List<string> Columns { get; set; }

    [JsonProperty("fixed_widths")]
    public List<int> FixedWidths { get; set; }

    [JsonProperty("xml_record_path")]
    public string XmlRecordPath { get; set; }

    [JsonProperty("xml_namespaces")]
    public Dictionary<string, string> XmlNamespaces { get; set; }

    [JsonProperty("json_record_path")]
    public string JsonRecordPath { get; set; }

    [JsonProperty("confidence")]
    public double? Confidence { get; set; }

    [JsonProperty("reason")]
    public string Reason { get; set; }

    /// <summary>
    /// Табличный текст без строки заголовка: имена колонок подобраны моделью по значениям (или сгенерированы),
    /// а не взяты из файла.
    /// </summary>
    [JsonIgnore]
    public bool HasInferredColumnNames => (Format == FormatDelimited || Format == FormatFixedWidth) && HasHeader != true;

    public StructureDescriptor Clone()
    {
        return JsonConvert.DeserializeObject<StructureDescriptor>(JsonConvert.SerializeObject(this));
    }

    /// <summary>Краткое описание для колонки «Определённый формат и структура».</summary>
    public string Describe()
    {
        if (Classification != ClassStructured)
        {
            return Classification == ClassInsufficientSample ? "Недостаточно образца" : "Нет табличной структуры";
        }

        var columns = Columns?.Count ?? 0;
        switch (Format)
        {
            case FormatDelimited:
                return $"Разделитель {DelimiterName(Delimiter)}, {ColumnsText(columns)}{(HasHeader == true ? ", заголовок" : ", без заголовка")}";
            case FormatFixedWidth:
                return $"Фиксированная ширина, {ColumnsText(FixedWidths?.Count ?? columns)}";
            case FormatXml:
                return $"XML, записи {XmlRecordPath}";
            case FormatJsonl:
                return "JSON Lines";
            case FormatJsonArray:
                return "JSON-массив";
            default:
                return Format ?? "—";
        }
    }

    public static string DelimiterName(string delimiter)
    {
        switch (delimiter)
        {
            case "\t": return "«табуляция»";
            case " ": return "«пробел»";
            case null: return "—";
            default: return "«" + delimiter + "»";
        }
    }

    private static string ColumnsText(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;
        string word;
        if (mod10 == 1 && mod100 != 11) word = "столбец";
        else if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) word = "столбца";
        else word = "столбцов";
        return count + " " + word;
    }
}
