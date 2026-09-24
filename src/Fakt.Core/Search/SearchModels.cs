using System;
using System.Collections.Generic;

namespace Fakt.Core.Search;

public enum SearchMode
{
    /// <summary>Все слова (AND) — полнотекстовый поиск.</summary>
    AllWords,

    /// <summary>Любое слово (OR) — полнотекстовый поиск.</summary>
    AnyWord,

    /// <summary>Точная фраза — полнотекстовый поиск.</summary>
    ExactPhrase,

    /// <summary>Подстрока — LIKE по поисковой проекции; медленно на больших объёмах, отличается от полнотекстового.</summary>
    Substring,
}

public enum SearchSort
{
    Relevance,
    IdAscending,
    IdDescending,
}

public sealed class SearchQuery
{
    public string Text { get; set; }
    public SearchMode Mode { get; set; } = SearchMode.AllWords;

    public string Surname { get; set; }
    public string Name { get; set; }
    public string Patronymic { get; set; }

    /// <summary>true — точное совпадение ФИО; false — по началу значения.</summary>
    public bool NamesExact { get; set; }

    public DateTime? BirthDateFrom { get; set; }
    public DateTime? BirthDateTo { get; set; }

    public string BirthPlace { get; set; }

    public string FactType { get; set; }

    /// <summary>Телефон, счёт или иной идентификатор — сравнивается по нормализованному значению.</summary>
    public string Identifier { get; set; }

    public bool IdentifierPrefix { get; set; }

    public string FileName { get; set; }
    public string FileCode { get; set; }

    public long? PersonFactId { get; set; }
    public long? FileId { get; set; }

    public int Page { get; set; }
    public int PageSize { get; set; } = 100;
    public SearchSort Sort { get; set; } = SearchSort.Relevance;

    /// <summary>Предел времени выполнения запроса поиска, секунд.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public bool HasAnyCriteria =>
        !string.IsNullOrWhiteSpace(Text) || !string.IsNullOrWhiteSpace(Surname) || !string.IsNullOrWhiteSpace(Name) ||
        !string.IsNullOrWhiteSpace(Patronymic) || BirthDateFrom.HasValue || BirthDateTo.HasValue ||
        !string.IsNullOrWhiteSpace(BirthPlace) || !string.IsNullOrWhiteSpace(FactType) ||
        !string.IsNullOrWhiteSpace(Identifier) || !string.IsNullOrWhiteSpace(FileName) ||
        !string.IsNullOrWhiteSpace(FileCode) || PersonFactId.HasValue || FileId.HasValue;

    public SearchQuery WithPage(int page)
    {
        var copy = (SearchQuery)MemberwiseClone();
        copy.Page = page;
        return copy;
    }
}

public sealed class SearchResultRow
{
    public long PersonFactId { get; set; }
    public string Surname { get; set; }
    public string Name { get; set; }
    public string Patronymic { get; set; }
    public DateTime? BirthDate { get; set; }
    public string BirthPlace { get; set; }
    public string AllJson { get; set; }
    public long FileId { get; set; }
    public string FileCode { get; set; }
    public string FileName { get; set; }
    public int? Rank { get; set; }
}

public sealed class SearchPage
{
    public List<SearchResultRow> Rows { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>Есть следующая страница (выяснено запросом PageSize + 1 строк, без подсчёта всего объёма).</summary>
    public bool HasMore { get; set; }

    public TimeSpan Elapsed { get; set; }

    public bool UsedFullText { get; set; }

    /// <summary>Пояснения: задержка обновления индекса, режим «Подстрока» и т. п.</summary>
    public List<string> Notices { get; set; } = new();

    /// <summary>Нормализованные слова запроса для подсветки совпадений.</summary>
    public List<string> HighlightTerms { get; set; } = new();
}

/// <summary>Все поля обеих основных таблиц для карточки наблюдения (включая технические столбцы).</summary>
public sealed class ObservationDetails
{
    public long PersonFactId { get; set; }
    public long FileId { get; set; }
    public string FileCode { get; set; }
    public string FileName { get; set; }
    public string Surname { get; set; }
    public string Name { get; set; }
    public string Patronymic { get; set; }
    public DateTime? BirthDate { get; set; }
    public string BirthPlace { get; set; }
    public string AllJson { get; set; }
    public string SourcePath { get; set; }

    public List<KeyValuePair<string, string>> PersonFactsColumns { get; set; } = new();
    public List<KeyValuePair<string, string>> SourceFilesColumns { get; set; } = new();
}

public sealed class SearchValidationException : Exception
{
    public SearchValidationException(string message) : base(message)
    {
    }
}
