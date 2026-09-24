using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Records;
using Fakt.Core.Security;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Newtonsoft.Json;

namespace Fakt.UnitTests.TestSupport;

/// <summary>Адаптер LLM с заранее заданными ответами; считает вызовы и одновременность.</summary>
internal sealed class ScriptedLlmAdapter : ILlmAdapter
{
    private readonly Queue<Func<LlmJsonRequest, CancellationToken, Task<LlmResponse>>> _steps = new();
    private readonly List<LlmJsonRequest> _requests = new();
    private int _calls;
    private int _inside;
    private int _maxInside;

    public LlmProviderDescriptor Descriptor { get; } = new() { Id = "scripted", DisplayName = "Scripted", ApiKey = ApiKeyRequirement.Optional };

    /// <summary>Ответ, когда сценарий исчерпан; по умолчанию — ошибка теста.</summary>
    public Func<LlmJsonRequest, CancellationToken, Task<LlmResponse>> Fallback { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public int Inside => Volatile.Read(ref _inside);

    public int MaxConcurrent => Volatile.Read(ref _maxInside);

    public IReadOnlyList<LlmJsonRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public ScriptedLlmAdapter Then(Func<LlmJsonRequest, CancellationToken, Task<LlmResponse>> step)
    {
        lock (_steps)
        {
            _steps.Enqueue(step);
        }

        return this;
    }

    public ScriptedLlmAdapter ThenReturn(LlmResponse response) => Then((_, _) => Task.FromResult(response));

    public ScriptedLlmAdapter ThenThrow(LlmException exception) => Then((_, _) => Task.FromException<LlmResponse>(exception));

    public Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(new ModelListResult { Status = ModelListStatus.Empty });

    public async Task<LlmResponse> CompleteJsonAsync(LlmRuntimeConfig config, LlmJsonRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        var inside = Interlocked.Increment(ref _inside);
        int observed;
        while (inside > (observed = Volatile.Read(ref _maxInside)) && Interlocked.CompareExchange(ref _maxInside, inside, observed) != observed)
        {
        }

        lock (_requests)
        {
            _requests.Add(request);
        }

        try
        {
            Func<LlmJsonRequest, CancellationToken, Task<LlmResponse>> step;
            lock (_steps)
            {
                step = _steps.Count > 0 ? _steps.Dequeue() : Fallback;
            }

            if (step == null)
            {
                throw new InvalidOperationException("Сценарий адаптера исчерпан.");
            }

            return await step(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }

    public static LlmResponse Ok(string text = "{\"rows\":[]}", int? input = 10, int? output = 5) =>
        new() { Text = text, FinishReason = LlmFinishReason.Stop, RawFinishReason = "stop", InputTokens = input, OutputTokens = output };
}

internal sealed class CapturingLogger : IAppLogger
{
    private readonly List<LogEntry> _entries = new();

    public bool IsVerbose => true;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public void Write(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>Учётная запись Windows для тестов ролей: SID пользователя и групп задаются явно.</summary>
internal sealed class FakeIdentityProvider : IIdentityProvider
{
    // Синтетические SID из одного вымышленного домена.
    public const string AdminSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string OperatorSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    public const string OutsiderSid = "S-1-5-21-1111111111-2222222222-3333333333-1003";
    public const string AdminGroupSid = "S-1-5-21-1111111111-2222222222-3333333333-2001";
    public const string OperatorGroupSid = "S-1-5-21-1111111111-2222222222-3333333333-2002";

    public FakeIdentityProvider(string userName, string userSid, params string[] groupSids)
    {
        UserName = userName;
        UserSid = userSid;
        GroupSids = groupSids ?? Array.Empty<string>();
    }

    public static FakeIdentityProvider Admin() => new("TESTDOM\\admin", AdminSid);

    public static FakeIdentityProvider Operator() => new("TESTDOM\\operator", OperatorSid);

    public static FakeIdentityProvider Outsider() => new("TESTDOM\\guest", OutsiderSid);

    public string UserName { get; }

    public string UserSid { get; }

    public IReadOnlyCollection<string> GroupSids { get; }

    public bool IsMember(string sid) =>
        !string.IsNullOrWhiteSpace(sid) &&
        (string.Equals(sid, UserSid, StringComparison.OrdinalIgnoreCase) || GroupSids.Contains(sid, StringComparer.OrdinalIgnoreCase));

    public ResolvedPrincipal Resolve(string accountName) => null;

    public string NameOf(string sid) => sid;
}

/// <summary>Хранилище настроек в памяти (сериализация JSON, как у файлового); ACL не применяет, только считает вызовы.</summary>
internal sealed class InMemorySettingsStore : ISettingsStore
{
    private string _json;

    public string Directory => "memory";

    public int AclApplications { get; private set; }

    public AccessSettings LastAclAccess { get; private set; }

    public string RawJson => _json;

    public AppSettings Load() => _json == null ? new AppSettings() : JsonConvert.DeserializeObject<AppSettings>(_json);

    public void Save(AppSettings settings) => _json = JsonConvert.SerializeObject(settings);

    public void ApplyAccessControl(AccessSettings access)
    {
        AclApplications++;
        LastAclAccess = access;
    }
}

/// <summary>Администрирование БД без SQL Server: запоминает переданный пароль.</summary>
internal sealed class RecordingDatabaseAdmin : IDatabaseAdmin
{
    public int Calls { get; private set; }

    public string LastPassword { get; private set; }

    public Task<DatabaseProvisionResult> EnsureDatabaseAsync(DatabaseSettings settings, string sqlPassword, string appliedBy, CancellationToken cancellationToken)
    {
        Calls++;
        LastPassword = sqlPassword;
        return Task.FromResult(new DatabaseProvisionResult { Existed = true });
    }

    public Task<SchemaReport> InspectAsync(DatabaseSettings settings, string sqlPassword, CancellationToken cancellationToken)
    {
        Calls++;
        LastPassword = sqlPassword;
        return Task.FromResult(new SchemaReport());
    }

    public Task<MigrationResult> ApplyMigrationAsync(DatabaseSettings settings, string sqlPassword, string migrationId, string appliedBy, CancellationToken cancellationToken)
    {
        Calls++;
        LastPassword = sqlPassword;
        return Task.FromResult(new MigrationResult { Success = true, Message = "ok" });
    }

    public Task<long> RebuildSearchProjectionAsync(DatabaseSettings settings, string sqlPassword, IProgress<long> progress, CancellationToken cancellationToken)
    {
        Calls++;
        LastPassword = sqlPassword;
        return Task.FromResult(0L);
    }
}

internal static class Rec
{
    /// <summary>Синтетическая исходная запись с порядковым номером и парами «колонка — значение».</summary>
    public static SourceRecord Make(long ordinal, params (string Column, string Value)[] fields) =>
        new(ordinal, ordinal + 1, ordinal + 1, fields.Select(f => new KeyValuePair<string, string>(f.Column, f.Value)).ToList(), "hash-" + ordinal, null, null);
}
