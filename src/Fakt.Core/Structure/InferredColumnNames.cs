using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fakt.Core.Extraction;

namespace Fakt.Core.Structure;

/// <summary>Исправление подобранного моделью имени колонки.</summary>
public sealed class ColumnRename
{
    public int Index { get; set; }
    public string OldName { get; set; }
    public string NewName { get; set; }
    public string Reason { get; set; }
}

/// <summary>
/// Сверка имён колонок, подобранных моделью для файла без заголовка, со значениями: если имя указывает на один
/// тип идентификатора, а не менее 80 % значений однозначно имеют формат другого (например, «Номер СНИЛС» у
/// 12-значных чисел — это ИНН), имя исправляется. Правила — те же, что у <see cref="FactTypeCorrection"/>.
/// Имена из заголовка файла не меняются.
/// </summary>
public static class InferredColumnNames
{
    public const double Threshold = 0.8;

    private static readonly (Regex Pattern, string Type)[] Hints =
    {
        (Word("снилс"), FactTypes.Snils),
        (Word("инн"), FactTypes.Inn),
        (new Regex(@"госномер|гос\.?\s*номер|номерной\s+знак|\bгрз\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), FactTypes.LicensePlate),
        (Word("vin"), FactTypes.Vin),
        (new Regex(@"автомобил|транспорт|машин", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), FactTypes.Vehicle),
    };

    private static readonly Dictionary<string, string> CanonicalNames = new(StringComparer.Ordinal)
    {
        [FactTypes.Inn] = "ИНН",
        [FactTypes.Snils] = "СНИЛС",
        [FactTypes.LicensePlate] = "Госномер",
        [FactTypes.Vin] = "VIN",
    };

    public static List<ColumnRename> Reconcile(IList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var renames = new List<ColumnRename>();
        if (columns == null || rows == null || rows.Count == 0)
        {
            return renames;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var implied = Hints.Where(h => h.Pattern.IsMatch(columns[i] ?? string.Empty)).Select(h => h.Type).FirstOrDefault();
            if (implied == null)
            {
                continue;
            }

            var values = rows.Select(r => r != null && i < r.Count ? r[i] : null).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (values.Count < 2)
            {
                continue;
            }

            var majority = values.Select(v => FactTypeCorrection.Correct(implied, v, null)).GroupBy(t => t).OrderByDescending(g => g.Count()).First();
            if (majority.Key == implied || majority.Count() < Threshold * values.Count || !CanonicalNames.TryGetValue(majority.Key, out var name))
            {
                continue;
            }

            var unique = name;
            for (var n = 2; columns.Where((c, index) => index != i).Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
            {
                unique = name + " " + n;
            }

            renames.Add(new ColumnRename
            {
                Index = i,
                OldName = columns[i],
                NewName = unique,
                Reason = $"значения ({majority.Count()} из {values.Count}) имеют формат «{FactTypes.Title(majority.Key)}»",
            });
            columns[i] = unique;
        }

        return renames;
    }

    private static Regex Word(string word) => new(@"(?<![\p{L}\p{N}])" + word + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
