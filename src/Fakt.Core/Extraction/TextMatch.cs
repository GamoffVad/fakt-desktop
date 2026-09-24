using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Fakt.Core.Extraction;

/// <summary>
/// Сравнение фрагментов ответа модели с исходной записью. Регистр, «ё/е», пробелы, варианты кавычек и тире
/// не различаются; иначе значение считается не подтверждённым записью.
/// </summary>
public static class TextMatch
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var raw in text)
        {
            var ch = char.ToLowerInvariant(raw);
            switch (ch)
            {
                case 'ё':
                    ch = 'е';
                    break;
                case '«':
                case '»':
                case '“':
                case '”':
                case '„':
                case '‟':
                    ch = '"';
                    break;
                case '‘':
                case '’':
                case '`':
                    ch = '\'';
                    break;
                case '–':
                case '—':
                case '‑':
                case '−':
                    ch = '-';
                    break;
                case '​':
                case '‌':
                case '‍':
                case '﻿':
                    continue;
            }

            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>Только буквы и цифры в нижнем регистре; для номеров, записанных в разных форматах.</summary>
    public static string AlnumLower(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            if (char.IsLetterOrDigit(raw))
            {
                builder.Append(raw == 'ё' || raw == 'Ё' ? 'е' : char.ToLowerInvariant(raw));
            }
        }

        return builder.ToString();
    }

    public static bool Contains(string normalizedHaystack, string fragment)
    {
        var needle = Normalize(fragment);
        return needle.Length > 0 && normalizedHaystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
    }

    public static bool ContainsAlnum(string alnumHaystack, string fragment, int minLength = 3)
    {
        var needle = AlnumLower(fragment);
        return needle.Length >= minLength && alnumHaystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
    }

    public static IEnumerable<string> Words(string normalized)
    {
        var builder = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>
    /// Значение — допустимый вариант фрагмента: совпадает или отличается только грамматическим окончанием
    /// (например, «Тестове» → «Тестов»). Для каждого слова значения в фрагменте должно найтись слово с общим
    /// началом не короче max(3, длина − 3).
    /// </summary>
    public static bool IsPlausibleVariant(string value, string evidence)
    {
        var valueNorm = Normalize(value);
        var evidenceNorm = Normalize(evidence);
        if (valueNorm.Length == 0 || evidenceNorm.Length == 0)
        {
            return false;
        }

        if (evidenceNorm.IndexOf(valueNorm, StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        var evidenceWords = Words(evidenceNorm).ToList();
        foreach (var word in Words(valueNorm))
        {
            var need = Math.Max(3, word.Length - 3);
            if (!evidenceWords.Any(e => CommonPrefix(word, e) >= Math.Min(need, Math.Min(word.Length, e.Length)) && CommonPrefix(word, e) >= Math.Min(3, word.Length)))
            {
                return false;
            }
        }

        return true;
    }

    private static int CommonPrefix(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < length && a[i] == b[i])
        {
            i++;
        }

        return i;
    }
}
