using System;
using System.Collections.Generic;
using System.Linq;

namespace Fakt.Core.Structure;

/// <summary>
/// Уточнение ширин колонок фиксированной ширины по образцу. Формат определяет модель; приложение лишь выравнивает
/// предложенные ею границы: каждая граница сдвигается к ближайшему началу колонки, найденному по вертикальной
/// полосе пробелов во всех строках образца, но не дальше <see cref="MaxShift"/> символов. Если выравнивание
/// неоднозначно, ширины модели остаются без изменений.
/// </summary>
public static class FixedWidthRefiner
{
    public const int MaxShift = 3;

    public static List<int> Refine(IReadOnlyList<int> widths, IEnumerable<string> sampleLines, out bool changed)
    {
        changed = false;
        if (widths == null || widths.Count < 2 || widths.Any(w => w <= 0))
        {
            return widths?.ToList();
        }

        var lines = (sampleLines ?? Enumerable.Empty<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.IndexOf('\t') < 0)
            .Select(l => l.TrimEnd('\r', '\n'))
            .ToList();
        if (lines.Count < 2)
        {
            return widths.ToList();
        }

        // Позиция «пустая», если во всех строках образца там пробел или строка уже закончилась.
        var length = lines.Max(l => l.Length);
        var blank = new bool[length];
        for (var i = 0; i < length; i++)
        {
            blank[i] = lines.All(l => i >= l.Length || l[i] == ' ');
        }

        var starts = new List<int>();
        for (var i = 1; i < length; i++)
        {
            if (!blank[i] && blank[i - 1])
            {
                starts.Add(i);
            }
        }

        var boundaries = new List<int>();
        var position = 0;
        foreach (var width in widths.Take(widths.Count - 1))
        {
            position += width;
            boundaries.Add(position);
        }

        var snapped = new List<int>();
        foreach (var boundary in boundaries)
        {
            var candidates = starts.Where(s => Math.Abs(s - boundary) <= MaxShift).OrderBy(s => Math.Abs(s - boundary)).ToList();
            if (candidates.Count > 1 && Math.Abs(candidates[0] - boundary) == Math.Abs(candidates[1] - boundary))
            {
                return widths.ToList(); // две равноудалённые границы — выравнивание неоднозначно
            }

            snapped.Add(candidates.Count > 0 ? candidates[0] : boundary);
        }

        for (var i = 0; i < snapped.Count; i++)
        {
            if (snapped[i] <= (i == 0 ? 0 : snapped[i - 1]))
            {
                return widths.ToList();
            }
        }

        var result = new List<int>();
        var previous = 0;
        foreach (var start in snapped)
        {
            result.Add(start - previous);
            previous = start;
        }

        // Последняя колонка читается до конца строки; её ширина сохраняет общую длину, предложенную моделью.
        result.Add(Math.Max(1, widths[widths.Count - 1] + boundaries[boundaries.Count - 1] - snapped[snapped.Count - 1]));
        changed = !result.SequenceEqual(widths);
        return result;
    }
}
