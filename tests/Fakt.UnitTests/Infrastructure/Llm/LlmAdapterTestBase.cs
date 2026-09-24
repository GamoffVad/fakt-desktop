using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Infrastructure.Llm;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json;

namespace Fakt.UnitTests.Infrastructure.Llm;

/// <summary>
/// Общая часть тестов адаптеров: имитатор сервера на 127.0.0.1, настоящий LlmHttp (без прокси) и реестр адаптеров.
/// Ключ API синтетический; ни один тест не обращается к внешней сети.
/// </summary>
public abstract class LlmAdapterTestBase : IDisposable
{
    protected const string TestKey = "sk-test-UNIT-0123456789abcdef";

    protected LlmAdapterTestBase()
    {
        Server = new MockHttpServer();
        Http = new LlmHttp(new HttpClientHandler { UseProxy = false });
        Registry = new LlmAdapterRegistry(Http);
    }

    internal MockHttpServer Server { get; }

    protected LlmHttp Http { get; }

    protected LlmAdapterRegistry Registry { get; }

    /// <summary>Профиль с Base URL на имитаторе: корень сервера + <paramref name="path"/>.</summary>
    protected LlmRuntimeConfig Config(string providerId, string path = "v1", string apiKey = TestKey, Action<LlmProfile> configure = null,
        IReadOnlyDictionary<string, string> headers = null)
    {
        var profile = new LlmProfile
        {
            Name = "test",
            ProviderId = providerId,
            BaseUrl = Server.BaseUrl + path,
            ModelId = "test-model",
            TimeoutSeconds = 30,
        };
        configure?.Invoke(profile);
        return new LlmRuntimeConfig(profile, apiKey, headers);
    }

    protected static LlmJsonRequest Request(StructuredOutputMode mode = StructuredOutputMode.JsonSchema, double? temperature = null) => new()
    {
        SystemPrompt = "SYSTEM-PROMPT-MARKER",
        UserContent = "USER-CONTENT-MARKER",
        SchemaName = JsonSchemas.ExtractionSchemaName,
        Schema = JsonSchemas.Extraction(),
        MaxOutputTokens = 1234,
        Temperature = temperature,
        Mode = mode,
        Purpose = "test",
    };

    protected Task<LlmResponse> Complete(LlmRuntimeConfig config, LlmJsonRequest request = null) =>
        Registry.Get(config.Profile.ProviderId).CompleteJsonAsync(config, request ?? Request(), CancellationToken.None);

    protected Task<ModelListResult> ListModels(LlmRuntimeConfig config) =>
        Registry.Get(config.Profile.ProviderId).ListModelsAsync(config, CancellationToken.None);

    /// <summary>Тело ошибки в формате OpenAI: {"error": {"message", "type", "param", "code"}}.</summary>
    protected static string Err(string message, string type = null, string code = null, string param = null) =>
        JsonConvert.SerializeObject(new { error = new { message, type, param, code } });

    public void Dispose()
    {
        Http.Dispose();
        Server.Dispose();
    }
}
