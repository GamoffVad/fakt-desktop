using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Fakt.Core.Extraction;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Sql;

/// <summary>Строка поисковой проекции для одного наблюдения.</summary>
public sealed class SearchDocument
{
    public string AllText { get; set; }
    public string NameText { get; set; }
    public string BirthPlaceText { get; set; }
    public string FactsText { get; set; }
    public string FileText { get; set; }
    public List<KeyValuePair<string, string>> Values { get; } = new();
}

/// <summary>
/// Поисковая проекция объединяет в одном документе основные поля, значения фактов из [ALL] (разобранного
/// как JSON, без технических ключей) и данные файла — запрос «фамилия имя_файла» находит одно наблюдение.
/// Проекция восстанавливаема: её можно перестроить из двух основных таблиц.
/// </summary>
public static class SearchDocumentBuilder
{
    private static readonly Regex NonWord = new(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant);

    public static string FileText(string fileName, string fileCode)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            parts.Add(fileName.Trim());
            var tokens = NonWord.Replace(fileName, " ").Trim();
            if (tokens.Length > 0 && tokens != fileName.Trim())
            {
                parts.Add(tokens);
            }
        }

        if (!string.IsNullOrWhiteSpace(fileCode))
        {
            parts.Add(fileCode.Trim());
            var digits = fileCode.Trim().StartsWith("T_", StringComparison.Ordinal) ? fileCode.Trim().Substring(2) : null;
            if (!string.IsNullOrEmpty(digits))
            {
                parts.Add(digits);
            }
        }

        return Truncate(string.Join(" ", parts), 2000);
    }

    public static SearchDocument FromDraft(ObservationDraft draft, string fileText)
    {
        var document = new SearchDocument
        {
            NameText = Truncate(Join(draft.Surname, draft.Name, draft.Patronymic), 1000),
            BirthPlaceText = Truncate(draft.BirthPlace, 2000),
            FactsText = FactsText(draft.Facts.Select(f => (f.Value, f.NormalizedValue, f.Label)).Concat(draft.Unresolved.Select(u => (u.RawValue, (string)null, (string)null)))),
            FileText = fileText,
        };
        document.AllText = Join(draft.BuildSearchText(), fileText);
        document.Values.AddRange(draft.SearchValues());
        return document;
    }

    /// <summary>Перестроение проекции из сохранённых данных (без модели): основные поля + разбор [ALL].</summary>
    public static SearchDocument FromStored(string surname, string name, string patronymic, DateTime? birthDate, string birthPlace,
        string allJson, string fileText)
    {
        var facts = new List<(string Value, string Normalized, string Label)>();
        var document = new SearchDocument { FileText = fileText };
        JObject root = null;
        try
        {
            root = string.IsNullOrWhiteSpace(allJson) ? null : JObject.Parse(allJson);
        }
        catch (JsonReaderException)
        {
        }

        foreach (var fact in (root?["facts"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var type = (string)fact["type"];
            var valueToken = fact["value"];
            var value = valueToken == null ? null : valueToken.Type == JTokenType.Object || valueToken.Type == JTokenType.Array
                ? string.Join(" ", valueToken.SelectTokens("..*").Where(t => t.Type == JTokenType.String).Select(t => (string)t))
                : valueToken.ToString();
            var normalized = (string)fact["normalized_value"];
            facts.Add((value, normalized, (string)fact["label"]));
            var key = FactNormalizer.SearchKey(type, value);
            if (key != null && FactTypes.IsKnown(type))
            {
                document.Values.Add(new KeyValuePair<string, string>(type, key));
            }
        }

        foreach (var unresolved in (root?["unresolved_fields"] as JArray ?? new JArray()).OfType<JObject>())
        {
            facts.Add(((string)unresolved["raw_value"], null, null));
        }

        document.NameText = Truncate(Join(surname, name, patronymic), 1000);
        document.BirthPlaceText = Truncate(birthPlace, 2000);
        document.FactsText = FactsText(facts);
        var dateText = birthDate.HasValue
            ? birthDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + birthDate.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : null;
        document.AllText = Join(surname, name, patronymic, dateText, birthPlace, document.FactsText, fileText);
        var distinct = document.Values.Distinct().ToList();
        document.Values.Clear();
        document.Values.AddRange(distinct);
        return document;
    }

    private static string FactsText(IEnumerable<(string Value, string Normalized, string Label)> facts)
    {
        var builder = new StringBuilder();
        foreach (var (value, normalized, label) in facts)
        {
            Append(builder, value);
            if (normalized != null && normalized != value)
            {
                Append(builder, normalized);
            }

            Append(builder, label);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void Append(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(value.Trim());
    }

    private static string Join(params string[] values) => string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));

    private static string Truncate(string value, int max)
    {
        // Проекция вспомогательная: ограничение длины отдельных полей не затрагивает исходные данные (полный текст — в AllText).
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= max ? value : value.Substring(0, max);
    }
}
