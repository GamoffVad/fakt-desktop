using System.Collections.Generic;
using Fakt.Core.Llm;

namespace Fakt.Infrastructure.Llm;

/// <summary>
/// Публичные значения по умолчанию и набор полей поддерживаемых провайдеров (сверено с официальной
/// документацией 24.09.2026, см. docs/PROVIDERS.md). Список расширяется регистрацией адаптеров и не
/// является исчерпывающим каталогом всех существующих провайдеров.
/// </summary>
public static class ProviderCatalog
{
    public const string OpenAi = "openai";
    public const string OpenAiCompatible = "openai_compatible";
    public const string AzureOpenAi = "azure_openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string Mistral = "mistral";
    public const string DeepSeek = "deepseek";
    public const string Qwen = "qwen";
    public const string XAi = "xai";
    public const string Groq = "groq";
    public const string OpenRouter = "openrouter";
    public const string Together = "together";
    public const string Fireworks = "fireworks";
    public const string Ollama = "ollama";
    public const string LmStudio = "lmstudio";

    // Ключи Options профиля.
    public const string OptProtocol = "protocol";
    public const string OptModelsPath = "models_path";
    public const string OptChatPath = "chat_path";
    public const string OptResponsesPath = "responses_path";
    public const string OptAuthStyle = "auth_style";
    public const string OptAuthHeader = "auth_header";
    public const string OptMaxTokensParam = "max_tokens_param";
    public const string OptSystemRole = "system_role";
    public const string OptApiMode = "api_mode";
    public const string OptApiVersion = "api_version";
    public const string OptAnthropicVersion = "anthropic_version";
    public const string OptWorkspaceId = "workspace_id";
    public const string OptEnableThinking = "enable_thinking";
    public const string OptAccountId = "account_id";
    public const string OptKeepAlive = "keep_alive";
    public const string OptNumCtx = "num_ctx";
    public const string OptOrganization = "organization";
    public const string OptProject = "project";
    public const string OptGeminiSchemaField = "gemini_schema_field";
    public const string OptStrict = "strict";

    private static readonly StructuredOutputMode[] OpenAiLikeModes = { StructuredOutputMode.JsonSchema, StructuredOutputMode.JsonObject, StructuredOutputMode.ToolCall, StructuredOutputMode.PromptOnly };

    public static IReadOnlyList<LlmProviderDescriptor> All { get; } = new List<LlmProviderDescriptor>
    {
        new()
        {
            Id = OpenAi, DisplayName = "OpenAI", DefaultBaseUrl = "https://api.openai.com/v1",
            Fields = new[]
            {
                new ProviderField(OptProtocol, "Протокол", ProviderFieldKind.Choice, true, "chat", new[] { "chat", "responses" },
                    "chat — /chat/completions (max_completion_tokens); responses — /responses (max_output_tokens, store=false)."),
                new ProviderField(OptOrganization, "Organization ID (необязательно)", ProviderFieldKind.Text),
                new ProviderField(OptProject, "Project ID (необязательно)", ProviderFieldKind.Text),
            },
            DocumentedModes = OpenAiLikeModes,
            DocumentationUrl = "https://developers.openai.com/api/docs",
        },
        new()
        {
            Id = OpenAiCompatible, DisplayName = "OpenAI Compatible", DefaultBaseUrl = "http://localhost:8000/v1",
            ApiKey = ApiKeyRequirement.Optional,
            Fields = new[]
            {
                new ProviderField(OptProtocol, "Протокол", ProviderFieldKind.Choice, true, "chat", new[] { "chat", "responses" },
                    "Выберите протокол, который действительно поддерживает сервис."),
                new ProviderField(OptModelsPath, "Путь списка моделей", ProviderFieldKind.Text, false, "models",
                    help: "Относительно Base URL; «/v1» не дублируется. Пусто — список не поддерживается, модель вводится вручную."),
                new ProviderField(OptChatPath, "Путь генерации (chat)", ProviderFieldKind.Text, true, "chat/completions"),
                new ProviderField(OptResponsesPath, "Путь генерации (responses)", ProviderFieldKind.Text, false, "responses"),
                new ProviderField(OptAuthStyle, "Авторизация", ProviderFieldKind.Choice, true, "bearer", new[] { "bearer", "header", "none" },
                    "bearer — Authorization: Bearer <ключ>; header — ключ в заголовке с указанным именем; none — без ключа."),
                new ProviderField(OptAuthHeader, "Имя заголовка ключа (для header)", ProviderFieldKind.Text, false, "api-key"),
                new ProviderField(OptMaxTokensParam, "Параметр предела ответа", ProviderFieldKind.Choice, true, "max_tokens", new[] { "max_tokens", "max_completion_tokens" }),
                new ProviderField(OptSystemRole, "Роль системной инструкции", ProviderFieldKind.Choice, true, "system", new[] { "system", "developer" }),
            },
            DocumentedModes = OpenAiLikeModes,
            Notes = "Возможности (JSON Schema, JSON mode) проверяются тестовым запросом, а не выводятся из имени сервиса.",
        },
        new()
        {
            Id = AzureOpenAi, DisplayName = "Azure OpenAI", DefaultBaseUrl = "https://<resource>.openai.azure.com",
            ModelFieldLabel = "Развёртывание (deployment)",
            Fields = new[]
            {
                new ProviderField(OptApiMode, "Поверхность API", ProviderFieldKind.Choice, true, "v1", new[] { "v1", "classic" },
                    "v1 — /openai/v1/chat/completions, в поле model передаётся имя развёртывания; classic — /openai/deployments/{deployment}/chat/completions?api-version=…"),
                new ProviderField(OptApiVersion, "API version (для classic)", ProviderFieldKind.Text, false, "2024-10-21"),
                new ProviderField(OptAuthStyle, "Авторизация", ProviderFieldKind.Choice, true, "api-key", new[] { "api-key", "bearer" },
                    "api-key — заголовок api-key; bearer — токен Microsoft Entra ID в поле ключа."),
            },
            ModelListingNote = "Список, получаемый по API, — каталог моделей ресурса, а не развёртывания. Для вызова нужно имя развёртывания: введите его вручную. Перечисление развёртываний доступно только через Azure Resource Manager и не требуется для работы.",
            DocumentedModes = OpenAiLikeModes,
            DocumentationUrl = "https://learn.microsoft.com/azure/foundry/openai/reference",
        },
        new()
        {
            Id = Anthropic, DisplayName = "Anthropic", DefaultBaseUrl = "https://api.anthropic.com",
            Fields = new[]
            {
                new ProviderField(OptAnthropicVersion, "anthropic-version", ProviderFieldKind.Text, true, "2023-06-01"),
                new ProviderField(OptAuthStyle, "Заголовок ключа", ProviderFieldKind.Choice, true, "x-api-key", new[] { "x-api-key", "bearer" }),
                new ProviderField(OptWorkspaceId, "anthropic-workspace-id (необязательно)", ProviderFieldKind.Text),
            },
            DocumentedModes = new[] { StructuredOutputMode.JsonSchema, StructuredOutputMode.ToolCall, StructuredOutputMode.PromptOnly },
            DocumentationUrl = "https://platform.claude.com/docs/en/api/overview",
            Notes = "Structured outputs: output_config.format (GA). Принудительный вызов инструмента недоступен на части новых моделей. Температура у новых моделей не настраивается.",
        },
        new()
        {
            Id = Gemini, DisplayName = "Google Gemini", DefaultBaseUrl = "https://generativelanguage.googleapis.com",
            Fields = new[]
            {
                new ProviderField(OptApiVersion, "Версия API", ProviderFieldKind.Choice, true, "v1beta", new[] { "v1beta", "v1" }),
                new ProviderField(OptGeminiSchemaField, "Поле схемы ответа", ProviderFieldKind.Choice, true, "responseJsonSchema", new[] { "responseJsonSchema", "responseSchema" },
                    "responseJsonSchema — JSON Schema; responseSchema — подмножество OpenAPI (устаревший вариант)."),
            },
            DocumentedModes = new[] { StructuredOutputMode.JsonSchema, StructuredOutputMode.JsonObject, StructuredOutputMode.PromptOnly },
            DocumentationUrl = "https://ai.google.dev/api",
            Notes = "Неверный ключ возвращается кодом 400 INVALID_ARGUMENT (API_KEY_INVALID), а не 401.",
        },
        new()
        {
            Id = Mistral, DisplayName = "Mistral", DefaultBaseUrl = "https://api.mistral.ai/v1",
            DocumentedModes = OpenAiLikeModes, DocumentationUrl = "https://docs.mistral.ai/api/",
        },
        new()
        {
            Id = DeepSeek, DisplayName = "DeepSeek", DefaultBaseUrl = "https://api.deepseek.com",
            DocumentedModes = new[] { StructuredOutputMode.JsonObject, StructuredOutputMode.ToolCall, StructuredOutputMode.PromptOnly },
            DocumentationUrl = "https://api-docs.deepseek.com/",
            Notes = "Поддерживается только response_format json_object; схема передаётся в промпте и проверяется приложением.",
        },
        new()
        {
            Id = Qwen, DisplayName = "Qwen / Alibaba Cloud Model Studio", DefaultBaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
            Fields = new[]
            {
                new ProviderField("region", "Регион (подставляет Base URL)", ProviderFieldKind.Choice, false, "intl",
                    new[] { "intl", "cn-beijing", "us", "custom" },
                    "intl — dashscope-intl.aliyuncs.com; cn-beijing — dashscope.aliyuncs.com; us — dashscope-us.aliyuncs.com; custom — домен рабочего пространства {WorkspaceId}.{region}.maas.aliyuncs.com. Ключ привязан к региону."),
                new ProviderField(OptEnableThinking, "enable_thinking", ProviderFieldKind.Choice, true, "false", new[] { "false", "true", "не отправлять" },
                    "Структурированный вывод несовместим с режимом рассуждений."),
            },
            ModelListingNote = "Эндпоинт списка моделей в документации Model Studio не описан; если сервис не вернёт список, введите model ID вручную.",
            DocumentedModes = new[] { StructuredOutputMode.JsonObject, StructuredOutputMode.JsonSchema, StructuredOutputMode.PromptOnly },
            DocumentationUrl = "https://www.alibabacloud.com/help/en/model-studio/",
        },
        new()
        {
            Id = XAi, DisplayName = "xAI", DefaultBaseUrl = "https://api.x.ai/v1",
            DocumentedModes = OpenAiLikeModes, DocumentationUrl = "https://docs.x.ai/",
        },
        new()
        {
            Id = Groq, DisplayName = "Groq", DefaultBaseUrl = "https://api.groq.com/openai/v1",
            DocumentedModes = OpenAiLikeModes, DocumentationUrl = "https://console.groq.com/docs",
            Notes = "Строгий json_schema поддерживается только частью моделей; для остальных используется json_object.",
        },
        new()
        {
            Id = OpenRouter, DisplayName = "OpenRouter", DefaultBaseUrl = "https://openrouter.ai/api/v1",
            DocumentedModes = OpenAiLikeModes, DocumentationUrl = "https://openrouter.ai/docs",
            Notes = "Строгость схемы зависит от конечного провайдера модели. Ответ HTTP 200 может содержать ошибку в теле.",
        },
        new()
        {
            Id = Together, DisplayName = "Together AI", DefaultBaseUrl = "https://api.together.xyz/v1",
            DocumentedModes = OpenAiLikeModes, DocumentationUrl = "https://docs.together.ai/",
        },
        new()
        {
            Id = Fireworks, DisplayName = "Fireworks AI", DefaultBaseUrl = "https://api.fireworks.ai/inference/v1",
            Fields = new[]
            {
                new ProviderField(OptAccountId, "Account ID для списка моделей", ProviderFieldKind.Text, false, "fireworks",
                    help: "fireworks — публичный каталог serverless-моделей; укажите свой аккаунт для развёрнутых моделей."),
            },
            DocumentedModes = OpenAiLikeModes, DocumentationUrl = "https://docs.fireworks.ai/",
        },
        new()
        {
            Id = Ollama, DisplayName = "Ollama", DefaultBaseUrl = "http://localhost:11434", ApiKey = ApiKeyRequirement.Optional, IsLocalByDefault = true,
            Fields = new[]
            {
                new ProviderField(OptNumCtx, "num_ctx (размер контекста)", ProviderFieldKind.Number, false, "8192",
                    help: "Контекст по умолчанию у Ollama мал; для пакетов записей укажите 8192 и более, если модель и память позволяют."),
                new ProviderField(OptKeepAlive, "keep_alive", ProviderFieldKind.Text, false, "10m"),
            },
            DocumentedModes = new[] { StructuredOutputMode.JsonSchema, StructuredOutputMode.JsonObject, StructuredOutputMode.PromptOnly },
            DocumentationUrl = "https://docs.ollama.com/api",
            Notes = "Установка Ollama на Windows 7 не гарантируется: подключайтесь к серверу Ollama на другой машине.",
        },
        new()
        {
            Id = LmStudio, DisplayName = "LM Studio", DefaultBaseUrl = "http://localhost:1234/v1", ApiKey = ApiKeyRequirement.Optional, IsLocalByDefault = true,
            DocumentedModes = OpenAiLikeModes,
            DocumentationUrl = "https://lmstudio.ai/docs/developer",
            Notes = "Первый запрос к незагруженной модели может выполняться долго (JIT-загрузка). Установка LM Studio на Windows 7 не гарантируется: используйте сервер на другой машине.",
        },
    };
}
