using System.Linq;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Infrastructure.Llm;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fakt.UnitTests.Infrastructure.Llm;

public sealed class AnthropicAdapterTests : LlmAdapterTestBase
{
    private LlmRuntimeConfig AnthropicConfig(System.Action<LlmProfile> configure = null) =>
        Config(ProviderCatalog.Anthropic, path: string.Empty, configure: configure);

    private static object Message(object[] content, string stopReason = "end_turn") => new
    {
        id = "msg_test_1",
        type = "message",
        role = "assistant",
        model = "test-model-2026",
        content,
        stop_reason = stopReason,
        stop_sequence = (string)null,
        usage = new { input_tokens = 100, cache_creation_input_tokens = 20, cache_read_input_tokens = 5, output_tokens = 30 },
    };

    [Fact]
    public async Task Request_UsesVersionHeaderXApiKeyAndOutputConfig()
    {
        Server.Respond(MockResponse.Json(Message(new object[] { new { type = "text", text = "{\"rows\":[]}" } })).WithHeader("request-id", "req_anthropic_1"));
        var request = Request();

        var response = await Complete(AnthropicConfig(), request);

        var sent = Server.SingleRequest();
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1/messages", sent.RawUrl);
        Assert.Equal(TestKey, sent.Header("x-api-key"));
        Assert.Equal("2023-06-01", sent.Header("anthropic-version"));
        Assert.Null(sent.Header("Authorization"));
        var body = sent.Json;
        Assert.Equal("test-model", (string)body["model"]);
        Assert.Equal(1234, (int)body["max_tokens"]);
        Assert.Equal("SYSTEM-PROMPT-MARKER", (string)body["system"]);
        var message = Assert.Single((JArray)body["messages"]);
        Assert.Equal("user", (string)message["role"]);
        Assert.Equal("USER-CONTENT-MARKER", (string)message["content"]);
        Assert.Equal("json_schema", (string)body["output_config"]["format"]["type"]);
        Assert.True(JToken.DeepEquals(request.Schema, body["output_config"]["format"]["schema"]));
        Assert.Null(body["temperature"]);
        Assert.Null(body["tools"]);

        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
        Assert.Equal(125, response.InputTokens);
        Assert.Equal(30, response.OutputTokens);
        Assert.Equal("req_anthropic_1", response.RequestId);
    }

    [Fact]
    public async Task ToolCallMode_ForcesToolAndReturnsItsInput()
    {
        var toolUse = new { type = "tool_use", id = "toolu_1", name = JsonSchemas.ExtractionSchemaName, input = new { rows = new object[0] } };
        Server.Respond(MockResponse.Json(Message(new object[] { toolUse }, "tool_use")));
        var request = Request(StructuredOutputMode.ToolCall);

        var response = await Complete(AnthropicConfig(), request);

        var body = Server.SingleRequest().Json;
        var tool = body["tools"][0];
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)tool["name"]);
        Assert.True((bool)tool["strict"]);
        Assert.True(JToken.DeepEquals(request.Schema, tool["input_schema"]));
        Assert.Equal("tool", (string)body["tool_choice"]["type"]);
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)body["tool_choice"]["name"]);
        Assert.Null(body["output_config"]);
        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task JsonObjectMode_IsRejectedWithoutRequest()
    {
        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(AnthropicConfig(), Request(StructuredOutputMode.JsonObject)));

        Assert.Equal(LlmErrorKind.UnsupportedResponseFormat, ex.Kind);
        Assert.Empty(Server.Requests);
    }

    [Theory]
    [InlineData("max_tokens", LlmFinishReason.Length)]
    [InlineData("model_context_window_exceeded", LlmFinishReason.Length)]
    [InlineData("refusal", LlmFinishReason.Refusal)]
    [InlineData("stop_sequence", LlmFinishReason.Stop)]
    [InlineData("pause_turn", LlmFinishReason.Other)]
    public async Task StopReason_IsMapped(string stopReason, LlmFinishReason expected)
    {
        Server.Respond(MockResponse.Json(Message(new object[] { new { type = "text", text = "{" } }, stopReason)));

        Assert.Equal(expected, (await Complete(AnthropicConfig())).FinishReason);
    }

    [Fact]
    public async Task BearerAuthStyle_UsesAuthorizationHeader()
    {
        Server.Respond(MockResponse.Json(Message(new object[] { new { type = "text", text = "{}" } })));

        await Complete(AnthropicConfig(p => p.Options["auth_style"] = "bearer"));

        var sent = Server.SingleRequest();
        Assert.Equal("Bearer " + TestKey, sent.Header("Authorization"));
        Assert.Null(sent.Header("x-api-key"));
    }

    [Fact]
    public async Task OverloadedError_IsTransient()
    {
        Server.Respond(MockResponse.Json("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", 529));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(AnthropicConfig()));

        Assert.Equal(LlmErrorKind.Overloaded, ex.Kind);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task AuthenticationError_IsNotTransient()
    {
        Server.Respond(MockResponse.Json("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}", 401));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(AnthropicConfig()));

        Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        Assert.DoesNotContain(TestKey, ex.Message);
    }

    [Fact]
    public async Task Models_FollowAfterIdCursor()
    {
        Server.Respond(r => r.Query.Contains("after_id=model-b")
            ? MockResponse.Json(new { data = new[] { new { type = "model", id = "model-a", display_name = "Model A" } }, has_more = false, first_id = "model-a", last_id = "model-a" })
            : MockResponse.Json(new { data = new[] { new { type = "model", id = "model-b", display_name = "Model B", max_input_tokens = 200000 } }, has_more = true, first_id = "model-b", last_id = "model-b" }));

        var result = await ListModels(AnthropicConfig());

        Assert.Equal(ModelListStatus.Loaded, result.Status);
        Assert.Equal(new[] { "model-a", "model-b" }, result.Models.Select(m => m.Id));
        Assert.Equal(200000, result.Models.Single(m => m.Id == "model-b").ContextLength);
        Assert.Equal(2, result.PagesFetched);
        Assert.All(Server.Requests, r =>
        {
            Assert.Equal("/v1/models", r.Path);
            Assert.Contains("limit=1000", r.Query);
            Assert.Equal(TestKey, r.Header("x-api-key"));
            Assert.Equal("2023-06-01", r.Header("anthropic-version"));
        });
    }
}
