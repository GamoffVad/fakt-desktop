using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Infrastructure.Llm;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fakt.UnitTests.Infrastructure.Llm;

public sealed class OpenAiResponsesAdapterTests : LlmAdapterTestBase
{
    private LlmRuntimeConfig ResponsesConfig(string providerId = ProviderCatalog.OpenAi) =>
        Config(providerId, configure: p => p.Options["protocol"] = "responses");

    /// <summary>Ответ Responses API в том виде, в каком его возвращает сервис: служебные поля со значением null.</summary>
    private static JObject Completed(params JObject[] output) => new()
    {
        ["id"] = "resp_test_1",
        ["object"] = "response",
        ["created_at"] = 1760000000,
        ["status"] = "completed",
        ["error"] = null,
        ["incomplete_details"] = null,
        ["instructions"] = "SYSTEM-PROMPT-MARKER",
        ["max_output_tokens"] = 1234,
        ["model"] = "test-model-2026",
        ["output"] = new JArray(output),
        ["parallel_tool_calls"] = true,
        ["previous_response_id"] = null,
        ["reasoning"] = new JObject { ["effort"] = null, ["summary"] = null },
        ["store"] = false,
        ["temperature"] = 1.0,
        ["text"] = new JObject { ["format"] = new JObject { ["type"] = "json_schema" } },
        ["usage"] = new JObject { ["input_tokens"] = 200, ["output_tokens"] = 20, ["total_tokens"] = 220 },
        ["user"] = null,
        ["metadata"] = new JObject(),
    };

    private static JObject Message(params string[] texts)
    {
        var content = new JArray();
        foreach (var text in texts)
        {
            content.Add(new JObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JArray() });
        }

        return new JObject { ["type"] = "message", ["id"] = "msg_1", ["status"] = "completed", ["role"] = "assistant", ["content"] = content };
    }

    private static MockResponse Json(JObject body) => MockResponse.Json(body.ToString());

    [Fact]
    public async Task Request_UsesInstructionsInputStoreFalseAndTextFormat()
    {
        Server.Respond(Json(Completed(Message("{\"rows\":[]}"))));
        var request = Request();

        await Complete(ResponsesConfig(), request);

        var sent = Server.SingleRequest();
        Assert.Equal("/v1/responses", sent.RawUrl);
        Assert.Equal("Bearer " + TestKey, sent.Header("Authorization"));
        var body = sent.Json;
        Assert.Equal("test-model", (string)body["model"]);
        Assert.Equal("SYSTEM-PROMPT-MARKER", (string)body["instructions"]);
        Assert.Equal("USER-CONTENT-MARKER", (string)body["input"]);
        Assert.Equal(1234, (int)body["max_output_tokens"]);
        Assert.False((bool)body["store"]);
        Assert.Null(body["messages"]);
        var format = body["text"]["format"];
        Assert.Equal("json_schema", (string)format["type"]);
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)format["name"]);
        Assert.True((bool)format["strict"]);
        Assert.True(JToken.DeepEquals(request.Schema, format["schema"]));
    }

    [Fact]
    public async Task CompletedResponse_WithNullServiceFields_IsParsed()
    {
        // Настоящий сервис присылает "incomplete_details": null и "error": null у завершённого ответа.
        var reasoning = new JObject { ["type"] = "reasoning", ["id"] = "rs_1", ["summary"] = new JArray() };
        Server.Respond(Json(Completed(reasoning, Message("{\"rows\":", "[]}"))).WithHeader("x-request-id", "req-resp-1"));

        var response = await Complete(ResponsesConfig());

        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
        Assert.Equal(200, response.InputTokens);
        Assert.Equal(20, response.OutputTokens);
        Assert.Equal("req-resp-1", response.RequestId);
        Assert.Equal("test-model-2026", response.ModelReported);
    }

    [Theory]
    [InlineData("max_output_tokens", LlmFinishReason.Length)]
    [InlineData("content_filter", LlmFinishReason.ContentFilter)]
    public async Task IncompleteResponse_MapsReason(string reason, LlmFinishReason expected)
    {
        var body = Completed(Message("{\"rows\":["));
        body["status"] = "incomplete";
        body["incomplete_details"] = new JObject { ["reason"] = reason };
        Server.Respond(Json(body));

        var response = await Complete(ResponsesConfig());

        Assert.Equal(expected, response.FinishReason);
        Assert.Equal(reason, response.RawFinishReason);
    }

    [Fact]
    public async Task FailedResponse_IsTransientServerError()
    {
        var body = Completed();
        body["status"] = "failed";
        body["error"] = new JObject { ["code"] = "server_error", ["message"] = "Something went wrong" };
        body["usage"] = null;
        Server.Respond(Json(body));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(ResponsesConfig()));

        Assert.Equal(LlmErrorKind.ServerError, ex.Kind);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Refusal_IsReportedWithoutText()
    {
        var message = new JObject
        {
            ["type"] = "message",
            ["role"] = "assistant",
            ["content"] = new JArray { new JObject { ["type"] = "refusal", ["refusal"] = "Не могу помочь." } },
        };
        Server.Respond(Json(Completed(message)));

        var response = await Complete(ResponsesConfig());

        Assert.Equal(LlmFinishReason.Refusal, response.FinishReason);
        Assert.Null(response.Text);
    }

    [Fact]
    public async Task ToolCallMode_UsesFunctionToolAndReadsArguments()
    {
        var call = new JObject { ["type"] = "function_call", ["id"] = "fc_1", ["call_id"] = "call_1", ["name"] = JsonSchemas.ExtractionSchemaName, ["arguments"] = "{\"rows\":[]}" };
        Server.Respond(Json(Completed(call)));

        var response = await Complete(ResponsesConfig(), Request(StructuredOutputMode.ToolCall));

        var body = Server.SingleRequest().Json;
        Assert.Equal("function", (string)body["tools"][0]["type"]);
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)body["tools"][0]["name"]);
        Assert.Equal(JsonSchemas.ExtractionSchemaName, (string)body["tool_choice"]["name"]);
        Assert.Equal("{\"rows\":[]}", response.Text);
    }

    [Fact]
    public async Task OpenAiCompatible_ResponsesUsesCustomPathAndHeaderAuth()
    {
        Server.Respond(Json(Completed(Message("{}"))));
        var config = Config(ProviderCatalog.OpenAiCompatible, configure: p =>
        {
            p.Options["protocol"] = "responses";
            p.Options["responses_path"] = "api/responses";
            p.Options["auth_style"] = "header";
            p.Options["auth_header"] = "X-Api-Key";
        });

        await Complete(config);

        var sent = Server.SingleRequest();
        Assert.Equal("/v1/api/responses", sent.RawUrl);
        Assert.Equal(TestKey, sent.Header("X-Api-Key"));
        Assert.Null(sent.Header("Authorization"));
        Assert.False((bool)sent.Json["store"]);
    }

    [Fact]
    public async Task ChatProtocol_IsDefaultForOpenAi()
    {
        Server.Respond(MockResponse.Json(new { choices = new[] { new { message = new { role = "assistant", content = "{}" }, finish_reason = "stop" } } }));

        await Complete(Config(ProviderCatalog.OpenAi));

        Assert.Equal("/v1/chat/completions", Server.SingleRequest().RawUrl);
    }
}
