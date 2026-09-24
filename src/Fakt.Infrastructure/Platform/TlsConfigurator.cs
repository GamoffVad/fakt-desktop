using System;
using System.Net;

namespace Fakt.Infrastructure.Platform;

/// <summary>
/// Настройка TLS для HttpClient/HttpWebRequest. На Windows 7 SChannel по умолчанию предлагает клиенту
/// только TLS 1.0, поэтому TLS 1.2 запрашивается явно (явный запрос разрешён даже при DisabledByDefault).
/// TLS 1.3 на Windows 7 отсутствует. На Windows 8+ используется выбор ОС (SystemDefault).
/// Проверка сертификатов не отключается.
/// </summary>
public static class TlsConfigurator
{
    public static string Configure()
    {
        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 32);
        ServicePointManager.Expect100Continue = false;
        var version = Environment.OSVersion.Version;
        if (version.Major < 6 || (version.Major == 6 && version.Minor <= 1))
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            return "TLS 1.2 (явно, Windows 7)";
        }

        return "системные настройки TLS";
    }

    public static bool IsWindows7OrOlder
    {
        get
        {
            var version = Environment.OSVersion.Version;
            return version.Major < 6 || (version.Major == 6 && version.Minor <= 1);
        }
    }
}
