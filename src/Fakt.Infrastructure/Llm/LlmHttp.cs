using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Llm;

public sealed class HttpResult
{
    public int StatusCode { get; set; }
    public string Body { get; set; }
    public HttpResponseHeaders Headers { get; set; }
    public string RequestId { get; set; }
    public TimeSpan Elapsed { get; set; }

    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

    public JToken Json()
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            return null;
        }

        try
        {
            return JToken.Parse(Body);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }
}

/// <summary>
/// HTTP-транспорт адаптеров LLM на HttpClient (.NET Framework 4.8). Один экземпляр на приложение;
/// тайм-аут задаётся на запрос. Проверка сертификатов сервера не отключается.
/// </summary>
public sealed class LlmHttp : IDisposable
{
    private readonly HttpClient _client;

    public LlmHttp(HttpMessageHandler handler = null)
    {
        _client = handler == null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseProxy = true })
            : new HttpClient(handler);
        _client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("FAKT/1.0");
    }

    public async Task<HttpResult> SendAsync(HttpMethod method, string url, JObject body, IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null)
        {
            request.Content = new StringContent(body.ToString(Formatting.None), new UTF8Encoding(false), "application/json");
        }

        if (headers != null)
        {
            foreach (var header in headers)
            {
                if (string.IsNullOrEmpty(header.Value))
                {
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && request.Content != null)
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var text = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpResult
            {
                StatusCode = (int)response.StatusCode,
                Body = text,
                Headers = response.Headers,
                RequestId = RequestIdOf(response.Headers),
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new LlmException(LlmErrorKind.Timeout, $"Нет ответа за {timeout.TotalSeconds:0} с. Запрос мог быть принят и оплачен провайдером.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorKind.Network, "Сервис недоступен: " + Describe(ex), inner: ex);
        }
        catch (WebException ex)
        {
            throw new LlmException(LlmErrorKind.Network, "Сервис недоступен: " + ex.Message, inner: ex);
        }
        catch (IOException ex)
        {
            throw new LlmException(LlmErrorKind.Network, "Соединение прервано: " + ex.Message, inner: ex);
        }
    }

    private static string Describe(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        var text = string.Join(" → ", messages);
        if (text.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("trust relationship", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("защищенный канал", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            text += ". Проверьте поддержку TLS 1.2 и корневые сертификаты Windows (см. docs/SETUP.md, раздел TLS).";
        }

        return LogSanitizer.Sanitize(text);
    }

    public static string RequestIdOf(HttpResponseHeaders headers)
    {
        if (headers == null)
        {
            return null;
        }

        foreach (var name in new[] { "x-request-id", "request-id", "apim-request-id", "x-ms-request-id", "x-groq-id", "cf-ray", "x-amzn-requestid" })
        {
            if (headers.TryGetValues(name, out var values))
            {
                var value = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(value))
                {
                    return value.Length > 128 ? value.Substring(0, 128) : value;
                }
            }
        }

        return null;
    }

    /// <summary>Retry-After (секунды или HTTP-дата), retry-after-ms (Azure), x-ratelimit-reset-* («1s», «6m0s»), anthropic-ratelimit-*-reset (RFC 3339).</summary>
    public static TimeSpan? RetryAfterOf(HttpResponseHeaders headers)
    {
        if (headers == null)
        {
            return null;
        }

        if (headers.TryGetValues("retry-after-ms", out var ms) && double.TryParse(ms.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        }

        if (headers.RetryAfter != null)
        {
            if (headers.RetryAfter.Delta.HasValue)
            {
                return headers.RetryAfter.Delta;
            }

            if (headers.RetryAfter.Date.HasValue)
            {
                var delta = headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }
        }

        TimeSpan? best = null;
        foreach (var header in headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (!(name.StartsWith("x-ratelimit-reset", StringComparison.Ordinal) || (name.StartsWith("anthropic-ratelimit-", StringComparison.Ordinal) && name.EndsWith("-reset", StringComparison.Ordinal))))
            {
                continue;
            }

            var parsed = ParseReset(header.Value.FirstOrDefault());
            if (parsed.HasValue && (!best.HasValue || parsed > best))
            {
                best = parsed;
            }
        }

        return best;
    }

    private static TimeSpan? ParseReset(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) && value.Contains("T"))
        {
            var delta = moment - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // Формат OpenAI: «1s», «6m0s», «20ms», «1h2m3s».
        double total = 0;
        var number = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsDigit(ch) || ch == '.')
            {
                number.Append(ch);
                continue;
            }

            if (number.Length == 0 || !double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            number.Clear();
            if (ch == 'h') total += n * 3600;
            else if (ch == 'm' && i + 1 < value.Length && value[i + 1] == 's') { total += n / 1000; i++; }
            else if (ch == 'm') total += n * 60;
            else if (ch == 's') total += n;
            else return null;
        }

        return total > 0 ? TimeSpan.FromSeconds(total) : (TimeSpan?)null;
    }

    /// <summary>Сообщение об ошибке из тела ответа провайдера (разные форматы), очищенное и укороченное.</summary>
    public static (string Message, string Code, string Type, string Param) ErrorDetails(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null, null, null);
        }

        try
        {
            var token = JToken.Parse(body);
            if (token is JArray array && array.Count > 0)
            {
                token = array[0];
            }

            var obj = token as JObject;
            var error = obj?["error"];
            string message = null, code = null, type = null, param = null;
            if (error is JObject errorObj)
            {
                message = (string)errorObj["message"] ?? (string)errorObj["error"];
                code = errorObj["code"]?.ToString();
                type = (string)errorObj["type"] ?? (string)errorObj["status"];
                param = (string)errorObj["param"];
                if (errorObj["details"] is JArray details)
                {
                    var reason = details.OfType<JObject>().Select(d => (string)d["reason"]).FirstOrDefault(r => r != null);
                    code ??= reason;
                    if (reason != null)
                    {
                        type = type == null ? reason : type + "/" + reason;
                    }
                }
            }
            else if (error != null && error.Type == JTokenType.String)
            {
                message = (string)error;
            }

            message ??= (string)obj?["message"] ?? (string)obj?["detail"];
            code ??= obj?["code"]?.ToString();
            type ??= (string)obj?["type"];
            return (Shorten(message), code, type, param);
        }
        catch (JsonException)
        {
            return (Shorten(body), null, null, null);
        }
    }

    private static string Shorten(string text)
    {
        if (text == null)
        {
            return null;
        }

        text = LogSanitizer.Sanitize(text.Replace('\n', ' ').Replace('\r', ' '));
        return text.Length > 500 ? text.Substring(0, 500) + "…" : text;
    }

    /// <summary>Классификация неуспешного ответа по коду HTTP и тексту ошибки провайдера.</summary>
    public static LlmException Classify(HttpResult result, string context = null)
    {
        var (message, code, type, param) = ErrorDetails(result.Body);
        var haystack = ((message ?? string.Empty) + " " + (code ?? string.Empty) + " " + (type ?? string.Empty)).ToLowerInvariant();
        var retryAfter = RetryAfterOf(result.Headers);
        var status = result.StatusCode;
        var prefix = string.IsNullOrEmpty(context) ? string.Empty : context + ": ";
        var detail = message != null ? $" ({message})" : string.Empty;

        LlmException Make(LlmErrorKind kind, string text, string parameter = null) =>
            new(kind, prefix + text + detail, status, result.RequestId, retryAfter, code, parameter ?? param);

        if (Contains(haystack, "api key not valid", "api_key_invalid", "invalid api key", "invalid_api_key", "incorrect api key", "authentication_error", "unauthorized"))
        {
            return Make(LlmErrorKind.Authentication, LlmErrorText.Describe(LlmErrorKind.Authentication));
        }

        if (Contains(haystack, "insufficient_quota", "exceeded your current quota", "billing", "credit balance", "insufficient balance", "payment required"))
        {
            return Make(LlmErrorKind.QuotaExceeded, LlmErrorText.Describe(LlmErrorKind.QuotaExceeded));
        }

        if (Contains(haystack, "context_length", "context length", "maximum context", "context window", "too many tokens", "prompt is too long",
                "reduce the length", "input is too long", "maximum input length", "token limit"))
        {
            return Make(LlmErrorKind.ContextLengthExceeded, LlmErrorText.Describe(LlmErrorKind.ContextLengthExceeded));
        }

        switch (status)
        {
            case 401:
                return Make(LlmErrorKind.Authentication, LlmErrorText.Describe(LlmErrorKind.Authentication));
            case 402:
                return Make(LlmErrorKind.QuotaExceeded, LlmErrorText.Describe(LlmErrorKind.QuotaExceeded));
            case 403:
                return Make(LlmErrorKind.PermissionDenied, LlmErrorText.Describe(LlmErrorKind.PermissionDenied));
            case 404:
                return Contains(haystack, "model", "deployment", "not_found_error", "does not exist")
                    ? Make(LlmErrorKind.ModelNotFound, LlmErrorText.Describe(LlmErrorKind.ModelNotFound))
                    : Make(LlmErrorKind.NotSupported, "Адрес не найден (404): проверьте Base URL и пути");
            case 408:
                return Make(LlmErrorKind.Timeout, LlmErrorText.Describe(LlmErrorKind.Timeout));
            case 413:
                return Make(LlmErrorKind.ContextLengthExceeded, "Запрос слишком большой (413)");
            case 429:
                return Make(LlmErrorKind.RateLimited, LlmErrorText.Describe(LlmErrorKind.RateLimited));
            case 529:
                return Make(LlmErrorKind.Overloaded, LlmErrorText.Describe(LlmErrorKind.Overloaded));
        }

        if (status == 503 && Contains(haystack, "overload"))
        {
            return Make(LlmErrorKind.Overloaded, LlmErrorText.Describe(LlmErrorKind.Overloaded));
        }

        if (status >= 500)
        {
            return Make(LlmErrorKind.ServerError, $"Ошибка сервера провайдера (HTTP {status})");
        }

        if (Contains(haystack, "content_filter", "content filter", "responsibleaipolicyviolation", "safety"))
        {
            return Make(LlmErrorKind.ContentFiltered, LlmErrorText.Describe(LlmErrorKind.ContentFiltered));
        }

        if (Contains(haystack, "response_format", "json_schema", "json schema", "structured output", "output_config", "responsejsonschema",
                "responseschema", "responsemimetype", "tool_choice", "schema"))
        {
            return Make(LlmErrorKind.UnsupportedResponseFormat, LlmErrorText.Describe(LlmErrorKind.UnsupportedResponseFormat));
        }

        if (Contains(haystack, "temperature"))
        {
            return Make(LlmErrorKind.UnsupportedParameter, "Параметр temperature не поддерживается моделью", "temperature");
        }

        if (Contains(haystack, "unsupported parameter", "unknown parameter", "unrecognized request argument", "not supported", "extra inputs are not permitted"))
        {
            return Make(LlmErrorKind.UnsupportedParameter, LlmErrorText.Describe(LlmErrorKind.UnsupportedParameter));
        }

        if (Contains(haystack, "model") && Contains(haystack, "not found", "does not exist", "invalid model", "unknown model", "no such model", "not available"))
        {
            return Make(LlmErrorKind.ModelNotFound, LlmErrorText.Describe(LlmErrorKind.ModelNotFound));
        }

        return Make(LlmErrorKind.BadRequest, $"Провайдер отклонил запрос (HTTP {status})");
    }

    private static bool Contains(string haystack, params string[] needles) => needles.Any(n => haystack.IndexOf(n, StringComparison.Ordinal) >= 0);

    public void Dispose() => _client.Dispose();
}
