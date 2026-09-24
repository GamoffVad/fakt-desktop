using System;
using System.Linq;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Infrastructure.Llm;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fakt.UnitTests.Infrastructure.Llm;

public sealed class OpenAiChatAdapterTests : LlmAdapterTestBase
{
    private const string OpenAi = ProviderCatalog.OpenAi;
    private const string Compatible = ProviderCatalog.OpenAiCompatible;

    private static object Completion(string content, string finish = "stop", object message = null) => new
    {
        id = "chatcmpl-test-1",
        @object = "chat.completion",
        created = 1760000000,
        model = "test-model-2026",
        choices = new[]
        {
            new { index = 0, message = message ?? new { role = "assistant", content, refusal = (string)null }, logprobs = (object)null, finish_reason = finish },
        },
        usage = new { prompt_tokens = 120, completion_tokens = 15, total_tokens = 135 },
    };

    [Fact]
    public async Task JsonSchemaRequest_HasOpenAiShapeAndBearerKey()
    {
        Server.Respond(MockResponse.Json(Completion("{\"rows\":[]}")).WithHeader("x-request-id", "req-openai-1"));
        var request = Request();

        var response = await Complete(Config(OpenAi), request);

        var sent = Server.SingleRequest();
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1/chat/completions", sent.RawUrl);
        Assert.Equal("Bearer " + TestKey, sent.Header("Authorization"));
        Assert.StartsWith("application/json", sent.Header("Content-Type"));
        Assert.Equal("FAKT/1.0", sent.Header("User-Agent"));
        var body = sent.Json;
        Assert.Equal("test-model", (string)body["model"]);
        Assert.Equal("developer", (string)body["messages"][0]["role"]);
        Assert.Equal("SYSTEM-PROMPT-MARKER", (string)body["messages"][0]["content"]);
        Assert.Equal("user", (string)body["messages"][1]["role"]);
        Assert.Equal("USER-CONTENT-MARKER", (string)body["messages"][1]["content"]);
        Assert.Equal(1234, (int)body["max_completion_tokens"]);
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["temperature"]);
        Assert.False((bool)body["stream"]);
        var format = body["response_format"];
        Assert.Equal("json_schema", (string)format["type"]);
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)format["json_schema"]["name"]);
        Assert.True((bool)format["json_schema"]["strict"]);
        Assert.True(JToken.DeepEquals(request.Schema, format["json_schema"]["schema"]));

        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
        Assert.Equal(120, response.InputTokens);
        Assert.Equal(15, response.OutputTokens);
        Assert.Equal("req-openai-1", response.RequestId);
        Assert.Equal("test-model-2026", response.ModelReported);
    }

    [Fact]
    public async Task Temperature_IsSentOnlyWhenSet()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));

        await Complete(Config(OpenAi), Request(temperature: 0.2));

        Assert.Equal(0.2, (double)Server.SingleRequest().Json["temperature"], 6);
    }

    [Fact]
    public async Task JsonObjectMode_PutsSchemaIntoPrompt()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));

        await Complete(Config(OpenAi), Request(StructuredOutputMode.JsonObject));

        var body = Server.SingleRequest().Json;
        Assert.Equal("json_object", (string)body["response_format"]["type"]);
        var user = (string)body["messages"][1]["content"];
        Assert.StartsWith("USER-CONTENT-MARKER", user);
        Assert.Contains("RESPONSE FORMAT", user);
        Assert.Contains("\"additionalProperties\":false", user);
    }

    [Fact]
    public async Task PromptOnlyMode_SendsNoResponseFormat()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));

        await Complete(Config(OpenAi), Request(StructuredOutputMode.PromptOnly));

        var body = Server.SingleRequest().Json;
        Assert.Null(body["response_format"]);
        Assert.Null(body["tools"]);
        Assert.Contains("RESPONSE FORMAT", (string)body["messages"][1]["content"]);
    }

    [Fact]
    public async Task ToolCallMode_ForcesFunctionAndReadsArguments()
    {
        var message = new
        {
            role = "assistant",
            content = (string)null,
            tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = JsonSchemas.ExtractionSchemaName, arguments = "{\"rows\":[]}" } } },
        };
        Server.Respond(MockResponse.Json(Completion(null, "tool_calls", message)));
        var request = Request(StructuredOutputMode.ToolCall);

        var response = await Complete(Config(OpenAi), request);

        var body = Server.SingleRequest().Json;
        var function = body["tools"][0]["function"];
        Assert.Equal("function", (string)body["tools"][0]["type"]);
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)function["name"]);
        Assert.True((bool)function["strict"]);
        Assert.True(JToken.DeepEquals(request.Schema, function["parameters"]));
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)body["tool_choice"]["function"]["name"]);
        Assert.Null(body["response_format"]);
        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
    }

    [Theory]
    [InlineData("<think>\nрассуждения модели\n</think>\n{\"rows\":[]}", "{\"rows\":[]}")]
    [InlineData("  <THINK>x</THINK>{\"rows\":[]}", "{\"rows\":[]}")]
    [InlineData("<think>усечённые рассуждения без конца", "")]
    [InlineData("{\"rows\":[]}", "{\"rows\":[]}")]
    public async Task ReasoningBlock_IsStripped(string content, string expected)
    {
        Server.Respond(MockResponse.Json(Completion(content)));

        Assert.Equal(expected, (await Complete(Config(OpenAi))).Text);
    }

    [Fact]
    public async Task LengthFinish_MarksResponseTruncated()
    {
        Server.Respond(MockResponse.Json(Completion("{\"rows\":[{\"source_row_id\":\"r1\"", "length")));

        var response = await Complete(Config(OpenAi));

        Assert.Equal(LlmFinishReason.Length, response.FinishReason);
        Assert.True(response.IsTruncated);
    }

    [Fact]
    public async Task Refusal_IsReportedWithoutText()
    {
        Server.Respond(MockResponse.Json(Completion(null, "stop", new { role = "assistant", content = (string)null, refusal = "Не могу помочь с этим запросом." })));

        var response = await Complete(Config(OpenAi));

        Assert.Equal(LlmFinishReason.Refusal, response.FinishReason);
        Assert.Null(response.Text);
    }

    [Fact]
    public async Task ContentParts_OnlyTextIsUsed()
    {
        var message = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "thinking", thinking = new[] { new { type = "text", text = "размышления" } } },
                new { type = "text", text = "{\"rows\":[]}" },
            },
        };
        Server.Respond(MockResponse.Json(Completion(null, "stop", message)));

        Assert.Equal("{\"rows\":[]}", (await Complete(Config(ProviderCatalog.Mistral))).Text);
    }

    [Fact]
    public async Task OpenAiCompatible_HeaderAuthCustomPathAndDialectOptions()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));
        var config = Config(Compatible, configure: p =>
        {
            p.Options["auth_style"] = "header";
            p.Options["auth_header"] = "X-Api-Key";
            p.Options["chat_path"] = "custom/chat";
            p.Options["strict"] = "false";
        });

        await Complete(config);

        var sent = Server.SingleRequest();
        Assert.Equal("/v1/custom/chat", sent.Path);
        Assert.Equal(TestKey, sent.Header("X-Api-Key"));
        Assert.Null(sent.Header("Authorization"));
        var body = sent.Json;
        Assert.Equal(1234, (int)body["max_tokens"]);
        Assert.Null(body["max_completion_tokens"]);
        Assert.Equal("system", (string)body["messages"][0]["role"]);
        Assert.Null(body["response_format"]["json_schema"]["strict"]);
    }

    [Fact]
    public async Task OpenAiCompatible_NoAuth_SendsNoKey()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));

        await Complete(Config(Compatible, configure: p => p.Options["auth_style"] = "none"));

        var sent = Server.SingleRequest();
        Assert.Null(sent.Header("Authorization"));
        Assert.DoesNotContain(sent.Headers.Values, v => v.Contains(TestKey));
    }

    [Fact]
    public async Task ExtraHeaders_AreSent()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));
        var headers = new System.Collections.Generic.Dictionary<string, string> { ["X-Team"] = "blue", ["X-Empty"] = "" };

        await Complete(Config(OpenAi, headers: headers));

        var sent = Server.SingleRequest();
        Assert.Equal("blue", sent.Header("X-Team"));
        Assert.Null(sent.Header("X-Empty"));
    }

    [Fact]
    public async Task AzureClassic_UsesDeploymentPathApiVersionAndApiKeyHeader()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));
        var config = Config(ProviderCatalog.AzureOpenAi, path: string.Empty, configure: p =>
        {
            p.ModelId = "my-deploy";
            p.Options["api_mode"] = "classic";
        });

        await Complete(config);

        var sent = Server.SingleRequest();
        Assert.Equal("/openai/deployments/my-deploy/chat/completions?api-version=2024-10-21", sent.RawUrl);
        Assert.Equal(TestKey, sent.Header("api-key"));
        Assert.Null(sent.Header("Authorization"));
        Assert.Null(sent.Json["model"]);
        Assert.Equal(1234, (int)sent.Json["max_completion_tokens"]);
    }

    [Fact]
    public async Task AzureV1_SendsDeploymentAsModel()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));

        await Complete(Config(ProviderCatalog.AzureOpenAi, path: string.Empty, configure: p => p.ModelId = "my-deploy"));

        var sent = Server.SingleRequest();
        Assert.Equal("/openai/v1/chat/completions", sent.RawUrl);
        Assert.Equal("my-deploy", (string)sent.Json["model"]);
    }

    [Fact]
    public async Task Qwen_DisablesThinkingByDefault()
    {
        Server.Respond(MockResponse.Json(Completion("{}")));

        await Complete(Config(ProviderCatalog.Qwen));

        Assert.False((bool)Server.SingleRequest().Json["enable_thinking"]);
    }

    [Fact]
    public async Task Http200WithErrorBody_IsClassified()
    {
        Server.Respond(MockResponse.Json(new { error = new { code = 429, message = "Rate limit exceeded upstream" } }));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(Config(ProviderCatalog.OpenRouter)));

        Assert.Equal(LlmErrorKind.RateLimited, ex.Kind);
        Assert.True(ex.IsTransient);
    }

    [Theory]
    [InlineData("<html><body>Proxy login</body></html>")]
    [InlineData("{\"id\":\"x\",\"choices\":[{\"message\":{\"content\":\"{\\\"rows")]
    [InlineData("")]
    public async Task NonJsonOrTruncatedSuccessBody_IsInvalidResponse(string body)
    {
        Server.Respond(MockResponse.Text(body, 200));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(Config(OpenAi)));

        Assert.Equal(LlmErrorKind.InvalidResponse, ex.Kind);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task ResponseWithoutChoices_IsInvalidResponse()
    {
        Server.Respond(MockResponse.Json(new { id = "x", choices = new object[0] }));

        Assert.Equal(LlmErrorKind.InvalidResponse, (await Assert.ThrowsAsync<LlmException>(() => Complete(Config(OpenAi)))).Kind);
    }

    [Theory]
    [InlineData(" ", "https")]
    [InlineData("test-model", "ftp")]
    public async Task InvalidProfile_IsConfigurationErrorWithoutRequest(string model, string scheme)
    {
        var config = Config(OpenAi, configure: p =>
        {
            p.ModelId = model;
            p.BaseUrl = scheme == "ftp" ? "ftp://127.0.0.1/v1" : p.BaseUrl;
        });

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(config));

        Assert.Equal(LlmErrorKind.Configuration, ex.Kind);
        Assert.Empty(Server.Requests);
    }

    // ---------- Классификация ошибок HTTP ----------

    public static TheoryData<int, string, LlmErrorKind, bool> ErrorCases => new()
    {
        { 401, Err("Incorrect API key provided: " + TestKey + ".", "invalid_request_error", "invalid_api_key"), LlmErrorKind.Authentication, false },
        { 401, "", LlmErrorKind.Authentication, false },
        { 403, Err("Project does not have access to this resource.", "permission_error"), LlmErrorKind.PermissionDenied, false },
        { 404, Err("The model `test-model` does not exist or you do not have access to it.", "invalid_request_error", "model_not_found"), LlmErrorKind.ModelNotFound, false },
        { 404, "<html><body>Not Found</body></html>", LlmErrorKind.NotSupported, false },
        { 429, Err("Rate limit reached for requests", "requests"), LlmErrorKind.RateLimited, true },
        { 429, Err("You exceeded your current quota, please check your plan and billing details.", "insufficient_quota", "insufficient_quota"), LlmErrorKind.QuotaExceeded, false },
        { 402, Err("Payment required"), LlmErrorKind.QuotaExceeded, false },
        { 500, Err("The server had an error while processing your request."), LlmErrorKind.ServerError, true },
        { 502, "Bad gateway", LlmErrorKind.ServerError, true },
        { 503, Err("The engine is currently overloaded, please try again later"), LlmErrorKind.Overloaded, true },
        { 529, JsonConvert.SerializeObject(new { type = "error", error = new { type = "overloaded_error", message = "Overloaded" } }), LlmErrorKind.Overloaded, true },
        { 408, "", LlmErrorKind.Timeout, true },
        { 413, "", LlmErrorKind.ContextLengthExceeded, false },
        { 400, Err("This model's maximum context length is 8192 tokens.", "invalid_request_error", "context_length_exceeded"), LlmErrorKind.ContextLengthExceeded, false },
        { 400, Err("Invalid parameter: 'response_format' of type 'json_schema' is not supported with this model."), LlmErrorKind.UnsupportedResponseFormat, false },
        { 400, Err("The response was filtered due to the prompt triggering content management policy.", code: "content_filter"), LlmErrorKind.ContentFiltered, false },
        { 400, Err("Unrecognized request argument supplied: foo"), LlmErrorKind.UnsupportedParameter, false },
        { 400, Err("Something else is wrong"), LlmErrorKind.BadRequest, false },
        { 422, Err("Input should be a valid string"), LlmErrorKind.BadRequest, false },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task HttpErrors_AreClassifiedWithoutLeakingKey(int status, string body, LlmErrorKind kind, bool transient)
    {
        Server.Respond(MockResponse.Text(body, status, "application/json"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(Config(OpenAi)));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(status, ex.HttpStatus);
        Assert.DoesNotContain(TestKey, ex.Message);
    }

    [Fact]
    public async Task UnsupportedTemperature_NamesTheParameter()
    {
        Server.Respond(MockResponse.Json(
            Err("Unsupported value: 'temperature' does not support 0.2 with this model. Only the default (1) value is supported.", "invalid_request_error", "unsupported_value", "temperature"), 400));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(Config(OpenAi), Request(temperature: 0.2)));

        Assert.Equal(LlmErrorKind.UnsupportedParameter, ex.Kind);
        Assert.Equal("temperature", ex.Parameter);
    }

    [Theory]
    [InlineData("Retry-After", "7", 7000)]
    [InlineData("retry-after-ms", "1500", 1500)]
    [InlineData("x-ratelimit-reset-requests", "6m0s", 360000)]
    public async Task RateLimit_CarriesRetryAfter(string header, string value, int expectedMilliseconds)
    {
        Server.Respond(MockResponse.Json(Err("Rate limit reached for requests"), 429).WithHeader(header, value).WithHeader("x-request-id", "req-429"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(Config(OpenAi)));

        Assert.Equal(LlmErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), ex.RetryAfter);
        Assert.Equal("req-429", ex.RequestId);
    }

    [Fact]
    public async Task ProviderEchoingAnUnprefixedKey_DoesNotLeakItIntoMessages()
    {
        const string key = "local-key-2c9f1e7a55d04b8e";
        Server.Respond(MockResponse.Json(Err("Incorrect API key provided: " + key + ". Check your configuration.", "invalid_request_error"), 401));
        var config = Config(Compatible, apiKey: key);

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(config));
        var list = await ListModels(config);

        Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        Assert.DoesNotContain(key, ex.Message);
        Assert.Equal(ModelListStatus.Unauthorized, list.Status);
        Assert.DoesNotContain(key, list.Message);
    }

    [Fact]
    public async Task DroppedConnection_IsNetworkError()
    {
        Server.Respond(MockResponse.Abort());

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(Config(OpenAi)));

        Assert.Equal(LlmErrorKind.Network, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.DoesNotContain(TestKey, ex.Message);
    }

    // ---------- Список моделей ----------

    [Fact]
    public async Task Models_ArePagedFilteredAndSorted()
    {
        Server.Respond(r => r.Query.Contains("after=whisper-test")
            ? MockResponse.Json(new { @object = "list", data = new[] { new { id = "gpt-test-a", @object = "model", owned_by = "system" } }, has_more = false })
            : MockResponse.Json(new
            {
                @object = "list",
                data = new[]
                {
                    new { id = "gpt-test-b", @object = "model", owned_by = "system" },
                    new { id = "text-embedding-test", @object = "model", owned_by = "system" },
                    new { id = "whisper-test", @object = "model", owned_by = "system" },
                },
                has_more = true,
                last_id = "whisper-test",
            }));

        var result = await ListModels(Config(OpenAi));

        Assert.Equal(ModelListStatus.Loaded, result.Status);
        Assert.Equal(new[] { "gpt-test-a", "gpt-test-b" }, result.Models.Select(m => m.Id));
        Assert.Equal("system", result.Models[0].OwnedBy);
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(2, Server.Requests.Count);
        Assert.All(Server.Requests, r =>
        {
            Assert.Equal("GET", r.Method);
            Assert.Equal("/v1/models", r.Path);
            Assert.Equal("Bearer " + TestKey, r.Header("Authorization"));
        });
    }

    [Fact]
    public async Task EmptyModelList_IsReportedAsEmpty()
    {
        Server.Respond(MockResponse.Json(new { @object = "list", data = new object[0] }));

        var result = await ListModels(Config(OpenAi));

        Assert.Equal(ModelListStatus.Empty, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Theory]
    [InlineData(401, ModelListStatus.Unauthorized)]
    [InlineData(403, ModelListStatus.Unauthorized)]
    [InlineData(404, ModelListStatus.NotSupported)]
    [InlineData(405, ModelListStatus.NotSupported)]
    [InlineData(500, ModelListStatus.ServiceUnavailable)]
    [InlineData(503, ModelListStatus.ServiceUnavailable)]
    [InlineData(400, ModelListStatus.Error)]
    public async Task ModelListErrors_MapToUiStatus(int status, ModelListStatus expected)
    {
        Server.Respond(MockResponse.Json(Err("error text"), status));

        var result = await ListModels(Config(OpenAi));

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Models);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public async Task ModelListConnectionFailure_IsServiceUnavailable()
    {
        Server.Respond(MockResponse.Abort());

        Assert.Equal(ModelListStatus.ServiceUnavailable, (await ListModels(Config(OpenAi))).Status);
    }

    [Fact]
    public async Task EmptyModelsPath_MeansListingNotSupported()
    {
        var result = await ListModels(Config(Compatible, configure: p => p.Options["models_path"] = string.Empty));

        Assert.Equal(ModelListStatus.NotSupported, result.Status);
        Assert.Empty(Server.Requests);
    }
}
