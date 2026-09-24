using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fakt.Core.Extraction;

/// <summary>
/// Проверка даты рождения, предложенной моделью, по исходному фрагменту. Основное поле DATE заполняется
/// только полной однозначной датой; иначе значение остаётся NULL, а исходная форма и причина уходят в
/// unresolved_fields. Даты с точками читаются как «день.месяц.год» (российская запись).
/// </summary>
public static class BirthDateChecker
{
    public const int MinYear = 1850;

    private static readonly Regex IsoPattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex IsoInText = new(@"(?<!\d)(\d{4})-(\d{1,2})-(\d{1,2})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex DottedInText = new(@"(?<!\d)(\d{1,2})\.(\d{1,2})\.(\d{2,4})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex SlashedInText = new(@"(?<!\d)(\d{1,2})[/\-](\d{1,2})[/\-](\d{2,4})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex YearPattern = new(@"(?<!\d)(\d{4})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex NumberPattern = new(@"(?<!\d)(\d{1,4})(?!\d)", RegexOptions.CultureInvariant);

    // Русские основы допускают только падежные окончания месяца: «февраля», «марта», «сентябре».
    // Произвольный хвост не допускается: имена и слова с той же основой («Майя», «майор», «Мартин») — не месяцы.
    private static readonly string[][] MonthStems =
    {
        new[] { "январ" }, new[] { "феврал" }, new[] { "март" }, new[] { "апрел" }, new[] { "мая", "май" }, new[] { "июн" },
        new[] { "июл" }, new[] { "август" }, new[] { "сентябр" }, new[] { "октябр" }, new[] { "ноябр" }, new[] { "декабр" },
    };

    // Окончания единственного числа («ё» при нормализации заменено на «е»): мягкая основа (январь), твёрдая (март);
    // «мая» и «май» — только целиком.
    private static readonly string[] SoftEndings = { "ь", "я", "е", "ю", "ем" };
    private static readonly string[] HardEndings = { "", "а", "е", "у", "ом" };
    private static readonly string[] WholeWord = { "" };

    private static readonly string[][] MonthEndings =
    {
        SoftEndings, SoftEndings, HardEndings, SoftEndings, WholeWord, SoftEndings,
        SoftEndings, HardEndings, SoftEndings, SoftEndings, SoftEndings, SoftEndings,
    };

    // Английские названия — только точное совпадение слова, чтобы «Jane» или «Mark» не стали месяцем.
    private static readonly string[][] EnglishMonths =
    {
        new[] { "january", "jan" }, new[] { "february", "feb" }, new[] { "march" }, new[] { "april", "apr" },
        new[] { "may" }, new[] { "june", "jun" }, new[] { "july", "jul" }, new[] { "august", "aug" },
        new[] { "september", "sep", "sept" }, new[] { "october", "oct" }, new[] { "november", "nov" }, new[] { "december", "dec" },
    };

    public sealed class Result
    {
        public DateTime? Date { get; set; }

        /// <summary>Причина, по которой дата не принята (для unresolved_fields).</summary>
        public string Reason { get; set; }
    }

    public static Result Check(string modelValue, string evidence, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(modelValue))
        {
            return new Result();
        }

        var value = modelValue.Trim();
        if (!IsoPattern.IsMatch(value) ||
            !DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new Result { Reason = $"Значение «{value}» не является корректной датой ГГГГ-ММ-ДД" };
        }

        if (date.Year < MinYear)
        {
            return new Result { Reason = $"Год {date.Year} раньше {MinYear}" };
        }

        if (date.Date > today.Date)
        {
            return new Result { Reason = "Дата рождения в будущем" };
        }

        if (string.IsNullOrWhiteSpace(evidence))
        {
            return new Result { Reason = "Нет исходного фрагмента для проверки даты" };
        }

        var text = TextMatch.Normalize(evidence);

        // 1) ISO внутри фрагмента.
        foreach (Match m in IsoInText.Matches(text))
        {
            if (Same(date, Int(m.Groups[1]), Int(m.Groups[2]), Int(m.Groups[3])))
            {
                return new Result { Date = date };
            }
        }

        // 2) дд.мм.гггг — день первым; двузначный год не принимается.
        foreach (Match m in DottedInText.Matches(text))
        {
            if (m.Groups[3].Value.Length != 4)
            {
                return new Result { Reason = "Двузначный год: век не установлен" };
            }

            if (Same(date, Int(m.Groups[3]), Int(m.Groups[2]), Int(m.Groups[1])))
            {
                return new Result { Date = date };
            }

            return new Result { Reason = "Дата не совпадает с исходной записью дд.мм.гггг" };
        }

        // 3) дд/мм/гггг или мм/дд/гггг: однозначно только если одна из частей больше 12.
        foreach (Match m in SlashedInText.Matches(text))
        {
            if (m.Groups[3].Value.Length != 4)
            {
                return new Result { Reason = "Двузначный год: век не установлен" };
            }

            var a = Int(m.Groups[1]);
            var b = Int(m.Groups[2]);
            var year = Int(m.Groups[3]);
            if (a <= 12 && b <= 12 && a != b)
            {
                return new Result { Reason = "Неоднозначный порядок дня и месяца" };
            }

            if (Same(date, year, b, a) || Same(date, year, a, b))
            {
                return new Result { Date = date };
            }

            return new Result { Reason = "Дата не совпадает с исходной записью" };
        }

        // 4) Месяц словом: «15 февраля 1990 г.»
        var month = FindMonthName(text);
        if (month > 0)
        {
            var years = YearPattern.Matches(text).Cast<Match>().Select(m => Int(m.Groups[1])).ToList();
            var numbers = NumberPattern.Matches(text).Cast<Match>().Select(m => Int(m.Groups[1])).Where(n => n >= 1 && n <= 31).ToList();
            if (years.Contains(date.Year) && month == date.Month && numbers.Contains(date.Day))
            {
                return new Result { Date = date };
            }

            return new Result { Reason = "Дата не подтверждается фрагментом с названием месяца" };
        }

        if (YearPattern.IsMatch(text))
        {
            return new Result { Reason = "Во фрагменте нет полной даты" };
        }

        return new Result { Reason = "Формат даты во фрагменте не распознан для проверки" };
    }

    private static bool Same(DateTime date, int year, int month, int day) => date.Year == year && date.Month == month && date.Day == day;

    private static int Int(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    private static int FindMonthName(string normalized)
    {
        foreach (var word in TextMatch.Words(normalized))
        {
            for (var i = 0; i < MonthStems.Length; i++)
            {
                foreach (var stem in MonthStems[i])
                {
                    if (word.StartsWith(stem, StringComparison.Ordinal) && MonthEndings[i].Contains(word.Substring(stem.Length)))
                    {
                        return i + 1;
                    }
                }

                if (EnglishMonths[i].Contains(word))
                {
                    return i + 1;
                }
            }
        }

        return 0;
    }

    public static IEnumerable<string> RussianMonthNames() => MonthStems.Select(s => s[0]);
}
