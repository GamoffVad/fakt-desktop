using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Common;
using Fakt.Application.Extraction;
using Fakt.Application.Llm;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Core.Records;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json;
using Xunit;

namespace Fakt.UnitTests.Application;

public sealed class BatchExtractorTests
{
    private static readonly Regex IdPattern = new("\"source_row_id\":\"(r\\d+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Surnames = { "Иванов", "Петров", "Сидоров", "Смирнов", "Кузнецов", "Попов" };

    private readonly ScriptedLlmAdapter _adapter = new();
    private readonly TokenEstimator _estimator = new();
    private readonly AdaptiveBatchSize _batchSize = new(10);

    private BatchExtractor Extractor()
    {
        var profile = new LlmProfile { Name = "test", ProviderId = "scripted", ModelId = "test-model", MaxOutputTokens = 4096, MaxConcurrentRequests = 1 };
        var clock = new FakeClock(new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc));
        var client = new ResilientLlmClient(_adapter, new LlmRuntimeConfig(profile, null, null), new RetryPolicy(), null, null, new FakeDelay(clock), clock);
        var context = new ExtractionContext { ProviderId = "scripted", ModelId = "test-model", ExtractionVersion = "v-test", Today = new DateTime(2026, 9, 24) };
        return new BatchExtractor(client, context, _estimator, _batchSize, null);
    }

    private static List<SourceRecord> Records(int count) =>
        Enumerable.Range(1, count).Select(i => Rec.Make(i, ("ФИО", Surnames[i - 1] + " Иван Иванович"))).ToList();

    private static List<string> IdsOf(LlmJsonRequest request) =>
        IdPattern.Matches(request.UserContent).Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    private static LlmResponse NoFactsFor(IEnumerable<string> ids) => ScriptedLlmAdapter.Ok(JsonConvert.SerializeObject(new
    {
        rows = ids.Select(id => new { source_row_id = id, status = "no_facts", persons = new object[0], unassigned_facts = new object[0] }),
    }));

    private void AnswerAllRows() => _adapter.Fallback = (request, _) => Task.FromResult(NoFactsFor(IdsOf(request)));

    [Fact]
    public async Task EveryRecord_GetsItsOwnResult()
    {
        AnswerAllRows();

        var outcomes = await Extractor().ExtractAsync(Records(3), CancellationToken.None);

        Assert.Equal(new long[] { 1, 2, 3 }, outcomes.Select(o => o.Ordinal));
        Assert.All(outcomes, o => Assert.Equal(RowOutcomeKind.NoFacts, o.Kind));
        Assert.Equal(1, _adapter.Calls);
        Assert.Equal(StructuredOutputMode.JsonSchema, _adapter.Requests[0].Mode);
        Assert.Equal(JsonSchemas.ExtractionSchemaName, _adapter.Requests[0].SchemaName);
    }

    [Fact]
    public async Task TruncatedResponse_SplitsBatchInHalvesAndShrinksBatchSize()
    {
        _adapter.ThenReturn(new LlmResponse { Text = "{\"rows\":[", FinishReason = LlmFinishReason.Length });
        AnswerAllRows();

        var outcomes = await Extractor().ExtractAsync(Records(4), CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(RowOutcomeKind.NoFacts, o.Kind));
        Assert.Equal(3, _adapter.Calls);
        Assert.Equal(new[] { "r1", "r2" }, IdsOf(_adapter.Requests[1]));
        Assert.Equal(new[] { "r3", "r4" }, IdsOf(_adapter.Requests[2]));
        Assert.Equal(2, _batchSize.Rows);
    }

    [Fact]
    public async Task TruncatedSingleRecord_BecomesRowError()
    {
        _adapter.Fallback = (_, _) => Task.FromResult(new LlmResponse { Text = "{", FinishReason = LlmFinishReason.Length });

        var outcome = Assert.Single(await Extractor().ExtractAsync(Records(1), CancellationToken.None));

        Assert.Equal(RowOutcomeKind.Error, outcome.Kind);
        Assert.Equal("output_truncated", outcome.ErrorCode);
    }

    [Fact]
    public async Task MissingRows_AreRequestedAgainSeparately()
    {
        _adapter.Then((request, _) => Task.FromResult(NoFactsFor(IdsOf(request).Take(1))));
        AnswerAllRows();

        var outcomes = await Extractor().ExtractAsync(Records(3), CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(RowOutcomeKind.NoFacts, o.Kind));
        Assert.Equal(2, _adapter.Calls);
        Assert.Equal(new[] { "r2", "r3" }, IdsOf(_adapter.Requests[1]));
    }

    [Fact]
    public async Task RowNeverReturned_BecomesMissingResultAfterRetryRounds()
    {
        _adapter.Fallback = (request, _) => Task.FromResult(NoFactsFor(IdsOf(request).Where(id => id != "r2")));

        var outcomes = await Extractor().ExtractAsync(Records(3), CancellationToken.None);

        Assert.Equal(3, _adapter.Calls);
        Assert.Equal("missing_result", outcomes.Single(o => o.Ordinal == 2).ErrorCode);
        Assert.All(outcomes.Where(o => o.Ordinal != 2), o => Assert.Equal(RowOutcomeKind.NoFacts, o.Kind));
    }

    [Fact]
    public async Task InvalidJson_IsRetriedThenReportedAsRowError()
    {
        _adapter.Fallback = (_, _) => Task.FromResult(ScriptedLlmAdapter.Ok("Извините, вот ответ: rows"));

        var outcome = Assert.Single(await Extractor().ExtractAsync(Records(1), CancellationToken.None));

        Assert.Equal(3, _adapter.Calls);
        Assert.Equal("invalid_response", outcome.ErrorCode);
    }

    [Fact]
    public async Task ContextOverflow_SplitsDownToSingleRecordErrors()
    {
        _adapter.Fallback = (_, _) => Task.FromException<LlmResponse>(new LlmException(LlmErrorKind.ContextLengthExceeded, "too long", 400));

        var outcomes = await Extractor().ExtractAsync(Records(2), CancellationToken.None);

        Assert.Equal(3, _adapter.Calls);
        Assert.All(outcomes, o => Assert.Equal("record_too_large", o.ErrorCode));
    }

    [Fact]
    public async Task Refusal_OfSingleRecord_IsRowError()
    {
        _adapter.Fallback = (_, _) => Task.FromResult(new LlmResponse { FinishReason = LlmFinishReason.Refusal });

        var outcome = Assert.Single(await Extractor().ExtractAsync(Records(1), CancellationToken.None));

        Assert.Equal("model_refused", outcome.ErrorCode);
    }

    [Fact]
    public async Task FatalProviderError_StopsExtraction()
    {
        _adapter.Fallback = (_, _) => Task.FromException<LlmResponse>(new LlmException(LlmErrorKind.Authentication, "no key", 401));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Extractor().ExtractAsync(Records(2), CancellationToken.None));

        Assert.True(ex.IsFatalForJob);
        Assert.Equal(1, _adapter.Calls);
    }

    [Fact]
    public async Task ReportedUsage_CalibratesTokenEstimator()
    {
        _adapter.Fallback = (request, _) =>
        {
            var estimatedRaw = TokenEstimator.EstimateRaw(request.SystemPrompt) + TokenEstimator.EstimateRaw(request.UserContent) + 400;
            var response = NoFactsFor(IdsOf(request));
            response.InputTokens = estimatedRaw * 2;
            return Task.FromResult(response);
        };

        await Extractor().ExtractAsync(Records(2), CancellationToken.None);

        Assert.Equal(2.0, _estimator.Calibration, 6);
    }
}

public sealed class AdaptiveBatchSizeTests
{
    [Fact]
    public void Shrink_HalvesFailedBatchButNeverGrows()
    {
        var size = new AdaptiveBatchSize(10);

        size.Shrink(8);
        Assert.Equal(4, size.Rows);

        size.Shrink(20);
        Assert.Equal(4, size.Rows);

        size.Shrink(1);
        Assert.Equal(1, size.Rows);
    }

    [Fact]
    public void InitialSize_IsAtLeastOne()
    {
        Assert.Equal(1, new AdaptiveBatchSize(0).Rows);
    }
}

public sealed class CapabilityResolverTests
{
    private static LlmProfile Profile(StructuredOutputMode mode, CapabilityState caps = null) =>
        new() { ModelId = "test-model", OutputMode = mode, Capabilities = caps ?? new CapabilityState() };

    private static CapabilityState Verified(bool? schema, bool? jsonObject, bool? tool, string model = "test-model") =>
        new() { JsonSchema = schema, JsonObject = jsonObject, ToolCall = tool, VerifiedModelId = model, VerifiedAtUtc = new DateTime(2026, 9, 24) };

    [Fact]
    public void ExplicitMode_IsUsedAsIs()
    {
        Assert.Equal(StructuredOutputMode.ToolCall, CapabilityResolver.Resolve(Profile(StructuredOutputMode.ToolCall)));
    }

    [Fact]
    public void AutoMode_WithoutVerificationForCurrentModel_NeedsTest()
    {
        Assert.Null(CapabilityResolver.Resolve(Profile(StructuredOutputMode.Auto)));
        Assert.Null(CapabilityResolver.Resolve(Profile(StructuredOutputMode.Auto, Verified(true, true, true, model: "other-model"))));
    }

    [Theory]
    [InlineData(true, true, true, StructuredOutputMode.JsonSchema)]
    [InlineData(false, true, true, StructuredOutputMode.JsonObject)]
    [InlineData(false, false, true, StructuredOutputMode.ToolCall)]
    [InlineData(false, false, false, StructuredOutputMode.PromptOnly)]
    [InlineData(null, null, null, StructuredOutputMode.PromptOnly)]
    public void AutoMode_PicksBestVerifiedMode(bool? schema, bool? jsonObject, bool? tool, StructuredOutputMode expected)
    {
        Assert.Equal(expected, CapabilityResolver.Resolve(Profile(StructuredOutputMode.Auto, Verified(schema, jsonObject, tool))));
    }

    [Fact]
    public void Ladder_FollowsDocumentedModesAndEndsWithPromptOnly()
    {
        var deepSeek = new LlmProviderDescriptor { DocumentedModes = new[] { StructuredOutputMode.ToolCall, StructuredOutputMode.JsonObject } };

        Assert.Equal(new[] { StructuredOutputMode.JsonObject, StructuredOutputMode.ToolCall, StructuredOutputMode.PromptOnly }, CapabilityResolver.Ladder(deepSeek));
        Assert.Equal(
            new[] { StructuredOutputMode.JsonSchema, StructuredOutputMode.JsonObject, StructuredOutputMode.ToolCall, StructuredOutputMode.PromptOnly },
            CapabilityResolver.Ladder(new LlmProviderDescriptor()));
    }
}

public sealed class AsyncBoundedQueueTests
{
    [Fact]
    public async Task Writer_WaitsForFreeSlot()
    {
        var queue = new AsyncBoundedQueue<int>(2);
        await queue.EnqueueAsync(1, CancellationToken.None);
        await queue.EnqueueAsync(2, CancellationToken.None);

        var third = queue.EnqueueAsync(3, CancellationToken.None);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, queue.Count);

        var first = await queue.DequeueAsync(CancellationToken.None);
        await third;

        Assert.Equal((true, 1), first);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public async Task CompletedQueue_IsDrainedThenReportsEnd()
    {
        var queue = new AsyncBoundedQueue<string>(4);
        await queue.EnqueueAsync("a", CancellationToken.None);
        queue.Complete();

        Assert.Equal((true, "a"), await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal((false, (string)null), await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal((false, (string)null), await queue.DequeueAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync("b", CancellationToken.None));
    }

    [Fact]
    public async Task WaitingReader_IsReleasedByComplete()
    {
        var queue = new AsyncBoundedQueue<int>(1);
        var reader = queue.DequeueAsync(CancellationToken.None);
        Assert.False(reader.IsCompleted);

        queue.Complete();

        Assert.Equal((false, 0), await reader);
    }

    [Fact]
    public void Capacity_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncBoundedQueue<int>(0));
    }
}
