using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Fakt.Core.Search;

/// <summary>
/// Построение условия CONTAINS из пользовательского ввода. Пользователь не вводит синтаксис SQL Server:
/// слова и фразы берутся в кавычки, служебные операторы удаляются. Условие передаётся параметром —
/// эта проверка отвечает за корректность синтаксиса полнотекстового поиска, а не за защиту от инъекций.
/// </summary>
public static class FullTextQueryBuilder
{
    public const int MaxTerms = 20;
    public const int MaxTermLength = 100;
    public const int MaxQueryLength = 1000;

    public sealed class Result
    {
        public string Condition { get; set; }

        public List<string> Terms { get; set; } = new();

        public List<string> Notices { get; set; } = new();
    }

    public static Result Build(string text, SearchMode mode)
    {
        if (mode == SearchMode.Substring)
        {
            throw new ArgumentException("Режим «Подстрока» не использует полнотекстовый поиск.", nameof(mode));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SearchValidationException("Введите слова для поиска.");
        }

        if (text.Length > MaxQueryLength)
        {
            throw new SearchValidationException($"Запрос длиннее {MaxQueryLength} символов.");
        }

        var result = new Result();
        if (mode == SearchMode.ExactPhrase)
        {
            var words = SplitWords(text.Replace("\"", " "), result.Notices);
            if (words.Count == 0)
            {
                throw new SearchValidationException("В запросе нет слов для поиска: буквы или цифры не найдены.");
            }

            if (words.Any(w => w.EndsWith("*", StringComparison.Ordinal)))
            {
                throw new SearchValidationException("В режиме «Точная фраза» символ * не поддерживается.");
            }

            result.Terms.AddRange(words);
            result.Condition = "\"" + string.Join(" ", words) + "\"";
            return result;
        }

        var terms = Tokenize(text, result.Notices);
        if (terms.Count == 0)
        {
            throw new SearchValidationException("В запросе нет слов для поиска: буквы или цифры не найдены.");
        }

        if (terms.Count > MaxTerms)
        {
            throw new SearchValidationException($"Слишком много слов: {terms.Count} (не более {MaxTerms}).");
        }

        var joiner = mode == SearchMode.AnyWord ? " OR " : " AND ";
        result.Terms.AddRange(terms.Select(t => t.TrimEnd('*')));
        result.Condition = string.Join(joiner, terms.Select(t => "\"" + t + "\""));
        return result;
    }

    /// <summary>Разбиение с учётом фраз в кавычках: «"Тестов Иван" T_123» → [Тестов Иван] [T_123].</summary>
    private static List<string> Tokenize(string text, List<string> notices)
    {
        var terms = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '"')
            {
                var end = text.IndexOf('"', index + 1);
                var phrase = end < 0 ? text.Substring(index + 1) : text.Substring(index + 1, end - index - 1);
                index = end < 0 ? text.Length : end + 1;
                var words = SplitWords(phrase, notices);
                if (words.Count > 0)
                {
                    terms.Add(string.Join(" ", words));
                }

                continue;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '"')
            {
                index++;
            }

            terms.AddRange(SplitWords(text.Substring(start, index - start), notices));
        }

        return terms;
    }

    /// <summary>
    /// Слова: буквы, цифры и символы внутри идентификаторов (_ - . @ + /). Остальная пунктуация — разделитель.
    /// Звёздочка допускается только в конце слова (поиск по началу слова).
    /// </summary>
    private static List<string> SplitWords(string text, List<string> notices)
    {
        var words = new List<string>();
        var builder = new StringBuilder();
        void Flush()
        {
            if (builder.Length == 0)
            {
                return;
            }

            var word = builder.ToString().Trim('.', '-', '/', '+', '@');
            builder.Clear();
            if (word.Length == 0)
            {
                return;
            }

            if (word.IndexOf('*') >= 0)
            {
                if (word.StartsWith("*", StringComparison.Ordinal))
                {
                    throw new SearchValidationException(
                        "Полнотекстовый поиск не находит произвольный фрагмент слова: символ * допустим только в конце слова (поиск по началу). Для поиска фрагмента выберите режим «Подстрока».");
                }

                var core = word.Replace("*", string.Empty);
                if (core.Length == 0)
                {
                    return;
                }

                word = core + "*";
            }

            if (word.Length > MaxTermLength)
            {
                throw new SearchValidationException($"Слово длиннее {MaxTermLength} символов.");
            }

            words.Add(word);
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' || ch == '@' || ch == '+' || ch == '/' || ch == '*')
            {
                builder.Append(ch);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        if (words.Count == 0 && text.Trim().Length > 0 && notices != null)
        {
            notices.Add("Пунктуация в запросе не учитывается полнотекстовым поиском.");
        }

        return words;
    }

    /// <summary>Экранирование шаблона LIKE для режима «Подстрока».</summary>
    public static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch == '\\' || ch == '%' || ch == '_' || ch == '[')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
