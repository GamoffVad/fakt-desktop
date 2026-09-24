using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Fakt.Core.Search;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class FullTextQueryBuilderTests
{
    // Условие CONTAINS: только термины в кавычках, соединённые « AND » или « OR ».
    private static readonly Regex ConditionGrammar = new("^\"[^\"]+\"(?: (?:AND|OR) \"[^\"]+\")*$", RegexOptions.CultureInvariant);

    // Слово термина: буквы, цифры и символы идентификаторов; «*» — только в конце.
    private static readonly Regex TermWord = new(@"^[\p{L}\p{Nd}_\-.@+/]+\*?$", RegexOptions.CultureInvariant);

    private static void AssertSafe(FullTextQueryBuilder.Result result, SearchMode mode)
    {
        Assert.Matches(ConditionGrammar, result.Condition);
        var forbiddenJoiner = mode == SearchMode.AnyWord ? " AND " : " OR ";
        var outside = Regex.Replace(result.Condition, "\"[^\"]*\"", "T");
        Assert.DoesNotContain(forbiddenJoiner, outside);
        foreach (Match term in Regex.Matches(result.Condition, "\"([^\"]*)\""))
        {
            foreach (var word in term.Groups[1].Value.Split(' '))
            {
                Assert.Matches(TermWord, word);
            }
        }
    }

    [Fact]
    public void AllWords_QuotesEachTermAndJoinsWithAnd()
    {
        var result = FullTextQueryBuilder.Build("Иванов Иван", SearchMode.AllWords);

        Assert.Equal("\"Иванов\" AND \"Иван\"", result.Condition);
        Assert.Equal(new[] { "Иванов", "Иван" }, result.Terms);
    }

    [Fact]
    public void AnyWord_JoinsWithOr()
    {
        var result = FullTextQueryBuilder.Build("Иванов Петров", SearchMode.AnyWord);

        Assert.Equal("\"Иванов\" OR \"Петров\"", result.Condition);
    }

    [Fact]
    public void QuotedPhrase_StaysOneTerm()
    {
        var result = FullTextQueryBuilder.Build("\"Тестов Иван\" T_123456789012345678", SearchMode.AllWords);

        Assert.Equal("\"Тестов Иван\" AND \"T_123456789012345678\"", result.Condition);
        Assert.Equal(new[] { "Тестов Иван", "T_123456789012345678" }, result.Terms);
    }

    [Fact]
    public void ExactPhrase_BuildsSingleQuotedPhraseWithoutUserQuotes()
    {
        var result = FullTextQueryBuilder.Build("Иванов \"Иван\" Иванович", SearchMode.ExactPhrase);

        Assert.Equal("\"Иванов Иван Иванович\"", result.Condition);
        Assert.Equal(new[] { "Иванов", "Иван", "Иванович" }, result.Terms);
    }

    [Fact]
    public void TrailingStar_IsPrefixTerm()
    {
        var result = FullTextQueryBuilder.Build("Иван*", SearchMode.AllWords);

        Assert.Equal("\"Иван*\"", result.Condition);
        Assert.Equal(new[] { "Иван" }, result.Terms);
    }

    [Fact]
    public void StarInsideWord_BecomesSuffix()
    {
        Assert.Equal("\"Иван*\"", FullTextQueryBuilder.Build("Ив*ан", SearchMode.AllWords).Condition);
        Assert.Equal("\"Иван*\"", FullTextQueryBuilder.Build("Иван**", SearchMode.AllWords).Condition);
    }

    [Theory]
    [InlineData("*ванов")]
    [InlineData("Иванов *нов")]
    [InlineData(".*ванов")]
    public void LeadingStar_IsRejectedWithHintAboutSubstringMode(string text)
    {
        var ex = Assert.Throws<SearchValidationException>(() => FullTextQueryBuilder.Build(text, SearchMode.AllWords));
        Assert.Contains("Подстрока", ex.Message);
    }

    [Fact]
    public void StarInExactPhrase_IsRejected()
    {
        var ex = Assert.Throws<SearchValidationException>(() => FullTextQueryBuilder.Build("Иванов Ив*", SearchMode.ExactPhrase));
        Assert.Contains("*", ex.Message);
    }

    [Theory]
    [InlineData("Иванов\" OR \"x", SearchMode.AllWords, "\"Иванов\" AND \"OR\" AND \"x\"")]
    [InlineData("NEAR((Иванов, Иван), 5)", SearchMode.AllWords, "\"NEAR\" AND \"Иванов\" AND \"Иван\" AND \"5\"")]
    [InlineData("Иванов AND NOT Петров", SearchMode.AllWords, "\"Иванов\" AND \"AND\" AND \"NOT\" AND \"Петров\"")]
    [InlineData("[Иванов] & | ! ~ ( ) , ;", SearchMode.AnyWord, "\"Иванов\"")]
    [InlineData("FORMSOF(INFLECTIONAL, Иван)", SearchMode.AnyWord, "\"FORMSOF\" OR \"INFLECTIONAL\" OR \"Иван\"")]
    [InlineData("Иванов' OR 1=1 --", SearchMode.AllWords, "\"Иванов\" AND \"OR\" AND \"1\" AND \"1\"")]
    public void OperatorsAndPunctuation_BecomeQuotedLiteralsOrSeparators(string text, SearchMode mode, string expected)
    {
        var result = FullTextQueryBuilder.Build(text, mode);

        Assert.Equal(expected, result.Condition);
        AssertSafe(result, mode);
    }

    [Fact]
    public void IdentifierCharacters_AreKeptInsideTerm()
    {
        var result = FullTextQueryBuilder.Build("ivan.petrov@example.test +7-900-123 A/B_1", SearchMode.AllWords);

        Assert.Equal("\"ivan.petrov@example.test\" AND \"7-900-123\" AND \"A/B_1\"", result.Condition);
    }

    [Fact]
    public void PunctuationOnlyPhrase_AddsNotice()
    {
        var result = FullTextQueryBuilder.Build("Иванов \"!!!\"", SearchMode.AllWords);

        Assert.Equal("\"Иванов\"", result.Condition);
        Assert.Contains(result.Notices, n => n.Contains("Пунктуация"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ??? ...")]
    [InlineData("\"\"")]
    public void QueryWithoutWords_IsRejected(string text)
    {
        Assert.Throws<SearchValidationException>(() => FullTextQueryBuilder.Build(text, SearchMode.AllWords));
    }

    [Fact]
    public void TooLongQuery_IsRejected()
    {
        var text = new string('а', FullTextQueryBuilder.MaxQueryLength + 1);

        Assert.Throws<SearchValidationException>(() => FullTextQueryBuilder.Build(text, SearchMode.AllWords));
    }

    [Fact]
    public void TooLongWord_IsRejected()
    {
        var text = new string('а', FullTextQueryBuilder.MaxTermLength + 1);

        Assert.Throws<SearchValidationException>(() => FullTextQueryBuilder.Build(text, SearchMode.AnyWord));
    }

    [Fact]
    public void TermLimit_IsEnforced()
    {
        var twenty = string.Join(" ", Enumerable.Range(1, FullTextQueryBuilder.MaxTerms).Select(i => "w" + i));
        Assert.Equal(FullTextQueryBuilder.MaxTerms, FullTextQueryBuilder.Build(twenty, SearchMode.AnyWord).Terms.Count);

        var ex = Assert.Throws<SearchValidationException>(() => FullTextQueryBuilder.Build(twenty + " w21", SearchMode.AnyWord));
        Assert.Contains("21", ex.Message);
    }

    [Fact]
    public void SubstringMode_IsNotFullText()
    {
        Assert.Throws<ArgumentException>(() => FullTextQueryBuilder.Build("Иванов", SearchMode.Substring));
    }

    [Theory]
    [InlineData(SearchMode.AllWords)]
    [InlineData(SearchMode.AnyWord)]
    [InlineData(SearchMode.ExactPhrase)]
    public void RandomHostileInput_NeverBreaksContainsSyntax(SearchMode mode)
    {
        string[] fragments =
        {
            "Иванов", "Ivan", "\"", "'", "(", ")", "[", "]", "{", "}", "NEAR", "AND", "OR", "NOT", "~", "&", "|", "!", "*", "Ив*",
            "*ван", ",", ";", "--", "/*", "*/", "0012", "+7", "@mail", " ", "  ", "\t", "ISABOUT", "\\", "%", "_", "ё", "T_000000000000000001",
        };
        var random = new Random(20260924);
        for (var i = 0; i < 500; i++)
        {
            var builder = new StringBuilder();
            var count = random.Next(1, 12);
            for (var j = 0; j < count; j++)
            {
                builder.Append(fragments[random.Next(fragments.Length)]);
            }

            FullTextQueryBuilder.Result result;
            try
            {
                result = FullTextQueryBuilder.Build(builder.ToString(), mode);
            }
            catch (SearchValidationException)
            {
                continue;
            }

            AssertSafe(result, mode);
            if (mode == SearchMode.ExactPhrase)
            {
                // Точная фраза — ровно одна фраза в кавычках, без префиксного поиска.
                Assert.Matches("^\"[^\"*]+\"$", result.Condition);
            }
        }
    }

    [Theory]
    [InlineData("50%_[a]\\b", "50\\%\\_\\[a]\\\\b")]
    [InlineData("Иванов", "Иванов")]
    [InlineData("T_123", "T\\_123")]
    public void EscapeLike_EscapesWildcardsAndEscapeCharacter(string input, string expected)
    {
        Assert.Equal(expected, FullTextQueryBuilder.EscapeLike(input));
    }
}
