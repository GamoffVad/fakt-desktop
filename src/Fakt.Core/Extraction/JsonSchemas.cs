using System.Linq;
using Newtonsoft.Json.Linq;

namespace Fakt.Core.Extraction;

/// <summary>
/// JSON Schema ответов модели. Схемы строятся без $ref, все свойства перечислены в required, лишние
/// свойства запрещены, необязательность выражена через null — это совместимо со strict-режимами
/// OpenAI-подобных API и упрощает преобразование для Gemini и локальных серверов.
/// </summary>
public static class JsonSchemas
{
    public const string StructureSchemaName = "fakt_structure_v1";
    public const string ExtractionSchemaName = "fakt_facts_v1";

    public static JObject Structure()
    {
        return Obj(
            ("classification", Enum(false, "structured", "unstructured", "insufficient_sample")),
            ("format", Enum(true, "delimited", "fixed_width", "xml", "jsonl", "json_array")),
            ("encoding", Str(true)),
            ("has_header", Type("boolean", true)),
            ("header_row", Type("integer", true)),
            ("skip_rows", Type("integer", true)),
            ("delimiter", Str(true)),
            ("quote_char", Str(true)),
            ("escape_char", Str(true)),
            ("columns", Arr(Str(false), true)),
            ("fixed_widths", Arr(Type("integer", false), true)),
            ("xml_record_path", Str(true)),
            ("xml_namespaces", Arr(Obj(("prefix", Str(false)), ("uri", Str(false))), true)),
            ("json_record_path", Str(true)),
            ("confidence", Type("number", true)),
            ("reason", Str(false)));
    }

    public static JObject Extraction()
    {
        var fieldEnum = MainFields.All.ToArray();
        JObject Fact() => Obj(
            ("type", Enum(false, FactTypes.All.ToArray())),
            ("label", Str(true)),
            ("value", Str(false)),
            ("normalized_value", Str(true)),
            ("source_column", Str(true)),
            ("evidence", Str(false)));

        var fieldSource = Obj(("field", Enum(false, fieldEnum)), ("source_column", Str(true)), ("evidence", Str(false)));
        var unresolved = Obj(("field", Enum(false, fieldEnum)), ("raw_value", Str(false)), ("reason", Str(false)), ("source_column", Str(true)));
        var person = Obj(
            ("person_index", Type("integer", false)),
            ("surname", Str(true)),
            ("name", Str(true)),
            ("patronymic", Str(true)),
            ("birth_date", Str(true)),
            ("birth_place", Str(true)),
            ("identity_status", Enum(false, IdentityStatuses.Identified, IdentityStatuses.Partial, IdentityStatuses.Unresolved)),
            ("field_sources", Arr(fieldSource, false)),
            ("facts", Arr(Fact(), false)),
            ("unresolved_fields", Arr(unresolved, false)),
            ("warnings", Arr(Str(false), false)));
        var row = Obj(
            ("source_row_id", Str(false)),
            ("status", Enum(false, "extracted", "no_facts")),
            ("persons", Arr(person, false)),
            ("unassigned_facts", Arr(Fact(), false)));
        return Obj(("rows", Arr(row, false)));
    }

    private static JObject Obj(params (string Name, JObject Schema)[] properties)
    {
        var props = new JObject();
        foreach (var property in properties)
        {
            props[property.Name] = property.Schema;
        }

        return new JObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JArray(properties.Select(p => p.Name)),
            ["additionalProperties"] = false,
        };
    }

    private static JObject Str(bool nullable) => Type("string", nullable);

    private static JObject Type(string type, bool nullable) =>
        new() { ["type"] = nullable ? new JArray(type, "null") : (JToken)type };

    private static JObject Arr(JObject items, bool nullable) =>
        new() { ["type"] = nullable ? new JArray("array", "null") : (JToken)"array", ["items"] = items };

    private static JObject Enum(bool nullable, params string[] values)
    {
        var list = new JArray(values);
        if (nullable)
        {
            list.Add(JValue.CreateNull());
        }

        return new JObject
        {
            ["type"] = nullable ? new JArray("string", "null") : (JToken)"string",
            ["enum"] = list,
        };
    }
}
