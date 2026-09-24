using System.Collections.Generic;
using Newtonsoft.Json;

namespace Fakt.Core.Extraction;

// Формат ответа модели на запрос извлечения (задание, раздел 9). Модель возвращает кандидатов;
// приложение проверяет типы, длины, даты и соответствие фрагментов исходной записи.

public sealed class ExtractionResponseWire
{
    [JsonProperty("rows")] public List<RowWire> Rows { get; set; }
}

public sealed class RowWire
{
    [JsonProperty("source_row_id")] public string SourceRowId { get; set; }
    [JsonProperty("status")] public string Status { get; set; }
    [JsonProperty("persons")] public List<PersonWire> Persons { get; set; }
    [JsonProperty("unassigned_facts")] public List<FactWire> UnassignedFacts { get; set; }
}

public sealed class PersonWire
{
    [JsonProperty("person_index")] public int? PersonIndex { get; set; }
    [JsonProperty("surname")] public string Surname { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("patronymic")] public string Patronymic { get; set; }
    [JsonProperty("birth_date")] public string BirthDate { get; set; }
    [JsonProperty("birth_place")] public string BirthPlace { get; set; }
    [JsonProperty("identity_status")] public string IdentityStatus { get; set; }
    [JsonProperty("field_sources")] public List<FieldSourceWire> FieldSources { get; set; }
    [JsonProperty("facts")] public List<FactWire> Facts { get; set; }
    [JsonProperty("unresolved_fields")] public List<UnresolvedWire> UnresolvedFields { get; set; }
    [JsonProperty("warnings")] public List<string> Warnings { get; set; }
}

public sealed class FactWire
{
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("label")] public string Label { get; set; }
    [JsonProperty("value")] public string Value { get; set; }
    [JsonProperty("normalized_value")] public string NormalizedValue { get; set; }
    [JsonProperty("source_column")] public string SourceColumn { get; set; }
    [JsonProperty("evidence")] public string Evidence { get; set; }
}

public sealed class FieldSourceWire
{
    [JsonProperty("field")] public string Field { get; set; }
    [JsonProperty("source_column")] public string SourceColumn { get; set; }
    [JsonProperty("evidence")] public string Evidence { get; set; }
}

public sealed class UnresolvedWire
{
    [JsonProperty("field")] public string Field { get; set; }
    [JsonProperty("raw_value")] public string RawValue { get; set; }
    [JsonProperty("reason")] public string Reason { get; set; }
    [JsonProperty("source_column")] public string SourceColumn { get; set; }
}

public static class MainFields
{
    public const string Surname = "surname";
    public const string Name = "name";
    public const string Patronymic = "patronymic";
    public const string BirthDate = "birth_date";
    public const string BirthPlace = "birth_place";

    public static readonly IReadOnlyList<string> All = new[] { Surname, Name, Patronymic, BirthDate, BirthPlace };

    public static string Title(string field)
    {
        switch (field)
        {
            case Surname: return "Фамилия";
            case Name: return "Имя";
            case Patronymic: return "Отчество";
            case BirthDate: return "Дата рождения";
            case BirthPlace: return "Место рождения";
            default: return field;
        }
    }
}

public static class IdentityStatuses
{
    public const string Identified = "identified";
    public const string Partial = "partial";
    public const string Unresolved = "unresolved";

    public static string Title(string status)
    {
        switch (status)
        {
            case Identified: return "ФИО установлено";
            case Partial: return "ФИО частично";
            case Unresolved: return "Лицо не установлено";
            default: return status ?? "—";
        }
    }
}
