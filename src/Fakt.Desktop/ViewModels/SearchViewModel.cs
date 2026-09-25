using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Fakt.Core.Extraction;
using Fakt.Core.Search;
using Fakt.Core.Security;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Desktop.ViewModels;

public sealed class SearchResultItem
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public SearchResultItem(SearchResultRow row)
    {
        Row = row;
        FactsSummary = Summarize(row.AllJson);
    }

    public SearchResultRow Row { get; }
    public long PersonFactId => Row.PersonFactId;
    public string Surname => Row.Surname;
    public string Name => Row.Name;
    public string Patronymic => Row.Patronymic;
    public string BirthDate => Row.BirthDate?.ToString("dd.MM.yyyy", Ru);
    public string BirthPlace => Row.BirthPlace;
    public long FileId => Row.FileId;
    public string FileCode => Row.FileCode;
    public string FileName => Row.FileName;

    /// <summary>Краткое представление [ALL]: несколько фактов «Тип: значение».</summary>
    public string FactsSummary { get; }

    private static string Summarize(string json)
    {
        try
        {
            var root = JObject.Parse(json ?? "{}");
            var facts = (root["facts"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var parts = facts.Take(4).Select(f => FactTypes.Title((string)f["type"]) + ": " + FactValueText(f["value"])).ToList();
            if (facts.Count > 4)
            {
                parts.Add($"ещё {facts.Count - 4}");
            }

            var status = (string)root["identity_status"];
            if (status == IdentityStatuses.Unresolved && parts.Count > 0)
            {
                parts.Insert(0, "[лицо не установлено]");
            }

            return parts.Count == 0 ? "—" : string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return "[ALL не является корректным JSON]";
        }
    }

    internal static string FactValueText(JToken value) =>
        value == null ? null : value.Type == JTokenType.Object || value.Type == JTokenType.Array ? value.ToString(Formatting.None) : value.ToString();
}

/// <summary>
/// Страница «Поиск»: строка запроса и фильтры, серверная пагинация по 100 строк, отдельный подсчёт общего
/// числа, отмена и ограничение времени запроса, защита от показа результата устаревшего запроса.
/// Поиск выполняется по Enter или кнопке «Найти», а не по каждому нажатию клавиши.
/// </summary>
public sealed class SearchViewModel : ObservableObject, IPageActivation
{
    private readonly AppServices _services;
    private long _generation;
    private CancellationTokenSource _searchCts;
    private CancellationTokenSource _countCts;
    private string _text;
    private SearchMode _mode = SearchMode.AllWords;
    private SearchSort _sort = SearchSort.Relevance;
    private string _surname;
    private string _name;
    private string _patronymic;
    private bool _namesExact;
    private string _dateFrom;
    private string _dateTo;
    private string _birthPlace;
    private string _factType = string.Empty;
    private string _identifier;
    private bool _identifierPrefix;
    private string _fileName;
    private string _fileCode;
    private string _personFactId;
    private string _fileId;
    private bool _isSearching;
    private string _status;
    private string _error;
    private string _countText;
    private string _notices;
    private int _page;
    private bool _hasMore;
    private bool _hasSearched;
    private SearchQuery _lastQuery;
    private SearchResultItem _selected;
    private ObservationCardViewModel _card;
    private bool _filtersExpanded;
    private IReadOnlyList<string> _highlightTerms = Array.Empty<string>();

    public SearchViewModel(AppServices services)
    {
        _services = services;
        Results = new ObservableCollection<SearchResultItem>();
        SearchCommand = new RelayCommand(() => StartSearch(0));
        ResetCommand = new RelayCommand(Reset);
        CancelCommand = new RelayCommand(Cancel, () => IsSearching);
        NextPageCommand = new RelayCommand(() => StartSearch(_page + 1), () => !IsSearching && _hasMore && _lastQuery != null);
        PrevPageCommand = new RelayCommand(() => StartSearch(_page - 1), () => !IsSearching && _page > 0 && _lastQuery != null);
        OpenCardCommand = new RelayCommand(p => OpenCard(p as SearchResultItem ?? Selected), p => (p as SearchResultItem ?? Selected) != null);
        CloseCardCommand = new RelayCommand(() => Card = null);
        FactTypes = new[] { new KeyValuePair<string, string>(string.Empty, "Любой тип") }.Concat(Fakt.Core.Extraction.FactTypes.Choices()).ToList();
    }

    public ObservableCollection<SearchResultItem> Results { get; }

    public ICommand SearchCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand OpenCardCommand { get; }
    public ICommand CloseCardCommand { get; }

    public IReadOnlyList<KeyValuePair<string, string>> FactTypes { get; }

    public IReadOnlyList<KeyValuePair<SearchMode, string>> Modes { get; } = new[]
    {
        new KeyValuePair<SearchMode, string>(SearchMode.AllWords, "Все слова"),
        new KeyValuePair<SearchMode, string>(SearchMode.AnyWord, "Любое слово"),
        new KeyValuePair<SearchMode, string>(SearchMode.ExactPhrase, "Точная фраза"),
        new KeyValuePair<SearchMode, string>(SearchMode.Substring, "Подстрока (медленно)"),
    };

    public IReadOnlyList<KeyValuePair<SearchSort, string>> Sorts { get; } = new[]
    {
        new KeyValuePair<SearchSort, string>(SearchSort.Relevance, "По релевантности"),
        new KeyValuePair<SearchSort, string>(SearchSort.IdAscending, "По ID наблюдения ↑"),
        new KeyValuePair<SearchSort, string>(SearchSort.IdDescending, "По ID наблюдения ↓"),
    };

    public string Text { get => _text; set => SetProperty(ref _text, value); }
    public SearchMode Mode { get => _mode; set { if (SetProperty(ref _mode, value)) OnPropertyChanged(nameof(ModeHint)); } }
    public SearchSort Sort { get => _sort; set => SetProperty(ref _sort, value); }
    public string Surname { get => _surname; set => SetProperty(ref _surname, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Patronymic { get => _patronymic; set => SetProperty(ref _patronymic, value); }
    public bool NamesExact { get => _namesExact; set => SetProperty(ref _namesExact, value); }
    public string DateFrom { get => _dateFrom; set => SetProperty(ref _dateFrom, value); }
    public string DateTo { get => _dateTo; set => SetProperty(ref _dateTo, value); }
    public string BirthPlace { get => _birthPlace; set => SetProperty(ref _birthPlace, value); }
    public string FactType { get => _factType; set => SetProperty(ref _factType, value); }
    public string Identifier { get => _identifier; set => SetProperty(ref _identifier, value); }
    public bool IdentifierPrefix { get => _identifierPrefix; set => SetProperty(ref _identifierPrefix, value); }
    public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }
    public string FileCode { get => _fileCode; set => SetProperty(ref _fileCode, value); }
    public string PersonFactId { get => _personFactId; set => SetProperty(ref _personFactId, value); }
    public string FileId { get => _fileId; set => SetProperty(ref _fileId, value); }
    public bool FiltersExpanded { get => _filtersExpanded; set => SetProperty(ref _filtersExpanded, value); }

    public string ModeHint => Mode switch
    {
        SearchMode.AllWords => "Полнотекстовый поиск: все слова должны встретиться в одном наблюдении (ФИО, факты, имя и код файла). Символ * в конце слова — поиск по началу слова.",
        SearchMode.AnyWord => "Полнотекстовый поиск: достаточно любого из слов.",
        SearchMode.ExactPhrase => "Полнотекстовый поиск: слова идут подряд.",
        _ => "Подстрока: поиск фрагмента текста (LIKE) без полнотекстового индекса — медленно на больших объёмах и ограничено временем запроса.",
    };

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                CommandManager.InvalidateRequerySuggested();
                OnPropertyChanged(nameof(EmptyStateText));
            }
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(EmptyStateText)); } }
    public string CountText { get => _countText; private set => SetProperty(ref _countText, value); }
    public string Notices { get => _notices; private set => SetProperty(ref _notices, value); }
    public string PageText => _lastQuery == null ? null : $"Страница {_page + 1}";

    public IReadOnlyList<string> HighlightTerms
    {
        get => _highlightTerms;
        private set => SetProperty(ref _highlightTerms, value);
    }

    public SearchResultItem Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>Всплывающая карточка наблюдения (null — закрыта).</summary>
    public ObservationCardViewModel Card
    {
        get => _card;
        private set => SetProperty(ref _card, value);
    }

    public string EmptyStateText =>
        IsSearching ? null :
        Error != null ? null :
        !_hasSearched ? "Введите фамилию, имя, телефон, номер счёта, имя или код файла и нажмите «Найти» (или Enter)." :
        Results.Count == 0 ? "Ничего не найдено. Проверьте условия или используйте режим «Любое слово»; недавно сохранённые записи могут появиться в полнотекстовом поиске с задержкой." : null;

    public void OnActivated()
    {
    }

    private SearchQuery BuildQuery()
    {
        DateTime? ParseDate(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (DateTime.TryParseExact(text.Trim(), new[] { "dd.MM.yyyy", "yyyy-MM-dd", "d.M.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }

            throw new SearchValidationException($"{what}: дата должна быть в формате ДД.ММ.ГГГГ.");
        }

        long? ParseId(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                return id;
            }

            throw new SearchValidationException($"{what}: введите положительное целое число.");
        }

        var from = ParseDate(DateFrom, "Дата рождения с");
        var to = ParseDate(DateTo, "Дата рождения по");
        if (from.HasValue && !to.HasValue && !string.IsNullOrWhiteSpace(DateFrom) && string.IsNullOrWhiteSpace(DateTo))
        {
            // Одна дата без диапазона — точное совпадение.
            to = from;
        }

        return new SearchQuery
        {
            Text = Text,
            Mode = Mode,
            Sort = Sort,
            Surname = Surname,
            Name = Name,
            Patronymic = Patronymic,
            NamesExact = NamesExact,
            BirthDateFrom = from,
            BirthDateTo = to,
            BirthPlace = BirthPlace,
            FactType = string.IsNullOrEmpty(FactType) ? null : FactType,
            Identifier = Identifier,
            IdentifierPrefix = IdentifierPrefix,
            FileName = FileName,
            FileCode = FileCode,
            PersonFactId = ParseId(PersonFactId, "ID наблюдения"),
            FileId = ParseId(FileId, "ID файла"),
            PageSize = 100,
            TimeoutSeconds = 30,
        };
    }

    private void StartSearch(int page)
    {
        SearchQuery query;
        try
        {
            query = page == 0 ? BuildQuery() : _lastQuery.WithPage(page);
            query.Page = page;
        }
        catch (SearchValidationException ex)
        {
            Error = ex.Message;
            return;
        }

        _ = RunSearchAsync(query);
    }

    private async Task RunSearchAsync(SearchQuery query)
    {
        // Новый запрос отменяет предыдущий; результат устаревшего запроса не показывается.
        _searchCts?.Cancel();
        _countCts?.Cancel();
        var generation = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        Error = null;
        Status = "Поиск…";
        try
        {
            _services.Authorization.Demand(Permission.SearchData);
            var page = await Task.Run(() => _services.Search.SearchAsync(query, cts.Token), cts.Token);
            if (generation != Interlocked.Read(ref _generation))
            {
                return;
            }

            _lastQuery = query;
            _page = query.Page;
            _hasMore = page.HasMore;
            _hasSearched = true;
            Results.Clear();
            foreach (var row in page.Rows)
            {
                Results.Add(new SearchResultItem(row));
            }

            HighlightTerms = page.HighlightTerms;
            Notices = page.Notices.Count == 0 ? null : string.Join(Environment.NewLine, page.Notices.Distinct());
            Status = $"Строк на странице: {page.Rows.Count}{(page.HasMore ? " (есть следующая страница)" : string.Empty)} · {page.Elapsed.TotalMilliseconds:0} мс" +
                     (page.UsedFullText ? " · полнотекстовый индекс" : string.Empty);
            OnPropertiesChanged(nameof(PageText), nameof(EmptyStateText));
            if (query.Page == 0)
            {
                _ = CountAsync(query, generation);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                Status = "Поиск отменён.";
            }
        }
        catch (SearchValidationException ex)
        {
            Error = ex.Message;
            Status = null;
        }
        catch (Exception ex) when (ex is System.Data.SqlClient.SqlException || ex is InvalidOperationException || ex is AccessDeniedException)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                var (_, message) = Fakt.Infrastructure.Sql.SqlConnectionFactory.Describe(ex);
                Error = ex is System.Data.SqlClient.SqlException sql && sql.Number == -2
                    ? "Превышено время выполнения поиска (30 с). Уточните условия: добавьте слова или фильтры; режим «Подстрока» на больших объёмах медленный."
                    : message;
                Status = null;
            }
        }
        finally
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Точное общее количество — отдельным запросом; размер страницы не выдаётся за общее количество.</summary>
    private async Task CountAsync(SearchQuery query, long generation)
    {
        _countCts?.Cancel();
        var cts = new CancellationTokenSource();
        _countCts = cts;
        CountText = "Всего: подсчёт…";
        try
        {
            var count = await Task.Run(() => _services.Search.CountAsync(query, cts.Token), cts.Token);
            if (generation == Interlocked.Read(ref _generation))
            {
                CountText = $"Всего найдено: {count:N0}";
            }
        }
        catch (Exception) when (generation == Interlocked.Read(ref _generation))
        {
            CountText = "Точное количество не рассчитано (запрос подсчёта не уложился во время или отменён).";
        }
        catch (Exception)
        {
        }
    }

    private void Cancel()
    {
        _searchCts?.Cancel();
        _countCts?.Cancel();
    }

    private void Reset()
    {
        Cancel();
        Interlocked.Increment(ref _generation);
        Text = Surname = Name = Patronymic = DateFrom = DateTo = BirthPlace = Identifier = FileName = FileCode = PersonFactId = FileId = null;
        FactType = string.Empty;
        NamesExact = IdentifierPrefix = false;
        Mode = SearchMode.AllWords;
        Sort = SearchSort.Relevance;
        Results.Clear();
        _lastQuery = null;
        _hasSearched = false;
        Error = Status = CountText = Notices = null;
        IsSearching = false;
        OnPropertiesChanged(nameof(PageText), nameof(EmptyStateText));
    }

    private void OpenCard(SearchResultItem item)
    {
        if (item == null)
        {
            return;
        }

        var card = new ObservationCardViewModel(_services, item.PersonFactId, HighlightTerms, () => Card = null);
        Card = card;
        _ = card.LoadAsync();
    }
}

public sealed class FactGroupViewModel
{
    public string Title { get; set; }
    public List<FactLineViewModel> Items { get; set; }
}

public sealed class FactLineViewModel
{
    public string Value { get; set; }
    public string Normalized { get; set; }
    public string Label { get; set; }
    public string Source { get; set; }
    public string Evidence { get; set; }
}

public sealed class NameValue
{
    public NameValue(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }

    /// <summary>Значение для показа: пустое поле отображается прочерком, чтобы не выглядеть как незаполненный ввод.</summary>
    public string DisplayValue => string.IsNullOrWhiteSpace(Value) ? "—" : Value;
}

/// <summary>
/// Карточка наблюдения: основные поля, все факты из [ALL] по группам, неразрешённые значения,
/// предупреждения, происхождение (source_row_id, модель, время), все поля обеих таблиц и форматированный JSON.
/// Карточки разных людей не объединяются.
/// </summary>
public sealed class ObservationCardViewModel : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly AppServices _services;
    private readonly long _id;
    private bool _isLoading = true;
    private string _error;
    private string _json;
    private string _copyStatus;

    public ObservationCardViewModel(AppServices services, long personFactId, IReadOnlyList<string> highlightTerms, Action close)
    {
        _services = services;
        _id = personFactId;
        HighlightTerms = highlightTerms ?? Array.Empty<string>();
        CloseCommand = new RelayCommand(close);
        CopyJsonCommand = new RelayCommand(() => CopyStatus = services.Dialogs.CopyToClipboard(Json) ? "JSON скопирован" : "Буфер обмена занят другим приложением");
        CopyCardCommand = new RelayCommand(() => CopyStatus = services.Dialogs.CopyToClipboard(CardText()) ? "Карточка скопирована" : "Буфер обмена занят другим приложением");
        ShowFileCommand = new RelayCommand(ShowFile, () => SourcePath != null);
    }

    public ICommand CloseCommand { get; }
    public ICommand CopyJsonCommand { get; }
    public ICommand CopyCardCommand { get; }
    public ICommand ShowFileCommand { get; }

    public IReadOnlyList<string> HighlightTerms { get; }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string CopyStatus { get => _copyStatus; private set => SetProperty(ref _copyStatus, value); }

    public string Title { get; private set; }
    public string IdentityText { get; private set; }
    public string KindText { get; private set; }
    // Списки заполняются после появления карточки на экране; List<T> не сообщает о добавлении элементов,
    // поэтому по завершении загрузки свойства получают новые экземпляры (см. LoadAsync).
    public List<NameValue> MainFields { get; private set; } = new();
    public List<FactGroupViewModel> FactGroups { get; private set; } = new();
    public List<NameValue> Unresolved { get; private set; } = new();
    public List<string> Warnings { get; private set; } = new();
    public List<NameValue> Rejected { get; private set; } = new();
    public List<NameValue> Provenance { get; private set; } = new();
    public List<NameValue> PersonFactsColumns { get; private set; } = new();
    public List<NameValue> SourceFilesColumns { get; private set; } = new();
    public string SourcePath { get; private set; }

    public string Json { get => _json; private set => SetProperty(ref _json, value); }

    public bool HasFacts => FactGroups.Count > 0;
    public bool HasUnresolved => Unresolved.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasRejected => Rejected.Count > 0;

    public async Task LoadAsync()
    {
        try
        {
            var details = await Task.Run(() => _services.Search.GetObservationAsync(_id, CancellationToken.None));
            if (details == null)
            {
                Error = $"Наблюдение {_id} не найдено (возможно, удалено).";
                return;
            }

            Fill(details);
        }
        catch (Exception ex)
        {
            Error = Fakt.Infrastructure.Sql.SqlConnectionFactory.Describe(ex).Message;
        }
        finally
        {
            MainFields = MainFields.ToList();
            FactGroups = FactGroups.ToList();
            Unresolved = Unresolved.ToList();
            Warnings = Warnings.ToList();
            Rejected = Rejected.ToList();
            Provenance = Provenance.ToList();
            PersonFactsColumns = PersonFactsColumns.ToList();
            SourceFilesColumns = SourceFilesColumns.ToList();
            IsLoading = false;
            OnPropertiesChanged(nameof(Title), nameof(IdentityText), nameof(KindText), nameof(MainFields), nameof(FactGroups), nameof(Unresolved),
                nameof(Warnings), nameof(Rejected), nameof(Provenance), nameof(PersonFactsColumns), nameof(SourceFilesColumns), nameof(SourcePath),
                nameof(HasFacts), nameof(HasUnresolved), nameof(HasWarnings), nameof(HasRejected));
        }
    }

    private void Fill(ObservationDetails details)
    {
        var fio = string.Join(" ", new[] { details.Surname, details.Name, details.Patronymic }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Title = fio.Length > 0 ? fio : "Лицо не установлено";
        SourcePath = details.SourcePath;
        MainFields.Add(new NameValue("Фамилия", details.Surname));
        MainFields.Add(new NameValue("Имя", details.Name));
        MainFields.Add(new NameValue("Отчество", details.Patronymic));
        MainFields.Add(new NameValue("Дата рождения", details.BirthDate?.ToString("dd.MM.yyyy", Ru)));
        MainFields.Add(new NameValue("Место рождения", details.BirthPlace));
        MainFields.Add(new NameValue("ID наблюдения (PersonFacts.ID)", details.PersonFactId.ToString(Ru)));
        MainFields.Add(new NameValue("ID файла (SourceFiles.ID)", details.FileId.ToString(Ru)));
        MainFields.Add(new NameValue("Код файла", details.FileCode));
        MainFields.Add(new NameValue("Имя файла", details.FileName));

        JObject root = null;
        try
        {
            root = JObject.Parse(details.AllJson ?? "{}");
            Json = root.ToString(Formatting.Indented);
        }
        catch (JsonException)
        {
            Json = details.AllJson;
            Warnings.Add("Поле [ALL] не является корректным JSON; показано как есть.");
        }

        if (root != null)
        {
            var identity = (string)root["identity_status"];
            IdentityText = IdentityStatuses.Title(identity);
            KindText = (string)root["kind"] == ObservationKinds.UnassignedFacts
                ? "Факты строки без установленной принадлежности конкретному лицу (неразрешённое наблюдение)"
                : null;
            foreach (var group in (root["facts"] as JArray ?? new JArray()).OfType<JObject>().GroupBy(f => (string)f["type"]))
            {
                FactGroups.Add(new FactGroupViewModel
                {
                    Title = FactTypes.Title(group.Key),
                    Items = group.Select(f => new FactLineViewModel
                    {
                        Value = SearchResultItem.FactValueText(f["value"]),
                        Normalized = (string)f["normalized_value"] is var n && n != SearchResultItem.FactValueText(f["value"]) ? n : null,
                        Label = (string)f["label"],
                        Source = (string)f["source_column"],
                        Evidence = (string)f["evidence"],
                    }).ToList(),
                });
            }

            foreach (var item in (root["unresolved_fields"] as JArray ?? new JArray()).OfType<JObject>())
            {
                Unresolved.Add(new NameValue(MainFieldsTitle((string)item["field"]), $"«{(string)item["raw_value"]}» — {(string)item["reason"]}" +
                                                                                      ((string)item["source_column"] is { } column ? $" (столбец «{column}»)" : string.Empty)));
            }

            foreach (var warning in (root["warnings"] as JArray ?? new JArray()).Select(w => (string)w).Where(w => !string.IsNullOrWhiteSpace(w)))
            {
                Warnings.Add(warning);
            }

            foreach (var rejected in (root["rejected_candidates"] as JArray ?? new JArray()).OfType<JObject>())
            {
                Rejected.Add(new NameValue((string)rejected["name"], $"«{(string)rejected["value"]}» — {(string)rejected["reason"]}"));
            }

            if (root["field_provenance"] is JObject provenance)
            {
                foreach (var property in provenance.Properties())
                {
                    Provenance.Add(new NameValue("Источник поля «" + MainFieldsTitle(property.Name) + "»",
                        $"{(string)property.Value["source_column"] ?? "—"}: «{(string)property.Value["evidence"]}»"));
                }
            }

            if (root["provenance"] is JObject p)
            {
                Provenance.Add(new NameValue("source_row_id", (string)p["source_row_id"]));
                Provenance.Add(new NameValue("Строка файла", p["source_line"]?.ToString()));
                Provenance.Add(new NameValue("Провайдер", (string)p["provider"]));
                Provenance.Add(new NameValue("Модель", (string)p["model"]));
                Provenance.Add(new NameValue("Версия промпта", (string)p["prompt_version"]));
                Provenance.Add(new NameValue("Версия извлечения", (string)p["extraction_version"]));
                Provenance.Add(new NameValue("Задание", p["job_id"]?.ToString()));
                Provenance.Add(new NameValue("Обработано (UTC)", (string)p["processed_at_utc"]));
                Provenance.Add(new NameValue("Хеш записи", (string)p["row_hash"]));
            }
        }

        PersonFactsColumns.AddRange(details.PersonFactsColumns.Select(c => new NameValue(c.Key, c.Key.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? "(JSON — см. ниже)" : c.Value)));
        SourceFilesColumns.AddRange(details.SourceFilesColumns.Select(c => new NameValue(c.Key, c.Value)));
    }

    private static string MainFieldsTitle(string field) => Fakt.Core.Extraction.MainFields.Title(field);

    private string CardText()
    {
        var lines = new List<string> { Title };
        lines.AddRange(MainFields.Where(f => f.Value != null).Select(f => $"{f.Name}: {f.Value}"));
        foreach (var group in FactGroups)
        {
            lines.AddRange(group.Items.Select(i => $"{group.Title}: {i.Value}" + (i.Label != null ? $" ({i.Label})" : string.Empty)));
        }

        lines.AddRange(Unresolved.Select(u => $"Неразрешено — {u.Name}: {u.Value}"));
        return string.Join(Environment.NewLine, lines);
    }

    private void ShowFile()
    {
        if (SourcePath == null)
        {
            return;
        }

        if (!System.IO.File.Exists(SourcePath))
        {
            CopyStatus = "Исходный файл не найден по сохранённому пути.";
            return;
        }

        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + SourcePath + "\"");
    }
}
