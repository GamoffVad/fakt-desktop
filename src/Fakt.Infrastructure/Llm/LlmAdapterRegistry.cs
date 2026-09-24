using System;
using System.Collections.Generic;
using System.Linq;
using Fakt.Core.Llm;

namespace Fakt.Infrastructure.Llm;

/// <summary>Реестр адаптеров. Новые провайдеры подключаются методом <see cref="Register"/>.</summary>
public sealed class LlmAdapterRegistry : ILlmAdapterRegistry
{
    private readonly Dictionary<string, ILlmAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LlmProviderDescriptor> _providers = new();

    public LlmAdapterRegistry(LlmHttp http)
    {
        foreach (var descriptor in ProviderCatalog.All)
        {
            ILlmAdapter adapter = descriptor.Id switch
            {
                ProviderCatalog.OpenAi or ProviderCatalog.OpenAiCompatible => new OpenAiProtocolRouter(http, descriptor),
                ProviderCatalog.Anthropic => new AnthropicAdapter(http, descriptor),
                ProviderCatalog.Gemini => new GeminiAdapter(http, descriptor),
                ProviderCatalog.Ollama => new OllamaAdapter(http, descriptor),
                _ => new OpenAiChatAdapter(http, descriptor),
            };
            Register(adapter);
        }
    }

    public IReadOnlyList<LlmProviderDescriptor> Providers => _providers;

    public void Register(ILlmAdapter adapter)
    {
        if (adapter == null)
        {
            throw new ArgumentNullException(nameof(adapter));
        }

        _adapters[adapter.Descriptor.Id] = adapter;
        _providers.RemoveAll(p => string.Equals(p.Id, adapter.Descriptor.Id, StringComparison.OrdinalIgnoreCase));
        _providers.Add(adapter.Descriptor);
    }

    public ILlmAdapter Get(string providerId)
    {
        if (providerId != null && _adapters.TryGetValue(providerId, out var adapter))
        {
            return adapter;
        }

        throw new LlmException(LlmErrorKind.Configuration, $"Провайдер «{providerId}» не зарегистрирован.");
    }

    public LlmProviderDescriptor Find(string providerId) => _providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
}
