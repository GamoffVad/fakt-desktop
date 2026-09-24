using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Search;
using Fakt.Core.Storage;

namespace Fakt.Infrastructure.Sql;

/// <summary>
/// Единый пользовательский поиск: полнотекстовый поиск SQL Server по поисковой проекции (слова обеих
/// таблиц в одном документе), типизированные предикаты для DATE и BIGINT и индекс нормализованных
/// идентификаторов. Объединение строго по внешнему ключу ID_FileName → SourceFiles.ID. Все значения
/// передаются параметрами; без Full-Text Search полнотекстовые режимы отклоняются с объяснением,
/// а медленный LIKE используется только в явно выбранном режиме «Подстрока».
/// </summary>
public sealed class SqlSearchRepository : ISearchRepository
{
    private static readonly Regex FileCodePattern = new(@"^T_\d{18}$", RegexOptions.CultureInvariant);
    private readonly SqlConnectionFactory _factory;
    private FullTextInfo _ftsCache;
    private DateTime _ftsCachedAt;

    public SqlSearchRepository(SqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private sealed class BuiltQuery
    {
        public string From { get; set; }
        public string Where { get; set; }
        public string OrderBy { get; set; }
        public bool UsesRank { get; set; }
        public List<SqlParameter> Parameters { get; } = new();
        public List<string> Notices { get; } = new();
        public List<string> Highlights { get; } = new();
        public bool UsedFullText { get; set; }
    }

    public async Task<FullTextInfo> GetFullTextInfoAsync(CancellationToken cancellationToken)
    {
        if (_ftsCache != null && DateTime.UtcNow - _ftsCachedAt < TimeSpan.FromSeconds(30))
        {
            return _ftsCache;
        }

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var exists = _factory.Command(connection, "SELECT CASE WHEN OBJECT_ID(@obj, N'U') IS NULL THEN 0 ELSE 1 END");
        exists.Parameters.Add("@obj", SqlDbType.NVarChar, 600).Value = _factory.Names.Aux("FaktSearchDocs");
        var projection = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
        _ftsCache = await DatabaseAdmin.ReadFullTextAsync(_factory, connection, _factory.Names, projection, cancellationToken).ConfigureAwait(false);
        _ftsCachedAt = DateTime.UtcNow;
        return _ftsCache;
    }

    private async Task<BuiltQuery> BuildAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (!query.HasAnyCriteria)
        {
            throw new SearchValidationException("Задайте слова для поиска или хотя бы один фильтр.");
        }

        var names = _factory.Names;
        var c = names.Columns;
        var built = new BuiltQuery();
        var from = new StringBuilder();
        from.Append($"FROM {names.PF} AS p INNER JOIN {names.SF} AS f ON p.{names.Col(c.FileId)} = f.{names.Col(c.SourceFilesId)}");
        var where = new List<string>();
        var needsDocs = false;
        var index = 0;
        SqlParameter Param(SqlDbType type, object value, int size = 0)
        {
            var parameter = new SqlParameter("@p" + index++, type) { Value = value ?? DBNull.Value };
            if (size != 0)
            {
                parameter.Size = size;
            }

            built.Parameters.Add(parameter);
            return parameter;
        }

        var text = query.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (query.Mode == SearchMode.Substring)
            {
                if (text.Length < 2)
                {
                    throw new SearchValidationException("Для режима «Подстрока» введите не менее двух символов.");
                }

                needsDocs = true;
                var like = Param(SqlDbType.NVarChar, "%" + FullTextQueryBuilder.EscapeLike(text) + "%", 4000);
                where.Add($"d.[AllText] LIKE {like.ParameterName} ESCAPE N'\\'");
                built.Highlights.Add(text);
                built.Notices.Add("Режим «Подстрока» использует LIKE по поисковой проекции без индекса: на миллионах записей он медленный и ограничен временем запроса. Это не полнотекстовый поиск.");
            }
            else
            {
                var fts = await GetFullTextInfoAsync(cancellationToken).ConfigureAwait(false);
                if (!fts.Ready)
                {
                    throw new SearchValidationException(
                        "Полнотекстовый поиск недоступен: " +
                        (!fts.Installed ? "компонент Full-Text Search не установлен на сервере SQL Server" :
                            !fts.CatalogExists || !fts.IndexExists ? "полнотекстовый индекс не создан (миграция V007 в разделе «Администрирование → База данных»)" : "индекс неактивен") +
                        ". Для поиска фрагмента текста выберите режим «Подстрока» (медленный LIKE).");
                }

                var condition = FullTextQueryBuilder.Build(text, query.Mode);
                var fts1 = Param(SqlDbType.NVarChar, condition.Condition, 4000);
                from.Append($" INNER JOIN CONTAINSTABLE({names.Aux("FaktSearchDocs")}, [AllText], {fts1.ParameterName}) AS ct ON ct.[KEY] = p.{names.Col(c.PersonFactsId)}");
                built.UsesRank = true;
                built.UsedFullText = true;
                built.Highlights.AddRange(condition.Terms.SelectMany(t => t.Split(' ')));
                built.Notices.AddRange(condition.Notices);
                if (fts.PendingChanges > 0)
                {
                    built.Notices.Add($"Полнотекстовый индекс ещё обрабатывает {fts.PendingChanges} изменений: недавно сохранённые записи могут не найтись.");
                }
            }
        }

        void NameFilter(string value, string column)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            built.Highlights.Add(trimmed);
            if (query.NamesExact)
            {
                where.Add($"p.{names.Col(column)} = {Param(SqlDbType.NVarChar, trimmed, 4000).ParameterName}");
            }
            else
            {
                where.Add($"p.{names.Col(column)} LIKE {Param(SqlDbType.NVarChar, FullTextQueryBuilder.EscapeLike(trimmed) + "%", 4000).ParameterName} ESCAPE N'\\'");
            }
        }

        NameFilter(query.Surname, c.Surname);
        NameFilter(query.Name, c.Name);
        NameFilter(query.Patronymic, c.Patronymic);

        if (query.BirthDateFrom.HasValue)
        {
            where.Add($"p.{names.Col(c.BirthDate)} >= {Param(SqlDbType.Date, query.BirthDateFrom.Value.Date).ParameterName}");
        }

        if (query.BirthDateTo.HasValue)
        {
            where.Add($"p.{names.Col(c.BirthDate)} <= {Param(SqlDbType.Date, query.BirthDateTo.Value.Date).ParameterName}");
        }

        if (!string.IsNullOrWhiteSpace(query.BirthPlace))
        {
            var fts = await GetFullTextInfoAsync(cancellationToken).ConfigureAwait(false);
            built.Highlights.Add(query.BirthPlace.Trim());
            if (fts.Ready)
            {
                needsDocs = true;
                var condition = FullTextQueryBuilder.Build(query.BirthPlace, SearchMode.AllWords);
                where.Add($"CONTAINS(d.[BirthPlaceText], {Param(SqlDbType.NVarChar, condition.Condition, 4000).ParameterName})");
            }
            else
            {
                where.Add($"p.{names.Col(c.BirthPlace)} LIKE {Param(SqlDbType.NVarChar, "%" + FullTextQueryBuilder.EscapeLike(query.BirthPlace.Trim()) + "%", 4000).ParameterName} ESCAPE N'\\'");
                built.Notices.Add("Фильтр «Место рождения» выполнен через LIKE: полнотекстовый индекс недоступен, на больших объёмах это медленно.");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.FactType))
        {
            where.Add($"EXISTS (SELECT 1 FROM {names.Aux("FaktFactValues")} AS vt WHERE vt.[PersonFactId] = p.{names.Col(c.PersonFactsId)} AND vt.[FactType] = {Param(SqlDbType.VarChar, query.FactType, 32).ParameterName})");
        }

        if (!string.IsNullOrWhiteSpace(query.Identifier))
        {
            var type = string.IsNullOrWhiteSpace(query.FactType) ? null : query.FactType;
            var key = type != null && FactTypes.IsIdentifier(type) ? FactNormalizer.SearchKey(type, query.Identifier) : FactNormalizer.SearchKeyForAnyIdentifier(query.Identifier);
            if (string.IsNullOrEmpty(key))
            {
                throw new SearchValidationException("Идентификатор не содержит значимых символов для поиска (цифр или букв).");
            }

            built.Highlights.Add(query.Identifier.Trim());
            var valueCondition = query.IdentifierPrefix
                ? $"vi.[NormalizedValue] LIKE {Param(SqlDbType.NVarChar, FullTextQueryBuilder.EscapeLike(key) + "%", 400).ParameterName} ESCAPE N'\\'"
                : $"vi.[NormalizedValue] = {Param(SqlDbType.NVarChar, key, 400).ParameterName}";
            var typeCondition = type != null ? $" AND vi.[FactType] = {Param(SqlDbType.VarChar, type, 32).ParameterName}" : string.Empty;
            where.Add($"EXISTS (SELECT 1 FROM {names.Aux("FaktFactValues")} AS vi WHERE vi.[PersonFactId] = p.{names.Col(c.PersonFactsId)} AND {valueCondition}{typeCondition})");
            built.Notices.Add($"Идентификатор сравнивается по нормализованному значению «{key}»{(query.IdentifierPrefix ? " (по началу)" : " (точно)")}. Код страны не добавляется: номера «8…» и «+7…» различаются.");
        }

        if (!string.IsNullOrWhiteSpace(query.FileName))
        {
            built.Highlights.Add(query.FileName.Trim());
            where.Add($"f.{names.Col(c.FileName)} LIKE {Param(SqlDbType.NVarChar, "%" + FullTextQueryBuilder.EscapeLike(query.FileName.Trim()) + "%", 4000).ParameterName} ESCAPE N'\\'");
        }

        if (!string.IsNullOrWhiteSpace(query.FileCode))
        {
            var code = query.FileCode.Trim().ToUpperInvariant();
            built.Highlights.Add(code);
            where.Add(FileCodePattern.IsMatch(code)
                ? $"f.{names.Col(c.FileCode)} = {Param(SqlDbType.VarChar, code, 32).ParameterName}"
                : $"f.{names.Col(c.FileCode)} LIKE {Param(SqlDbType.VarChar, FullTextQueryBuilder.EscapeLike(code) + "%", 64).ParameterName} ESCAPE '\\'");
        }

        if (query.PersonFactId.HasValue)
        {
            where.Add($"p.{names.Col(c.PersonFactsId)} = {Param(SqlDbType.BigInt, query.PersonFactId.Value).ParameterName}");
        }

        if (query.FileId.HasValue)
        {
            where.Add($"f.{names.Col(c.SourceFilesId)} = {Param(SqlDbType.BigInt, query.FileId.Value).ParameterName}");
        }

        if (needsDocs)
        {
            from.Append($" INNER JOIN {names.Aux("FaktSearchDocs")} AS d ON d.[PersonFactId] = p.{names.Col(c.PersonFactsId)}");
        }

        built.From = from.ToString();
        built.Where = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);
        var idColumn = $"p.{names.Col(c.PersonFactsId)}";
        built.OrderBy = query.Sort switch
        {
            SearchSort.IdDescending => $" ORDER BY {idColumn} DESC",
            SearchSort.Relevance when built.UsesRank => $" ORDER BY ct.[RANK] DESC, {idColumn} ASC",
            _ => $" ORDER BY {idColumn} ASC",
        };
        return built;
    }

    public async Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var built = await BuildAsync(query, cancellationToken).ConfigureAwait(false);
        var names = _factory.Names;
        var c = names.Columns;
        var pageSize = Math.Max(1, Math.Min(1000, query.PageSize));
        var sql = $@"
SELECT p.{names.Col(c.PersonFactsId)}, p.{names.Col(c.Surname)}, p.{names.Col(c.Name)}, p.{names.Col(c.Patronymic)}, p.{names.Col(c.BirthDate)},
       p.{names.Col(c.BirthPlace)}, p.{names.Col(c.All)}, f.{names.Col(c.SourceFilesId)}, f.{names.Col(c.FileCode)}, f.{names.Col(c.FileName)}
       {(built.UsesRank ? ", ct.[RANK]" : ", CAST(NULL AS INT)")}
{built.From}{built.Where}{built.OrderBy}
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;";
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, sql, timeoutSeconds: Math.Max(5, query.TimeoutSeconds));
        command.Parameters.AddRange(built.Parameters.ToArray());
        command.Parameters.Add("@skip", SqlDbType.Int).Value = Math.Max(0, query.Page) * pageSize;
        command.Parameters.Add("@take", SqlDbType.Int).Value = pageSize + 1;
        var page = new SearchPage { Page = query.Page, PageSize = pageSize, UsedFullText = built.UsedFullText };
        page.Notices.AddRange(built.Notices);
        page.HighlightTerms.AddRange(built.Highlights.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase));
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (page.Rows.Count == pageSize)
                {
                    page.HasMore = true;
                    break;
                }

                string Str(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                page.Rows.Add(new SearchResultRow
                {
                    PersonFactId = Convert.ToInt64(reader.GetValue(0)),
                    Surname = Str(1),
                    Name = Str(2),
                    Patronymic = Str(3),
                    BirthDate = reader.IsDBNull(4) ? (DateTime?)null : Convert.ToDateTime(reader.GetValue(4)),
                    BirthPlace = Str(5),
                    AllJson = Str(6),
                    FileId = Convert.ToInt64(reader.GetValue(7)),
                    FileCode = Str(8),
                    FileName = Str(9),
                    Rank = reader.IsDBNull(10) ? (int?)null : Convert.ToInt32(reader.GetValue(10)),
                });
            }
        }

        page.Elapsed = stopwatch.Elapsed;
        return page;
    }

    public async Task<long> CountAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var built = await BuildAsync(query, cancellationToken).ConfigureAwait(false);
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $"SELECT COUNT_BIG(*) {built.From}{built.Where};", timeoutSeconds: Math.Max(5, query.TimeoutSeconds));
        command.Parameters.AddRange(built.Parameters.ToArray());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<ObservationDetails> GetObservationAsync(long personFactId, CancellationToken cancellationToken)
    {
        var names = _factory.Names;
        var c = names.Columns;
        var details = new ObservationDetails { PersonFactId = personFactId };
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var command = _factory.Command(connection, $"SELECT * FROM {names.PF} WHERE {names.Col(c.PersonFactsId)} = @id;"))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = personFactId;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                details.PersonFactsColumns.Add(new KeyValuePair<string, string>(name, Display(value)));
                if (Same(name, c.Surname)) details.Surname = value as string;
                else if (Same(name, c.Name)) details.Name = value as string;
                else if (Same(name, c.Patronymic)) details.Patronymic = value as string;
                else if (Same(name, c.BirthDate)) details.BirthDate = value == null ? (DateTime?)null : Convert.ToDateTime(value);
                else if (Same(name, c.BirthPlace)) details.BirthPlace = value as string;
                else if (Same(name, c.All)) details.AllJson = value as string;
                else if (Same(name, c.FileId)) details.FileId = Convert.ToInt64(value);
            }
        }

        using (var command = _factory.Command(connection, $"SELECT * FROM {names.SF} WHERE {names.Col(c.SourceFilesId)} = @id;"))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = details.FileId;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    details.SourceFilesColumns.Add(new KeyValuePair<string, string>(name, Display(value)));
                    if (Same(name, c.FileCode)) details.FileCode = Convert.ToString(value);
                    else if (Same(name, c.FileName)) details.FileName = Convert.ToString(value);
                    else if (Same(name, "SourcePath")) details.SourcePath = value as string;
                }
            }
        }

        return details;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Display(object value)
    {
        switch (value)
        {
            case null:
                return null;
            case byte[] bytes:
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            case DateTime dateTime:
                return dateTime.TimeOfDay == TimeSpan.Zero
                    ? dateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : dateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }
}
