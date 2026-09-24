using System;
using System.Globalization;
using Xunit;

namespace Fakt.IntegrationTests.RealLlm;

/// <summary>
/// Параметры реального интеграционного теста LLM — только из переменных окружения (ключ не хранится в репозитории
/// и не передаётся аргументами командной строки):
/// FAKT_REAL_LLM_PROVIDER (id провайдера, например openrouter, lmstudio), FAKT_REAL_LLM_MODEL (точный model ID),
/// FAKT_REAL_LLM_BASEURL (необязательно), FAKT_REAL_LLM_KEY (необязательно), FAKT_REAL_LLM_BATCH (записей в запросе),
/// FAKT_REAL_LLM_TIMEOUT (секунды), FAKT_REAL_LLM_DB (имя базы; иначе временная), FAKT_REAL_LLM_KEEP_DB=1 (не удалять базу).
/// </summary>
internal static class RealLlmConfig
{
    public static string Provider => Environment.GetEnvironmentVariable("FAKT_REAL_LLM_PROVIDER");

    public static string Model => Environment.GetEnvironmentVariable("FAKT_REAL_LLM_MODEL");

    public static string BaseUrl => Environment.GetEnvironmentVariable("FAKT_REAL_LLM_BASEURL");

    public static string ApiKey => Environment.GetEnvironmentVariable("FAKT_REAL_LLM_KEY");

    public static int BatchRows => Int("FAKT_REAL_LLM_BATCH", 10);

    public static int TimeoutSeconds => Int("FAKT_REAL_LLM_TIMEOUT", 180);

    public static string Database => Environment.GetEnvironmentVariable("FAKT_REAL_LLM_DB");

    public static bool KeepDatabase => Environment.GetEnvironmentVariable("FAKT_REAL_LLM_KEEP_DB") == "1";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Model);

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
}

/// <summary>Тест обращается к настоящему провайдеру LLM и пропускается, если провайдер не задан.</summary>
public sealed class RealLlmFactAttribute : FactAttribute
{
    public RealLlmFactAttribute()
    {
        if (!RealLlmConfig.IsConfigured)
        {
            Skip = "Реальный LLM не настроен: задайте FAKT_REAL_LLM_PROVIDER и FAKT_REAL_LLM_MODEL (и при необходимости FAKT_REAL_LLM_KEY).";
        }
    }
}
