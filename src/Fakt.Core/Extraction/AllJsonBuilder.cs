using System.Globalization;
using System.Linq;
using Fakt.Core.Records;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Core.Extraction;

/// <summary>
/// Формирование JSON для поля [ALL]: дополнительные факты, происхождение основных полей, неразрешённые
/// значения и технический контекст. Ключ API и полный исходный файл сюда не попадают; основные поля
/// (ФИО, дата и место рождения) хранятся в отдельных столбцах и не дублируются.
/// </summary>
public static class AllJsonBuilder
{
    public const int SchemaVersion = 1;

    public static string Build(ObservationDraft draft, SourceRecord record, ExtractionContext context)
    {
        var facts = new JArray();
        foreach (var fact in draft.Facts)
        {
            var item = new JObject { ["type"] = fact.Type };
            if (fact.Label != null)
            {
                item["label"] = fact.Label;
            }

            item["value"] = fact.Value;
            if (fact.NormalizedValue != null)
            {
                item["normalized_value"] = fact.NormalizedValue;
            }

            item["source_column"] = fact.SourceColumn;
            if (fact.Evidence != null)
            {
                item["evidence"] = fact.Evidence;
            }

            facts.Add(item);
        }

        var root = new JObject
        {
            ["schema_version"] = SchemaVersion,
            ["kind"] = draft.Kind,
            ["identity_status"] = draft.IdentityStatus,
            ["facts"] = facts,
        };

        if (draft.FieldProvenance.Count > 0)
        {
            var provenance = new JObject();
            foreach (var field in MainFields.All)
            {
                if (draft.FieldProvenance.TryGetValue(field, out var source))
                {
                    provenance[field] = new JObject { ["source_column"] = source.SourceColumn, ["evidence"] = source.Evidence };
                }
            }

            root["field_provenance"] = provenance;
        }

        root["unresolved_fields"] = new JArray(draft.Unresolved.Select(u => new JObject
        {
            ["field"] = u.Field,
            ["raw_value"] = u.RawValue,
            ["reason"] = u.Reason,
            ["source_column"] = u.SourceColumn,
        }));

        root["provenance"] = new JObject
        {
            ["source_row_id"] = record.SourceRowId,
            ["source_line"] = record.Line,
            ["person_index"] = draft.PersonIndex,
            ["row_hash"] = record.Hash == null ? null : "sha256:" + record.Hash,
            ["provider"] = context.ProviderId,
            ["model"] = context.ModelId,
            ["prompt_version"] = context.PromptVersion,
            ["extraction_version"] = context.ExtractionVersion,
            ["job_id"] = context.JobId,
            ["processed_at_utc"] = context.ProcessedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };

        root["warnings"] = new JArray(draft.Warnings.Distinct());

        if (draft.Rejected.Count > 0)
        {
            root["rejected_candidates"] = new JArray(draft.Rejected.Select(r => new JObject
            {
                ["kind"] = r.Kind,
                ["name"] = r.Name,
                ["value"] = r.Value,
                ["reason"] = r.Reason,
            }));
        }

        return root.ToString(Formatting.None);
    }
}
