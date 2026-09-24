using System.Linq;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Infrastructure.Llm;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fakt.UnitTests.Infrastructure.Llm;

public sealed class GeminiAdapterTests : LlmAdapterTestBase
{
    private LlmRuntimeConfig GeminiConfig(System.Action<LlmProfile> configure = null) =>
        Config(ProviderCatalog.Gemini, path: string.Empty, configure: configure);

    private static object Generated(string finishReason = "STOP", params object[] parts) => new
    {
        candidates = new[] { new { content = new { parts, role = "model" }, finishReason, index = 0 } },
        usageMetadata = new { promptTokenCount = 90, candidatesTokenCount = 10, thoughtsTokenCount = 5, totalTokenCount = 105 },
        modelVersion = "test-model-001",
        responseId = "gem-resp-1",
    };

    [Fact]
    public async Task Request_SendsKeyInHeaderAndJsonSchemaInGenerationConfig()
    {
        Server.Respond(MockResponse.Json(Generated("STOP", new { text = "размышления", thought = true }, new { text = "{\"rows\":[]}" })));
        var request = Request();

        var response = await Complete(GeminiConfig(), request);

        var sent = Server.SingleRequest();
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1beta/models/test-model:generateContent", sent.RawUrl);
        Assert.Equal(TestKey, sent.Header("x-goog-api-key"));
        Assert.Null(sent.Header("Authorization"));
        Assert.DoesNotContain("key=", sent.RawUrl);
        var body = sent.Json;
        Assert.Equal("SYSTEM-PROMPT-MARKER", (string)body["systemInstruction"]["parts"][0]["text"]);
        Assert.Equal("user", (string)body["contents"][0]["role"]);
        Assert.Equal("USER-CONTENT-MARKER", (string)body["contents"][0]["parts"][0]["text"]);
        var generation = body["generationConfig"];
        Assert.Equal(1234, (int)generation["maxOutputTokens"]);
        Assert.Equal("application/json", (string)generation["responseMimeType"]);
        Assert.True(JToken.DeepEquals(request.Schema, generation["responseJsonSchema"]));
        Assert.Null(generation["responseSchema"]);
        Assert.Null(generation["temperature"]);

        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
        Assert.Equal(90, response.InputTokens);
        Assert.Equal(15, response.OutputTokens);
        Assert.Equal("gem-resp-1", response.RequestId);
        Assert.Equal("test-model-001", response.ModelReported);
    }

    [Fact]
    public async Task LegacyResponseSchema_IsConvertedToOpenApiSubset()
    {
        Server.Respond(MockResponse.Json(Generated("STOP", new { text = "{}" })));

        await Complete(GeminiConfig(p => p.Options["gemini_schema_field"] = "responseSchema"));

        var generation = Server.SingleRequest().Json["generationConfig"];
        Assert.Null(generation["responseJsonSchema"]);
        var schema = (JObject)generation["responseSchema"];
        Assert.Equal("OBJECT", (string)schema["type"]);
        Assert.DoesNotContain(schema.DescendantsAndSelf().OfType<JProperty>(), p => p.Name == "additionalProperties");
        Assert.Equal(new[] { "rows" }, schema["propertyOrdering"].Select(t => (string)t));
        var surname = schema["properties"]["rows"]["items"]["properties"]["persons"]["items"]["properties"]["surname"];
        Assert.Equal("STRING", (string)surname["type"]);
        Assert.True((bool)surname["nullable"]);
        var factType = schema["properties"]["rows"]["items"]["properties"]["persons"]["items"]["properties"]["facts"]["items"]["properties"]["type"];
        Assert.Contains("phone", factType["enum"].Select(t => (string)t));
    }

    [Fact]
    public void OpenApiConversion_DropsNullFromEnumsAndKeepsRequired()
    {
        var converted = GeminiAdapter.ToOpenApiSchema(JObject.Parse(
            "{\"type\":\"object\",\"properties\":{\"format\":{\"type\":[\"string\",\"null\"],\"enum\":[\"xml\",\"jsonl\",null]}},\"required\":[\"format\"],\"additionalProperties\":false}"));

        var format = converted["properties"]["format"];
        Assert.Equal("STRING", (string)format["type"]);
        Assert.True((bool)format["nullable"]);
        Assert.Equal(new[] { "xml", "jsonl" }, format["enum"].Select(t => (string)t));
        Assert.Equal(new[] { "format" }, converted["required"].Select(t => (string)t));
        Assert.Null(converted["additionalProperties"]);
    }

    [Fact]
    public async Task ModelsPrefix_IsStrippedFromPath()
    {
        Server.Respond(MockResponse.Json(Generated("STOP", new { text = "{}" })));

        await Complete(GeminiConfig(p => p.ModelId = "models/test-model"));

        Assert.Equal("/v1beta/models/test-model:generateContent", Server.SingleRequest().RawUrl);
    }

    [Fact]
    public async Task JsonObjectMode_SetsMimeTypeWithoutSchema()
    {
        Server.Respond(MockResponse.Json(Generated("STOP", new { text = "{}" })));

        await Complete(GeminiConfig(), Request(StructuredOutputMode.JsonObject));

        var generation = Server.SingleRequest().Json["generationConfig"];
        Assert.Equal("application/json", (string)generation["responseMimeType"]);
        Assert.Null(generation["responseJsonSchema"]);
    }

    [Theory]
    [InlineData("MAX_TOKENS", LlmFinishReason.Length)]
    [InlineData("SAFETY", LlmFinishReason.ContentFilter)]
    [InlineData("RECITATION", LlmFinishReason.ContentFilter)]
    [InlineData("OTHER", LlmFinishReason.Other)]
    public async Task FinishReason_IsMapped(string finishReason, LlmFinishReason expected)
    {
        Server.Respond(MockResponse.Json(Generated(finishReason, new { text = "{" })));

        Assert.Equal(expected, (await Complete(GeminiConfig())).FinishReason);
    }

    [Fact]
    public async Task BlockedPrompt_IsContentFilter()
    {
        Server.Respond(MockResponse.Json(new { promptFeedback = new { blockReason = "SAFETY" }, usageMetadata = new { promptTokenCount = 12 } }));

        var response = await Complete(GeminiConfig());

        Assert.Equal(LlmFinishReason.ContentFilter, response.FinishReason);
        Assert.Equal("SAFETY", response.RawFinishReason);
        Assert.Null(response.Text);
    }

    [Fact]
    public async Task InvalidKeyAs400_IsAuthenticationError()
    {
        const string body = "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\"," +
                            "\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"API_KEY_INVALID\",\"domain\":\"googleapis.com\"}]}}";
        Server.Respond(MockResponse.Json(body, 400));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(GeminiConfig()));

        Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.DoesNotContain(TestKey, ex.Message);
    }

    [Fact]
    public async Task ToolCallMode_IsRejectedWithoutRequest()
    {
        var ex = await Assert.ThrowsAsync<LlmException>(() => Complete(GeminiConfig(), Request(StructuredOutputMode.ToolCall)));

        Assert.Equal(LlmErrorKind.UnsupportedResponseFormat, ex.Kind);
        Assert.Empty(Server.Requests);
    }

    [Fact]
    public async Task Models_ArePagedAndOnlyGenerateContentModelsAreListed()
    {
        Server.Respond(r => r.Query.Contains("pageToken=page-2")
            ? MockResponse.Json(new { models = new[] { new { name = "models/gemini-a", displayName = "Gemini A", supportedGenerationMethods = new[] { "generateContent" } } } })
            : MockResponse.Json(new
            {
                models = new object[]
                {
                    new { name = "models/gemini-b", displayName = "Gemini B", inputTokenLimit = 1048576, supportedGenerationMethods = new[] { "generateContent", "countTokens" } },
                    new { name = "models/text-embedding-004", displayName = "Embedding", supportedGenerationMethods = new[] { "embedContent" } },
                },
                nextPageToken = "page-2",
            }));

        var result = await ListModels(GeminiConfig());

        Assert.Equal(ModelListStatus.Loaded, result.Status);
        Assert.Equal(new[] { "gemini-a", "gemini-b" }, result.Models.Select(m => m.Id));
        Assert.Equal(1048576, result.Models.Single(m => m.Id == "gemini-b").ContextLength);
        Assert.Equal(2, result.PagesFetched);
        Assert.All(Server.Requests, r =>
        {
            Assert.Equal("/v1beta/models", r.Path);
            Assert.Contains("pageSize=1000", r.Query);
            Assert.Equal(TestKey, r.Header("x-goog-api-key"));
        });
    }
}
