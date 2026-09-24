using System.Linq;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Fakt.Infrastructure.Llm;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fakt.UnitTests.Infrastructure.Llm;

public sealed class OllamaAdapterTests : LlmAdapterTestBase
{
    private LlmRuntimeConfig OllamaConfig(string apiKey = null, System.Action<LlmProfile> configure = null) =>
        Config(ProviderCatalog.Ollama, path: string.Empty, apiKey: apiKey, configure: configure);

    private static object Chat(string content, string doneReason = "stop") => new
    {
        model = "test-model",
        created_at = "2026-09-24T10:00:00Z",
        message = new { role = "assistant", content },
        done_reason = doneReason,
        done = true,
        prompt_eval_count = 50,
        eval_count = 12,
    };

    [Fact]
    public async Task NativeChat_SendsSchemaAsFormatAndOptions()
    {
        Server.Respond(MockResponse.Json(Chat("<think>размышления</think>\n{\"rows\":[]}")));
        var request = Request();

        var response = await Complete(OllamaConfig(configure: p =>
        {
            p.Options["num_ctx"] = "8192";
            p.Options["keep_alive"] = "10m";
        }), request);

        var sent = Server.SingleRequest();
        Assert.Equal("/api/chat", sent.RawUrl);
        Assert.Null(sent.Header("Authorization"));
        var body = sent.Json;
        Assert.Equal("test-model", (string)body["model"]);
        Assert.Equal("system", (string)body["messages"][0]["role"]);
        Assert.Equal("SYSTEM-PROMPT-MARKER", (string)body["messages"][0]["content"]);
        Assert.Equal("user", (string)body["messages"][1]["role"]);
        Assert.False((bool)body["stream"]);
        Assert.Equal(1234, (int)body["options"]["num_predict"]);
        Assert.Equal(8192, (int)body["options"]["num_ctx"]);
        Assert.Equal("10m", (string)body["keep_alive"]);
        Assert.True(JToken.DeepEquals(request.Schema, body["format"]));

        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
        Assert.Equal(50, response.InputTokens);
        Assert.Equal(12, response.OutputTokens);
        Assert.Equal("test-model", response.ModelReported);
    }

    [Fact]
    public async Task JsonObjectMode_UsesJsonFormat()
    {
        Server.Respond(MockResponse.Json(Chat("{}")));

        await Complete(OllamaConfig(), Request(StructuredOutputMode.JsonObject));

        var body = Server.SingleRequest().Json;
        Assert.Equal("json", (string)body["format"]);
        Assert.Null(body["options"]["num_ctx"]);
        Assert.Null(body["keep_alive"]);
    }

    [Fact]
    public async Task ApiKey_IsSentAsBearerWhenConfigured()
    {
        Server.Respond(MockResponse.Json(Chat("{}")));

        await Complete(OllamaConfig(apiKey: TestKey));

        Assert.Equal("Bearer " + TestKey, Server.SingleRequest().Header("Authorization"));
    }

    [Fact]
    public async Task LengthDoneReason_IsTruncated()
    {
        Server.Respond(MockResponse.Json(Chat("{\"rows\":[", "length")));

        Assert.True((await Complete(OllamaConfig())).IsTruncated);
    }

    [Fact]
    public async Task UnknownModel_IsModelNotFound()
    {
        Server.Respond(MockResponse.Json("{\"error\":\"model \\\"test-model\\\" not found, try pulling it first\"}", 404));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(OllamaConfig()));

        Assert.Equal(LlmErrorKind.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task Tags_ListTextModelsWithDetails()
    {
        Server.Respond(MockResponse.Json(new
        {
            models = new object[]
            {
                new { name = "llama-test:8b", model = "llama-test:8b", details = new { family = "llama", parameter_size = "8B", quantization_level = "Q4_K_M" } },
                new { name = "nomic-embed-text:latest", model = "nomic-embed-text:latest", details = new { family = "nomic-bert" } },
            },
        }));

        var result = await ListModels(OllamaConfig());

        Assert.Equal("/api/tags", Server.SingleRequest().RawUrl);
        var model = Assert.Single(result.Models);
        Assert.Equal("llama-test:8b", model.Id);
        Assert.Equal("llama, 8B, Q4_K_M", model.Description);
        Assert.Equal(ModelListStatus.Loaded, result.Status);
    }
}
