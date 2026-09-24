using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Fakt.Core.Llm;

/// <summary>
/// Сборка URL из Base URL и относительного пути без дублирования сегментов:
/// «http://host:1234/v1» + «/v1/models» → «http://host:1234/v1/models».
/// </summary>
public static class UrlBuilder
{
    public static string Combine(string baseUrl, string relativePath, IEnumerable<KeyValuePair<string, string>> query = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Не задан Base URL.", nameof(baseUrl));
        }

        var baseUri = new Uri(baseUrl.Trim(), UriKind.Absolute);
        var basePath = baseUri.AbsolutePath.Trim('/');
        var baseSegments = basePath.Length == 0 ? new List<string>() : basePath.Split('/').ToList();

        var path = (relativePath ?? string.Empty).Trim();
        string existingQuery = null;
        var questionMark = path.IndexOf('?');
        if (questionMark >= 0)
        {
            existingQuery = path.Substring(questionMark + 1);
            path = path.Substring(0, questionMark);
        }

        var relSegments = path.Trim('/').Length == 0 ? new List<string>() : path.Trim('/').Split('/').ToList();

        // Наибольшее перекрытие: хвост базового пути совпадает с началом относительного.
        var overlap = 0;
        for (var length = Math.Min(baseSegments.Count, relSegments.Count); length > 0; length--)
        {
            var match = true;
            for (var i = 0; i < length; i++)
            {
                if (!string.Equals(baseSegments[baseSegments.Count - length + i], relSegments[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                overlap = length;
                break;
            }
        }

        var segments = baseSegments.Concat(relSegments.Skip(overlap)).Where(s => s.Length > 0);
        var builder = new StringBuilder();
        builder.Append(baseUri.GetLeftPart(UriPartial.Authority));
        foreach (var segment in segments)
        {
            builder.Append('/').Append(segment);
        }

        var queryParts = new List<string>();
        if (!string.IsNullOrEmpty(baseUri.Query) && baseUri.Query.Length > 1)
        {
            queryParts.Add(baseUri.Query.Substring(1));
        }

        if (!string.IsNullOrEmpty(existingQuery))
        {
            queryParts.Add(existingQuery);
        }

        if (query != null)
        {
            foreach (var pair in query)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                queryParts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
            }
        }

        if (queryParts.Count > 0)
        {
            builder.Append('?').Append(string.Join("&", queryParts));
        }

        return builder.ToString();
    }

    /// <summary>Хост с портом в нижнем регистре — для привязки ключа к адресу.</summary>
    public static string HostOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : uri.Host.ToLowerInvariant() + ":" + uri.Port;
    }

    /// <summary>
    /// Привязка ключа API к адресу: схема, хост и нестандартный порт. Ключ, сохранённый для https, не отправляется
    /// на http того же хоста (открытым текстом) и на другой хост или порт.
    /// </summary>
    public static string KeyBindingOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme.ToLowerInvariant() + "://" + HostOf(url);
    }

    public static bool IsValidHttpUrl(string url, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Адрес не задан.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Адрес должен начинаться с http:// или https://.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Учётные данные в адресе не допускаются: используйте поле ключа.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Эндпоинт считается локальным/внутренним, если это loopback, частная сеть RFC 1918, link-local или имя без точки.
    /// Для остальных адресов интерфейс предупреждает, что записи отправляются внешнему провайдеру.
    /// </summary>
    public static bool IsLocalOrPrivate(string url)
    {
        if (!Uri.TryCreate(url?.Trim() ?? string.Empty, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        var host = uri.Host;
        if (host.IndexOf('.') < 0 && host.IndexOf(':') < 0)
        {
            return true;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".corp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
            {
                return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
            }

            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
