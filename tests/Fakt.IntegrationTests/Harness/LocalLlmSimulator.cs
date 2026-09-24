using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Testing;

/// <summary>Вид ошибки, которую внедряет имитатор.</summary>
public enum SimulatedFault
{
    None,

    /// <summary>HTTP 429 с Retry-After.</summary>
    RateLimited,

    /// <summary>HTTP 503: временная ошибка сервера без Retry-After.</summary>
    ServerError,

    /// <summary>Тайм-аут ожидания ответа.</summary>
    Timeout,

    /// <summary>JSON оборван посередине при finish_reason = stop (некорректный ответ).</summary>
    InvalidJson,

    /// <summary>Ответ усечён пределом выходных токенов (finish_reason = length).</summary>
    LengthTruncation,

    /// <summary>В ответе нет результата для последней записи пакета.</summary>
    MissingId,

    /// <summary>Результат первой записи пакета повторён дважды.</summary>
    DuplicateId,

    /// <summary>В ответе есть результат с неизвестным source_row_id.</summary>
    UnknownId,
}

/// <summary>Параметры имитатора: задержка и доли внедряемых ошибок (0..1 на запрос).</summary>
public sealed class LlmSimulatorOptions
{
    public int BaseLatencyMs { get; set; }

    public int PerRecordLatencyMs { get; set; }

    /// <summary>Детерминированная добавка 0..JitterMs по отпечатку запроса.</summary>
    public int JitterMs { get; set; }

    public int Seed { get; set; } = 20260924;

    public double RateLimitRate { get; set; }

    public int RetryAfterMs { get; set; } = 200;

    public double ServerErrorRate { get; set; }

    public double TimeoutRate { get; set; }

    public double InvalidJsonRate { get; set; }

    /// <summary>Внимание: усечение по пределу токенов уменьшает размер пакета задания до конца задания (поведение продукта).</summary>
    public double LengthTruncationRate { get; set; }

    public double MissingIdRate { get; set; }

    public double DuplicateIdRate { get; set; }

    public double UnknownIdRate { get; set; }

    /// <summary>Ошибки, которые внедряются по одной в первые подходящие запросы (детерминированный сценарий теста).</summary>
    public List<SimulatedFault> ScheduledFaults { get; set; } = new();

    /// <summary>Сколько последних записей, уже получивших ошибку, помнит имитатор (ограничение памяти).</summary>
    public int RecentFaultMemory { get; set; } = 65536;

    public double TotalRate =>
        RateLimitRate + ServerErrorRate + TimeoutRate + InvalidJsonRate + LengthTruncationRate + MissingIdRate + DuplicateIdRate + UnknownIdRate;

    public string Describe()
    {
        var parts = new List<string>
        {
            $"задержка {BaseLatencyMs} мс + {PerRecordLatencyMs} мс/запись" + (JitterMs > 0 ? $" + 0..{JitterMs} мс" : string.Empty),
        };
        if (TotalRate > 0)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture,
                "ошибки: 429 {0:P1}, 503 {1:P1}, тайм-аут {2:P1}, битый JSON {3:P1}, length {4:P1}, пропуск ID {5:P1}, повтор ID {6:P1}, чужой ID {7:P1}",
                RateLimitRate, ServerErrorRate, TimeoutRate, InvalidJsonRate, LengthTruncationRate, MissingIdRate, DuplicateIdRate, UnknownIdRate));
        }
        else
        {
            parts.Add("без внедрения ошибок");
        }

        if (ScheduledFaults.Count > 0)
        {
            parts.Add("по расписанию: " + string.Join(", ", ScheduledFaults));
        }

        return string.Join("; ", parts);
    }
}

/// <summary>Сведения о запросе для тестовых хуков.</summary>
public sealed class SimulatorRequestInfo
{
    public long CallNumber { get; set; }

    public string Purpose { get; set; }

    public IReadOnlyList<long> Ordinals { get; set; }

    public SimulatedFault Fault { get; set; }
}

/// <summary>Потокобезопасные счётчики имитатора.</summary>
public sealed class LlmSimulatorStats
{
    private long _calls;
    private long _records;
    private long _succeeded;
    private long _latencyTicks;
    private readonly long[] _faults = new long[Enum.GetValues(typeof(SimulatedFault)).Length];

    public long Calls => Interlocked.Read(ref _calls);

    public long RecordsSeen => Interlocked.Read(ref _records);

    public long Succeeded => Interlocked.Read(ref _succeeded);

    public TimeSpan SimulatedLatency => TimeSpan.FromTicks(Interlocked.Read(ref _latencyTicks));

    public long Faults(SimulatedFault fault) => Interlocked.Read(ref _faults[(int)fault]);

    public long TotalFaults => Enum.GetValues(typeof(SimulatedFault)).Cast<SimulatedFault>().Where(f => f != SimulatedFault.None).Sum(Faults);

    internal long NextCall() => Interlocked.Increment(ref _calls);

    internal void AddRecords(int count) => Interlocked.Add(ref _records, count);

    internal void AddSuccess() => Interlocked.Increment(ref _succeeded);

    internal void AddFault(SimulatedFault fault) => Interlocked.Increment(ref _faults[(int)fault]);

    internal void AddLatency(TimeSpan latency) => Interlocked.Add(ref _latencyTicks, latency.Ticks);

    public Dictionary<string, long> FaultCounts() =>
        Enum.GetValues(typeof(SimulatedFault)).Cast<SimulatedFault>().Where(f => f != SimulatedFault.None).ToDictionary(f => f.ToString(), Faults);
}

/// <summary>
/// ЛОКАЛЬНЫЙ ИМИТАТОР LLM для нагрузочного теста и тестов отказов. Это НЕ провайдер и НЕ доказательство работы
/// реальных API: ответ строится детерминированно по именам колонок записи (ФИО, дата и место рождения, телефон,
/// e-mail, счёт…), значения и фрагменты evidence копируются из текста записи. Ответ проходит ту же проверку
/// (ExtractionValidator), что и ответ настоящей модели. Поддерживаются задержка и внедрение ошибок: 429, 503,
/// тайм-аут, оборванный JSON, усечение по пределу токенов, пропущенные, повторённые и неизвестные source_row_id.
/// Ошибка внедряется в запрос, только если ни одна его запись недавно не получала ошибку — поэтому каждая запись
/// переживает не более одной внедрённой ошибки и итоговый результат совпадает с прогоном без ошибок.
/// </summary>
public sealed class LocalLlmSimulator : ILlmAdapter
{
    public const string ProviderId = "fakt-local-llm-simulator";
    public const string SimulatedModelId = "fakt-llm-simulator-1";

    private readonly object _gate = new();
    private readonly HashSet<ulong> _recentFaulted = new();
    private readonly Queue<ulong> _recentOrder = new();
    private int _scheduledIndex;

    public LocalLlmSimulator(LlmSimulatorOptions options = null)
    {
        Options = options ?? new LlmSimulatorOptions();
        Descriptor = new LlmProviderDescriptor
        {
            Id = ProviderId,
            DisplayName = "Локальный имитатор LLM (только для тестов, не провайдер)",
            DefaultBaseUrl = "http://127.0.0.1/fakt-llm-simulator",
            ApiKey = ApiKeyRequirement.NotUsed,
            IsLocalByDefault = true,
            SupportsModelListing = true,
            DocumentedModes = new[] { StructuredOutputMode.JsonSchema },
            Notes = "Имитатор для нагрузочных тестов конвейера. Не является доказательством работы провайдера.",
        };
    }

    public LlmSimulatorOptions Options { get; }

    public LlmSimulatorStats Stats { get; } = new();

    public LlmProviderDescriptor Descriptor { get; }

    /// <summary>Тестовый хук: вызывается перед ответом (может ждать, например, до запроса паузы).</summary>
    public Func<SimulatorRequestInfo, Task> BeforeRespond { get; set; }

    public Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(new ModelListResult
        {
            Status = ModelListStatus.Loaded,
            Models = new[] { new ModelInfo { Id = SimulatedModelId, DisplayName = "Имитатор LLM", Kind = "llm" } },
            PagesFetched = 1,
        });

    public async Task<LlmResponse> CompleteJsonAsync(LlmRuntimeConfig config, LlmJsonRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Stats.NextCall();
        var requestId = "sim-" + call.ToString(CultureInfo.InvariantCulture);
        if (request.Purpose != "extraction" && request.Purpose != "extraction_test")
        {
            throw new LlmException(LlmErrorKind.NotSupported, "Имитатор LLM поддерживает только запросы извлечения фактов (purpose = extraction).", requestId: requestId);
        }

        var records = ParseRecords(request.UserContent);
        Stats.AddRecords(records.Count);
        var fingerprint = Hash64(Options.Seed.ToString(CultureInfo.InvariantCulture) + "\n" + request.UserContent);
        var fault = DecideFault(fingerprint, records);
        var info = new SimulatorRequestInfo { CallNumber = call, Purpose = request.Purpose, Ordinals = records.Select(r => r.Ordinal).ToList(), Fault = fault };
        if (BeforeRespond != null)
        {
            await BeforeRespond(info).ConfigureAwait(false);
        }

        switch (fault)
        {
            case SimulatedFault.RateLimited:
                Stats.AddFault(fault);
                throw new LlmException(LlmErrorKind.RateLimited, "Имитатор: превышен лимит запросов (HTTP 429).", 429, requestId, TimeSpan.FromMilliseconds(Options.RetryAfterMs));
            case SimulatedFault.ServerError:
                Stats.AddFault(fault);
                throw new LlmException(LlmErrorKind.ServerError, "Имитатор: временная ошибка сервера (HTTP 503).", 503, requestId);
            case SimulatedFault.Timeout:
                Stats.AddFault(fault);
                throw new LlmException(LlmErrorKind.Timeout, "Имитатор: превышено время ожидания ответа.", null, requestId);
        }

        var latency = TimeSpan.FromMilliseconds(Options.BaseLatencyMs + Options.PerRecordLatencyMs * records.Count +
                                                (Options.JitterMs > 0 ? (int)(fingerprint % (ulong)(Options.JitterMs + 1)) : 0));
        if (latency > TimeSpan.Zero)
        {
            await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
            Stats.AddLatency(latency);
        }

        var rows = new JArray();
        foreach (var record in records)
        {
            rows.Add(BuildRow(record));
        }

        var finish = LlmFinishReason.Stop;
        switch (fault)
        {
            case SimulatedFault.MissingId when rows.Count > 0:
                rows.RemoveAt(rows.Count - 1);
                break;
            case SimulatedFault.DuplicateId when rows.Count > 0:
                rows.Add(rows[0].DeepClone());
                break;
            case SimulatedFault.UnknownId:
                var foreign = rows.Count > 0 ? (JObject)rows[0].DeepClone() : new JObject { ["status"] = "no_facts", ["persons"] = new JArray(), ["unassigned_facts"] = new JArray() };
                foreign["source_row_id"] = "r999999999999";
                rows.Add(foreign);
                break;
        }

        var text = new JObject { ["rows"] = rows }.ToString(Formatting.None);
        if (fault == SimulatedFault.InvalidJson || fault == SimulatedFault.LengthTruncation)
        {
            text = text.Substring(0, Math.Max(1, text.Length * 3 / 5));
            finish = fault == SimulatedFault.LengthTruncation ? LlmFinishReason.Length : LlmFinishReason.Stop;
        }

        if (fault != SimulatedFault.None)
        {
            Stats.AddFault(fault);
        }

        Stats.AddSuccess();
        return new LlmResponse
        {
            Text = text,
            FinishReason = finish,
            RawFinishReason = finish == LlmFinishReason.Length ? "length" : "stop",
            // Условные токены: собственная оценка продукта, чтобы калибровка оценщика не смещалась.
            InputTokens = TokenEstimator.EstimateRaw(request.SystemPrompt) + TokenEstimator.EstimateRaw(request.UserContent) + 400,
            OutputTokens = TokenEstimator.EstimateRaw(text),
            RequestId = requestId,
            Latency = latency,
            ModelReported = SimulatedModelId,
        };
    }

    private sealed class ParsedRecord
    {
        public string RowId { get; set; }
        public long Ordinal { get; set; }
        public JObject Fields { get; set; }
        public ulong Hash { get; set; }
    }

    private static List<ParsedRecord> ParseRecords(string userContent)
    {
        var result = new List<ParsedRecord>();
        foreach (var raw in (userContent ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("{", StringComparison.Ordinal))
            {
                continue;
            }

            var item = JObject.Parse(line);
            var rowId = (string)item["source_row_id"];
            long.TryParse(rowId?.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal);
            result.Add(new ParsedRecord { RowId = rowId, Ordinal = ordinal, Fields = item["fields"] as JObject ?? new JObject(), Hash = Hash64(line) });
        }

        return result;
    }

    private SimulatedFault DecideFault(ulong fingerprint, List<ParsedRecord> records)
    {
        lock (_gate)
        {
            if (records.Any(r => _recentFaulted.Contains(r.Hash)))
            {
                return SimulatedFault.None;
            }

            SimulatedFault fault;
            if (_scheduledIndex < Options.ScheduledFaults.Count)
            {
                fault = Options.ScheduledFaults[_scheduledIndex++];
            }
            else
            {
                fault = PickByRate(fingerprint);
            }

            if (fault != SimulatedFault.None)
            {
                foreach (var record in records)
                {
                    Remember(record.Hash);
                }
            }

            return fault;
        }
    }

    private SimulatedFault PickByRate(ulong fingerprint)
    {
        var o = Options;
        if (o.TotalRate <= 0)
        {
            return SimulatedFault.None;
        }

        var u = (Mix(fingerprint) >> 11) * (1.0 / (1UL << 53));
        var edges = new[]
        {
            (o.RateLimitRate, SimulatedFault.RateLimited), (o.ServerErrorRate, SimulatedFault.ServerError), (o.TimeoutRate, SimulatedFault.Timeout),
            (o.InvalidJsonRate, SimulatedFault.InvalidJson), (o.LengthTruncationRate, SimulatedFault.LengthTruncation), (o.MissingIdRate, SimulatedFault.MissingId),
            (o.DuplicateIdRate, SimulatedFault.DuplicateId), (o.UnknownIdRate, SimulatedFault.UnknownId),
        };
        var edge = 0.0;
        foreach (var (rate, kind) in edges)
        {
            edge += rate;
            if (u < edge)
            {
                return kind;
            }
        }

        return SimulatedFault.None;
    }

    private void Remember(ulong hash)
    {
        if (!_recentFaulted.Add(hash))
        {
            return;
        }

        _recentOrder.Enqueue(hash);
        while (_recentOrder.Count > Math.Max(1024, Options.RecentFaultMemory))
        {
            _recentFaulted.Remove(_recentOrder.Dequeue());
        }
    }

    // ---------- Построение результата одной записи ----------

    private enum ColumnKind
    {
        Ignore,
        FullName,
        Surname,
        Name,
        Patronymic,
        BirthDate,
        BirthPlace,
        Fact,
    }

    private static (ColumnKind Kind, string FactType) Classify(string column)
    {
        var c = (column ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е').Replace('_', ' ');
        if (c.Contains("фио") || c.Contains("ф.и.о") || c == "full name" || c == "fullname")
        {
            return (ColumnKind.FullName, null);
        }

        if (c.StartsWith("фамилия", StringComparison.Ordinal) || c == "surname" || c == "last name" || c == "lastname")
        {
            return (ColumnKind.Surname, null);
        }

        if (c.StartsWith("отчество", StringComparison.Ordinal) || c == "patronymic" || c == "middle name")
        {
            return (ColumnKind.Patronymic, null);
        }

        if (c == "имя" || c == "name" || c == "first name" || c == "firstname")
        {
            return (ColumnKind.Name, null);
        }

        if (c.Contains("дата рождения") || c == "birth date" || c == "birthdate" || c == "др")
        {
            return (ColumnKind.BirthDate, null);
        }

        if (c.Contains("место рождения") || c == "birth place" || c == "birthplace")
        {
            return (ColumnKind.BirthPlace, null);
        }

        if (c.Contains("телефон") || c.Contains("phone") || c.StartsWith("тел", StringComparison.Ordinal))
        {
            return (ColumnKind.Fact, FactTypes.Phone);
        }

        if (c.Contains("email") || c.Contains("e-mail") || c.Contains("почта"))
        {
            return (ColumnKind.Fact, FactTypes.Email);
        }

        if (c.Contains("счет") || c.Contains("account"))
        {
            return (ColumnKind.Fact, FactTypes.BankAccount);
        }

        if (c.Contains("карт") || c.Contains("card"))
        {
            return (ColumnKind.Fact, FactTypes.BankCard);
        }

        if (c == "инн" || c.StartsWith("инн ", StringComparison.Ordinal))
        {
            return (ColumnKind.Fact, FactTypes.Inn);
        }

        if (c.Contains("снилс"))
        {
            return (ColumnKind.Fact, FactTypes.Snils);
        }

        if (c.Contains("паспорт"))
        {
            return (ColumnKind.Fact, FactTypes.Document);
        }

        if (c.Contains("адрес"))
        {
            return (ColumnKind.Fact, FactTypes.Address);
        }

        if (c.Contains("место работы") || c.Contains("организация") || c.Contains("работодатель"))
        {
            return (ColumnKind.Fact, FactTypes.Workplace);
        }

        if (c.Contains("должность"))
        {
            return (ColumnKind.Fact, FactTypes.Position);
        }

        if (c.Contains("госномер") || c.Contains("гос. номер") || c.Contains("гос.номер"))
        {
            return (ColumnKind.Fact, FactTypes.LicensePlate);
        }

        if (c == "vin")
        {
            return (ColumnKind.Fact, FactTypes.Vin);
        }

        return (ColumnKind.Ignore, null);
    }

    private static JObject BuildRow(ParsedRecord record)
    {
        string surname = null, name = null, patronymic = null, birthPlace = null, birthDate = null;
        var sources = new JArray();
        var facts = new JArray();
        var unresolved = new JArray();

        void Source(string field, string column, string evidence) =>
            sources.Add(new JObject { ["field"] = field, ["source_column"] = column, ["evidence"] = evidence });

        foreach (var property in record.Fields.Properties())
        {
            var value = property.Value.Type == JTokenType.String ? (string)property.Value : property.Value.ToString(Formatting.None);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var column = property.Name;
            var trimmed = value.Trim();
            var (kind, factType) = Classify(column);
            switch (kind)
            {
                case ColumnKind.FullName when surname == null:
                {
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    surname = parts.Length > 0 ? parts[0] : null;
                    name = parts.Length > 1 ? parts[1] : null;
                    patronymic = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : null;
                    foreach (var (field, part) in new[] { (MainFields.Surname, surname), (MainFields.Name, name), (MainFields.Patronymic, patronymic) })
                    {
                        if (part != null)
                        {
                            Source(field, column, trimmed);
                        }
                    }

                    break;
                }

                case ColumnKind.Surname when surname == null:
                    surname = trimmed;
                    Source(MainFields.Surname, column, trimmed);
                    break;
                case ColumnKind.Name when name == null:
                    name = trimmed;
                    Source(MainFields.Name, column, trimmed);
                    break;
                case ColumnKind.Patronymic when patronymic == null:
                    patronymic = trimmed;
                    Source(MainFields.Patronymic, column, trimmed);
                    break;
                case ColumnKind.BirthPlace when birthPlace == null:
                    birthPlace = trimmed;
                    Source(MainFields.BirthPlace, column, trimmed);
                    break;
                case ColumnKind.BirthDate when birthDate == null:
                    if (TryParseFullDate(trimmed, out var iso))
                    {
                        birthDate = iso;
                        Source(MainFields.BirthDate, column, trimmed);
                    }
                    else
                    {
                        unresolved.Add(new JObject
                        {
                            ["field"] = MainFields.BirthDate,
                            ["raw_value"] = trimmed,
                            ["reason"] = "Дата неполная, неоднозначная или некорректная",
                            ["source_column"] = column,
                        });
                    }

                    break;
                case ColumnKind.Fact:
                    foreach (var piece in factType == FactTypes.Phone ? trimmed.Split(',', ';') : new[] { trimmed })
                    {
                        var item = piece.Trim();
                        if (item.Length == 0)
                        {
                            continue;
                        }

                        facts.Add(new JObject
                        {
                            ["type"] = factType,
                            ["label"] = null,
                            ["value"] = item,
                            ["normalized_value"] = factType == FactTypes.Phone ? NormalizePhone(item) : null,
                            ["source_column"] = column,
                            ["evidence"] = item,
                        });
                    }

                    break;
            }
        }

        var hasPerson = surname != null || name != null || patronymic != null || birthDate != null || birthPlace != null;
        var row = new JObject { ["source_row_id"] = record.RowId };
        if (!hasPerson && facts.Count == 0 && unresolved.Count == 0)
        {
            row["status"] = "no_facts";
            row["persons"] = new JArray();
            row["unassigned_facts"] = new JArray();
            return row;
        }

        row["status"] = "extracted";
        if (hasPerson || unresolved.Count > 0)
        {
            var present = (surname != null ? 1 : 0) + (name != null ? 1 : 0) + (patronymic != null ? 1 : 0);
            row["persons"] = new JArray(new JObject
            {
                ["person_index"] = 0,
                ["surname"] = surname,
                ["name"] = name,
                ["patronymic"] = patronymic,
                ["birth_date"] = birthDate,
                ["birth_place"] = birthPlace,
                ["identity_status"] = present == 3 ? IdentityStatuses.Identified : present == 0 ? IdentityStatuses.Unresolved : IdentityStatuses.Partial,
                ["field_sources"] = sources,
                ["facts"] = facts,
                ["unresolved_fields"] = unresolved,
                ["warnings"] = new JArray(),
            });
            row["unassigned_facts"] = new JArray();
        }
        else
        {
            row["persons"] = new JArray();
            row["unassigned_facts"] = facts;
        }

        return row;
    }

    private static bool TryParseFullDate(string value, out string iso)
    {
        iso = null;
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            date.Year >= BirthDateChecker.MinYear && date <= DateTime.Today)
        {
            iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static string NormalizePhone(string value)
    {
        var digits = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch >= '0' && ch <= '9')
            {
                digits.Append(ch);
            }
        }

        return value.TrimStart().StartsWith("+", StringComparison.Ordinal) ? "+" + digits : digits.ToString();
    }

    private static ulong Hash64(string text)
    {
        // FNV-1a 64: детерминированный отпечаток, одинаковый между запусками (string.GetHashCode не подходит).
        var hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong x)
    {
        x ^= x >> 33;
        x *= 0xff51afd7ed558ccdUL;
        x ^= x >> 33;
        x *= 0xc4ceb9fe1a85ec53UL;
        x ^= x >> 33;
        return x;
    }
}

/// <summary>Реестр адаптеров, в котором есть только имитатор.</summary>
public sealed class SimulatorAdapterRegistry : ILlmAdapterRegistry
{
    private readonly LocalLlmSimulator _simulator;

    public SimulatorAdapterRegistry(LocalLlmSimulator simulator)
    {
        _simulator = simulator;
        Providers = new[] { simulator.Descriptor };
    }

    public IReadOnlyList<LlmProviderDescriptor> Providers { get; }

    public ILlmAdapter Get(string providerId) =>
        providerId == LocalLlmSimulator.ProviderId
            ? _simulator
            : throw new LlmException(LlmErrorKind.Configuration, "В тестовом окружении доступен только локальный имитатор LLM.");
}
