using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Fakt.Infrastructure.Llm;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fakt.UnitTests.Infrastructure.Llm;

public sealed class LlmHttpTransportTests : LlmAdapterTestBase
{
    private string Url => Server.BaseUrl + "v1/chat/completions";

    private static Dictionary<string, string> AuthHeaders() => new() { ["Authorization"] = "Bearer " + TestKey };

    [Fact]
    public async Task NoResponseWithinTimeout_IsTransientTimeout()
    {
        Server.Hang();

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Http.SendAsync(HttpMethod.Post, Url, new JObject { ["model"] = "m" }, AuthHeaders(), TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Timeout, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.DoesNotContain(TestKey, ex.Message);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsTimeout()
    {
        Server.Hang();
        using var cts = new CancellationTokenSource();

        var task = Http.SendAsync(HttpMethod.Post, Url, new JObject { ["model"] = "m" }, AuthHeaders(), TimeSpan.FromSeconds(30), cts.Token);
        Assert.True(await Server.WaitForRequestAsync(TimeSpan.FromSeconds(10)));
        cts.Cancel();

        // Отмена пользователем пробрасывается как отмена, а не как «тайм-аут провайдера».
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task DroppedConnection_IsNetworkError()
    {
        Server.Respond(MockResponse.Abort());

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Http.SendAsync(HttpMethod.Get, Server.BaseUrl + "v1/models", null, AuthHeaders(), TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Equal(LlmErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Response_CarriesStatusBodyAndRequestId()
    {
        Server.Respond(MockResponse.Json("{\"ok\":true}", 201).WithHeader("apim-request-id", "azure-req-7"));

        var result = await Http.SendAsync(HttpMethod.Post, Url, new JObject { ["x"] = "тест" }, null, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(201, result.StatusCode);
        Assert.True(result.IsSuccess);
        Assert.Equal("azure-req-7", result.RequestId);
        Assert.True((bool)result.Json()["ok"]);
        Assert.Equal("тест", (string)Server.SingleRequest().Json["x"]);
    }
}

public sealed class LlmHttpParsingTests
{
    private static HttpResponseHeaders Headers(params (string Name, string Value)[] headers)
    {
        var message = new HttpResponseMessage();
        foreach (var (name, value) in headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        return message.Headers;
    }

    [Fact]
    public void RetryAfter_Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), LlmHttp.RetryAfterOf(Headers(("Retry-After", "7"))));
    }

    [Fact]
    public void RetryAfterMs_TakesPrecedence()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), LlmHttp.RetryAfterOf(Headers(("retry-after-ms", "1500"), ("Retry-After", "30"))));
    }

    [Fact]
    public void RetryAfter_HttpDate()
    {
        var message = new HttpResponseMessage();
        message.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30));

        var delay = LlmHttp.RetryAfterOf(message.Headers);

        Assert.NotNull(delay);
        Assert.InRange(delay.Value, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(31));
    }

    [Fact]
    public void RetryAfter_DateInPast_IsZero()
    {
        var message = new HttpResponseMessage();
        message.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.Equal(TimeSpan.Zero, LlmHttp.RetryAfterOf(message.Headers));
    }

    [Theory]
    [InlineData("1s", 1000)]
    [InlineData("6m0s", 360_000)]
    [InlineData("20ms", 20)]
    [InlineData("1h2m3s", 3_723_000)]
    [InlineData("2.5", 2500)]
    public void RateLimitReset_Formats(string value, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), LlmHttp.RetryAfterOf(Headers(("x-ratelimit-reset-tokens", value))));
    }

    [Fact]
    public void SeveralResetHeaders_UseLongestWait()
    {
        Assert.Equal(TimeSpan.FromSeconds(360), LlmHttp.RetryAfterOf(Headers(("x-ratelimit-reset-requests", "1s"), ("x-ratelimit-reset-tokens", "6m0s"))));
    }

    [Fact]
    public void AnthropicReset_Rfc3339()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(40).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var delay = LlmHttp.RetryAfterOf(Headers(("anthropic-ratelimit-requests-reset", at)));

        Assert.NotNull(delay);
        Assert.InRange(delay.Value, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(41));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("0s")]
    [InlineData("")]
    public void UnparseableOrZeroReset_IsIgnored(string value)
    {
        Assert.Null(LlmHttp.RetryAfterOf(Headers(("x-ratelimit-reset-tokens", value))));
    }

    [Fact]
    public void NoHeaders_NoRetryAfter()
    {
        Assert.Null(LlmHttp.RetryAfterOf(Headers()));
        Assert.Null(LlmHttp.RetryAfterOf(null));
    }

    [Fact]
    public void ErrorDetails_ReadsGoogleArrayWithReason()
    {
        var (message, code, type, _) = LlmHttp.ErrorDetails(
            "[{\"error\":{\"code\":400,\"message\":\"API key not valid\",\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}]");

        Assert.Equal("API key not valid", message);
        Assert.Equal("400", code);
        Assert.Equal("INVALID_ARGUMENT/API_KEY_INVALID", type);
    }

    [Theory]
    [InlineData("{\"error\":\"Unauthorized\"}", "Unauthorized")]
    [InlineData("{\"detail\":\"Not authenticated\"}", "Not authenticated")]
    [InlineData("{\"message\":\"Invalid model\"}", "Invalid model")]
    [InlineData("upstream connect error", "upstream connect error")]
    public void ErrorDetails_ReadsCommonShapes(string body, string expected)
    {
        Assert.Equal(expected, LlmHttp.ErrorDetails(body).Message);
    }

    [Fact]
    public void ErrorDetails_AreSanitizedAndShortened()
    {
        var body = "{\"error\":{\"message\":\"Bad key sk-proj-AbCdEf1234567890XYZ\\nline two " + new string('x', 600) + "\"}}";

        var message = LlmHttp.ErrorDetails(body).Message;

        Assert.DoesNotContain("sk-proj-AbCdEf1234567890XYZ", message);
        Assert.DoesNotContain("\n", message);
        Assert.Equal(501, message.Length);
        Assert.EndsWith("…", message);
    }

    [Theory]
    [InlineData("Invalid key team-key-2026 for team-key-2026", "team-key-2026", "Invalid key *** for ***")]
    [InlineData("Invalid key abc", "abc", "Invalid key abc")]
    [InlineData(null, "team-key-2026", null)]
    [InlineData("text", null, "text")]
    public void RedactKey_RemovesConfiguredKeyFromErrorText(string text, string key, string expected)
    {
        Assert.Equal(expected, LlmAdapterBase.RedactKey(text, key));
    }

    [Fact]
    public void Classify_UsesContextPrefix()
    {
        var ex = LlmHttp.Classify(new HttpResult { StatusCode = 401, Body = "{\"error\":{\"message\":\"no\"}}" }, "Список моделей");

        Assert.StartsWith("Список моделей: ", ex.Message);
        Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
    }
}

public sealed class LlmAdapterRegistryTests
{
    [Fact]
    public void AllCatalogProviders_AreRegistered()
    {
        using var http = new LlmHttp(new HttpClientHandler { UseProxy = false });
        var registry = new LlmAdapterRegistry(http);

        Assert.Equal(ProviderCatalog.All.Select(p => p.Id).OrderBy(x => x), registry.Providers.Select(p => p.Id).OrderBy(x => x));
        Assert.IsType<OpenAiProtocolRouter>(registry.Get(ProviderCatalog.OpenAi));
        Assert.IsType<AnthropicAdapter>(registry.Get(ProviderCatalog.Anthropic));
        Assert.IsType<GeminiAdapter>(registry.Get(ProviderCatalog.Gemini));
        Assert.IsType<OllamaAdapter>(registry.Get(ProviderCatalog.Ollama));
        Assert.IsType<OpenAiChatAdapter>(registry.Get(ProviderCatalog.DeepSeek));
        Assert.Same(registry.Get("openai"), registry.Get("OpenAI"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    public void UnknownProvider_IsConfigurationError(string id)
    {
        using var http = new LlmHttp(new HttpClientHandler { UseProxy = false });

        Assert.Equal(LlmErrorKind.Configuration, Assert.Throws<LlmException>(() => new LlmAdapterRegistry(http).Get(id)).Kind);
    }

    [Fact]
    public void Register_ReplacesAdapterWithSameId()
    {
        using var http = new LlmHttp(new HttpClientHandler { UseProxy = false });
        var registry = new LlmAdapterRegistry(http);
        var custom = new ScriptedLlmAdapter();
        custom.Descriptor.Id = ProviderCatalog.Ollama;

        registry.Register(custom);

        Assert.Same(custom, registry.Get(ProviderCatalog.Ollama));
        Assert.Single(registry.Providers, p => p.Id == ProviderCatalog.Ollama);
    }
}
