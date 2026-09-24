# Совместимость платформы с Windows 7 SP1 x64 — исследование

Дата исследования: **2026-09-24**. Целевая платформа клиента: **Windows 7 SP1 x64**.
Стек: WPF на **.NET Framework 4.8**; встроенный worker на **Python 3.8** (embeddable distribution) с `pandas`/`numpy`; подключение к **SQL Server 2025** (отдельный современный сервер) и к HTTPS LLM API.

Легенда статусов:

- **подтверждено (URL)** — факт взят из официального источника (learn.microsoft.com, support.microsoft.com, python.org, pypi.org, numpy.org, pandas.pydata.org, официальные репозитории Microsoft/Python/numpy/pandas на GitHub).
- **подтверждено (URL), сообщество** — issue/комментарий пользователей или мейнтейнеров на GitHub; это не официальная политика поддержки.
- **эмпирически** — моя собственная проверка (openssl, HEAD-запросы) на 2026-09-24; это не официальный источник.
- **не удалось проверить** — официального подтверждения не нашёл.
- **Вывод** — моя интерпретация фактов, а не факт.

---

## 0. Краткие выводы

1. **.NET Framework 4.8** ставится на Windows 7 SP1. **4.8.1** на Windows 7 не ставится. Сама Windows 7 давно вне поддержки: ESU Year 3 закончился 2023-01-11.
2. **TLS 1.2** в Windows 7 есть, но по умолчанию выключен для клиента. **TLS 1.3 нет** совсем. Шифронаборов `TLS_ECDHE_RSA_WITH_AES_*_GCM_*` в Windows 7 **нет**.
   Чтобы HTTPS работал, есть два пути: включить TLS 1.2 в реестре SChannel (`DisabledByDefault=0`, `Enabled=1`) или явно задать `SecurityProtocolType.Tls12` в коде. Первый путь надёжнее: он покрывает и `System.Data.SqlClient`.
3. На 2026-09-24 эмпирически проверил LLM API с набором шифров Windows 7:
   - OpenAI, Anthropic, Google Gemini, OpenRouter, Mistral, Groq и Yandex рукопожатие проходят.
   - **DeepSeek (api.deepseek.com) не проходит.**
4. Встроенный в .NET 4.8 **`System.Data.SqlClient`** умеет только TDS 7.x. **`Encrypt=Strict` / TDS 8.0 он не поддерживает.**
   SQL Server 2025 по-прежнему принимает клиентов TDS 7.x по TLS 1.2, если на сервере не включён **Force Strict Encryption** и на ОС сервера не оставлен только TLS 1.3.
   Официальной поддержки `Microsoft.Data.SqlClient` на Windows 7 нет: она привязана к списку ОС .NET Framework, где Windows 7 помечена как out-of-support.
5. **SQL Server 2025** ставится только на Windows 10+ и Windows Server 2019+.
   Full-Text Search есть во всех редакциях, включая Express: редакция Express with Advanced Services упразднена, её функции вошли в Express.
   В 2025 все компоненты FTS заменены на «version 2». Русский (LCID 1049) поддерживается.
6. **Python 3.8** — последняя ветка с поддержкой Windows 7. Последний релиз с бинарниками для Windows — **3.8.10**. EOL ветки 3.8 — **2024-10-07**.
   Для embeddable-сборки на Windows 7 нужны KB2533623 (или заменяющее его обновление) и Universal CRT (KB2999226/KB3118401).
7. Последние версии с поддержкой Python 3.8:
   - **numpy 1.24.4** и **pandas 2.0.3**; колёса `cp38-win_amd64` есть.
   - Известная проблема: numpy 1.24.x **падает при импорте на Windows 7 с 32-битным Python**. Для 64-битного Python таких жалоб не нашёл.
   - Сообщество рекомендует numpy **1.23.5** как «последнюю, работающую на Win7».

---

## 1. .NET Framework 4.8 на Windows 7 SP1

| # | Утверждение | Статус |
|---|---|---|
| 1.1 | В таблице Client OS для **Windows 7 SP1** (32/64-bit) в колонке «Installable separately» указаны 4.6.2, 4.7, 4.7.1, 4.7.2, **4.8**. **4.8.1 не указан**. Сноска: Windows 7 «out-of-support». | подтверждено (https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements) |
| 1.2 | 4.8.1 указан как устанавливаемый только на Windows 10 20H2 и новее (20H2, 21H1, 21H2, 22H2), Windows 11 и Windows Server 2022; в Windows 11 22H2+ и Windows Server 2025 он предустановлен. Windows 7 в этом списке нет, то есть **4.8.1 на Win7 не поддерживается**. | подтверждено (тот же URL). Косвенно подтверждает заголовок KB5011048: «.NET Framework 4.8.1 for Windows 10 version 21H2, Windows 10 version 22H2, Windows 11 version 21H2, Windows Server 2022…» (https://support.microsoft.com/en-us/topic/microsoft-net-framework-4-8-1-for-windows-10-version-21h2-windows-10-version-22h2-windows-11-version-21h2-windows-server-2022-desktop-azure-editions-azure-stack-21h2-and-azure-stack-22h2-kb5011048-277f9c30-7add-4150-b774-5e3667e02256) |
| 1.3 | Официальный анонс 4.8 перечисляет Windows 7 SP1 среди поддерживаемых клиентских ОС. | подтверждено (https://devblogs.microsoft.com/dotnet/announcing-the-net-framework-4-8/) |
| 1.4 | Страница offline installer 4.8 (Applies to) включает Windows 7 SP1 и Windows Server 2008 R2 SP1. На Win7 продукт виден в «Programs and Features» как «Update for Microsoft .NET Framework 4.8 (**KB4503548**)». | подтверждено (https://support.microsoft.com/en-us/topic/microsoft-net-framework-4-8-offline-installer-for-windows-9d23f658-3b97-68ab-d013-aa3c3e7495e0) |
| 1.5 | Имя offline installer: **`NDP48-x86-x64-AllOS-ENU.exe`**. Цепочка ссылок: страница `https://dotnet.microsoft.com/en-us/download/dotnet-framework/thank-you/net48-offline-installer` → `https://go.microsoft.com/fwlink/?linkid=2088631` → `https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe`. Размер по HEAD: 121 346 568 байт, `Last-Modified: 2025-02-06`. | подтверждено (https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) + эмпирически (HEAD) |
| 1.6 | На Windows 7 .NET Framework 4.x требует **SP1**. Без SP1 установщик пишет «not supported on this operating system». | подтверждено (https://learn.microsoft.com/en-us/dotnet/framework/install/troubleshoot-blocked-installations-and-uninstallations) |
| 1.7 | **KB4019990 (D3DCompiler_47.dll)**. Для **4.7** это документированное блокирующее предусловие: WPF зависит от `%windir%\system32\D3DCompiler_47.dll`. | подтверждено (https://support.microsoft.com/en-us/topic/the-net-framework-4-7-installation-is-blocked-on-windows-7-windows-server-2008-r2-and-windows-server-2012-because-of-a-missing-d3dcompiler-update-0869046a-0972-7824-1bb8-5d89bf99e112 ; https://support.microsoft.com/en-us/help/4019990/update-for-the-d3dcompiler-47-dll-component-on-windows) |
| 1.8 | Роллапы .NET для Win7 (например, KB5020688, ноябрь 2022) пишут: «All updates for .NET Framework 4.7.2, 4.7.1, 4.7, 4.6.2, 4.6.1, and 4.6 require that the d3dcompiler_47.dll update is installed». **4.8 в этой фразе не упомянут.** | подтверждено (https://support.microsoft.com/en-us/topic/november-8-2022-security-and-quality-rollup-for-net-framework-3-5-1-4-6-2-4-7-4-7-1-4-7-2-4-8-for-windows-7-sp1-and-windows-server-2008-r2-sp1-kb5020688-5dff36fc-5033-47c2-919b-90edd25e2885) |
| 1.9 | Явного официального требования KB4019990 именно для 4.8 на Win7 не нашёл. **Вывод:** ставить KB4019990 заранее всё равно разумно — зависимость WPF от D3DCompiler_47 появилась в 4.7, а 4.8 является in-place-обновлением 4.x. | **не удалось проверить** (для 4.8) |
| 1.10 | Сообщения сообщества: на **чистой** Win7 SP1 без интернета offline installer 4.8 может не пройти проверку подписи, пока не установлен корневой сертификат Microsoft Root Certificate Authority 2011. Для обновлений .NET 4.x такой же сценарий описан в архивном блоге Microsoft (ошибка «A certificate chain could not be built to a trusted root authority»). | подтверждено (https://github.com/dotnet/docs/issues/22308), сообщество ; архивный блог MS: https://learn.microsoft.com/en-us/archive/blogs/vsnetsetup/a-certificate-chain-could-not-be-built-to-a-trusted-root-authority-2 . Для версии установщика от 2025-02 — **не удалось проверить** |
| 1.11 | Жизненный цикл Windows 7: Extended support закончился 2020-01-14 (1/15/2020 6:59:59 AM PT). ESU Year 3 закончился 2023-01-11 (1/11/2023 6:59:59 AM PT). | подтверждено (https://learn.microsoft.com/en-us/lifecycle/products/windows-7) |

---

## 2. TLS на Windows 7 SP1 для .NET Framework 4.8 (HttpClient / HttpWebRequest)

### 2.1 Поддержка протоколов в SChannel (Windows 7 / Windows Server 2008 R2)

| Протокол | Client | Server | Статус |
|---|---|---|---|
| TLS 1.0 | Enabled | Enabled | подтверждено |
| TLS 1.1 | Disabled | Disabled | подтверждено |
| TLS 1.2 | **Disabled** | Disabled | подтверждено |
| TLS 1.3 | **Not supported** | Not supported | подтверждено |

Источник: https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-
На той же странице: «TLS 1.3 is supported starting in Windows 11 and Windows Server 2022. Enabling TLS 1.3 on earlier versions of Windows is not a safe system configuration.»

- Прежняя редакция .NET-документации (коммит от 2024-01, репозиторий dotnet/docs) описывала TLS 1.2 на Windows 7 SP1 как «Supported, but not enabled by default». Там же сказано, что при `SystemDefault` «Windows 7 SP1 uses TLS 1.0 while Windows 8 and Windows 10 use TLS 1.2». — **подтверждено** (https://github.com/dotnet/docs/blob/3ff4927b96813d031bf03620551e85ed9721d2d5/docs/framework/network-programming/tls.md).
- Смысл состояния «Disabled by default» из официальной документации Windows Server (редакция 2021 года): «Disabled by default: unless the SSPI caller explicitly requests this protocol version using the deprecated SCHANNEL_CRED structure, Schannel SSP will not negotiate this protocol version». Значит, если приложение **явно** запросит TLS 1.2 (как делает .NET при `SecurityProtocolType.Tls12`), протокол согласуется и без правки реестра. — **подтверждено** (https://github.com/MicrosoftDocs/windowsserverdocs/blob/75eddbd58bb1a6961e0f8cb87eb8c710190ccec7/WindowsServerDocs/security/tls/tls-registry-settings.md).
  Текущая редакция страницы (https://learn.microsoft.com/en-us/windows-server/security/tls/tls-registry-settings) этот текст уже не содержит.

### 2.2 Поведение .NET Framework 4.8

- .NET Framework опирается на SChannel, поэтому версии TLS определяет ОС. Рекомендация Microsoft — не задавать версию явно: при `ServicePointManager.SecurityProtocol` = `SecurityProtocolType.SystemDefault` протокол выбирает ОС. Для приложений, нацеленных на 4.7+, это значение по умолчанию. Для `HttpClient` и `HttpWebRequest` действует `ServicePointManager`. — **подтверждено** (https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls).
- Реестровые ключи .NET, `HKLM\SOFTWARE\[Wow6432Node\]Microsoft\.NETFramework\v4.0.30319`:
  - `SchUseStrongCrypto` (DWORD). Для приложений, нацеленных на 4.6+, по умолчанию равен 1.
  - `SystemDefaultTlsVersions` (DWORD). Для приложений, нацеленных на 4.7+, по умолчанию равен 1.
  - По умолчанию этих ключей в реестре нет.
  - Эквивалентные AppContext-переключатели: `Switch.System.Net.DontEnableSchUseStrongCrypto` и `Switch.System.Net.DontEnableSystemDefaultTlsVersions`, для таргета 4.7+ по умолчанию `false`.
  - — **подтверждено** (тот же URL).
- Если явно указать `Tls11` или `Tls12`, .NET передаёт в SChannel флаг `SCH_USE_STRONG_CRYPTO` (только для клиентских соединений). — **подтверждено** (тот же URL).
- **Вывод для Win7.** С `SystemDefault` и без правки реестра SChannel клиент на Win7 предложит только TLS 1.0. Современные API TLS 1.0 не принимают. Варианты:
  - **(A)** включить TLS 1.2 client в SChannel (см. 2.3);
  - **(B)** на Win7 явно задавать `ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12` (при необходимости `| Tls11`).

  Вариант (A) действует на всю машину и нужен в любом случае для `System.Data.SqlClient` (см. раздел 3). Вариант (B) лучше включать только на Win7: на Windows 11 явное указание Tls12 лишает приложение TLS 1.3 (так пишет Microsoft в той же статье).

### 2.3 Реестр SChannel для Windows 7 (официальная формулировка)

Официальная статья о TLS 1.2 для SQL Server приводит такие ключи:

```
[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client]
"DisabledByDefault"=dword:00000000
"Enabled"=dword:00000001
```

Цитата оттуда: «The `DisabledByDefault` and `Enabled` settings are required to be created on Windows 7 clients and Windows Server 2008 R2 servers.» — **подтверждено** (https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/connect/tls-1-2-support-microsoft-sql-server).

- KB3140245 касается только **WinHTTP**: он добавляет `DefaultSecureProtocols`. На той же странице для Win7 приведены ключи SChannel `TLS 1.1\Client` и `TLS 1.2\Client` с `DisabledByDefault=0`. На .NET `HttpWebRequest`/`HttpClient` сам KB3140245 не влияет: они работают через SChannel/SSPI, а не через WinHTTP. — **подтверждено** (https://support.microsoft.com/topic/update-to-enable-tls-1-1-and-tls-1-2-as-default-secure-protocols-in-winhttp-in-windows-c4bd73d2-31d7-761e-0178-11268bb10392).
- Статья Configuration Manager описывает те же три шага: обновить WinHTTP (KB3140245), включить TLS 1.2 в SChannel, выставить ключи .NET `SystemDefaultTlsVersions=1` и `SchUseStrongCrypto=1`, в том числе в `Wow6432Node`. — **подтверждено** (https://learn.microsoft.com/en-us/intune/configmgr/core/plan-design/security/enable-tls-1-2-client).

### 2.4 TLS 1.3 на Windows 7

- **Не поддерживается** (см. 2.1). — **подтверждено**.

### 2.5 Шифронаборы Windows 7 и KB2992611

- Официальный список Windows 7 (после обновления **KB3042058**) по умолчанию включает (TLS 1.2):
  - `TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384_P256/P384`, `TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256_P256/P384`, `TLS_ECDHE_RSA_WITH_AES_*_CBC_SHA_P256/P384`;
  - `TLS_DHE_RSA_WITH_AES_256/128_GCM_*`, `TLS_RSA_WITH_AES_256/128_GCM_*`, `TLS_RSA_WITH_AES_*_CBC_*`;
  - `TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384_P384`, `TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256_P256/P384`, а также CBC-варианты ECDSA.

  Кривые: P-256 и P-384 (P-521 по умолчанию выключена). **`TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256` и `TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384` в списке нет вообще**, ни во включённых, ни в выключенных. — **подтверждено** (https://learn.microsoft.com/en-us/windows/win32/secauthn/tls-cipher-suites-in-windows-7).
- **KB2992611 (MS14-066, ноябрь 2014)** добавил `TLS_DHE_RSA_WITH_AES_256_GCM_SHA384`, `TLS_DHE_RSA_WITH_AES_128_GCM_SHA256`, `TLS_RSA_WITH_AES_256_GCM_SHA384` и `TLS_RSA_WITH_AES_128_GCM_SHA256`. Затем 2014-11-18 вторичный пакет **KB3018238** убрал их из списка приоритетов по умолчанию. — **подтверждено** (https://support.microsoft.com/en-us/help/2992611/ms14-066-vulnerability-in-schannel-could-allow-remote-code-execution-n).
- **KB3042058** (Advisory 3042058, 2015-05-12; с 2015-10-13 доступен через MU/WSUS) снова добавил эти четыре набора в список по умолчанию для Windows 7 SP1 и Windows Server 2008 R2 SP1. **ECDHE_RSA + AES-GCM он не добавляет.** — **подтверждено** (https://learn.microsoft.com/en-us/security-updates/SecurityAdvisories/2015/3042058).
- Пересечение с сервером **Windows Server 2022/2025** (там стоит SQL Server 2025) при настройках по умолчанию существует: `TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384`, `..._128_CBC_SHA256`, `..._CBC_SHA`, `TLS_RSA_WITH_AES_*_GCM/CBC`. `TLS_DHE_RSA_WITH_AES_*_GCM` в Windows Server 2022 23H2+ и в 2025 **выключены по умолчанию**. — **подтверждено** (https://learn.microsoft.com/en-us/windows/win32/secauthn/tls-cipher-suites-in-windows-server-2022 ; https://learn.microsoft.com/en-us/windows/win32/secauthn/tls-cipher-suites-in-windows-server-2025).
  **Вывод:** если администратор сервера уберёт CBC-наборы и наборы с RSA key exchange (например, профилем «только AEAD/ECDHE»), Win7-клиент подключиться не сможет.

### 2.6 Эмпирическая проверка LLM API с набором шифров Windows 7 (2026-09-24, ~19:00 UTC)

Метод:
- Инструмент: `openssl s_client` 3.5.5 через локальный HTTP-прокси машины исследования.
- Параметры: `-tls1_2`; `-cipher` — только наборы из списка Windows 7 (ECDHE-RSA-AES-CBC-SHA/SHA256/SHA384, DHE-RSA-AES-GCM/CBC, RSA-AES-GCM/CBC, ECDHE-ECDSA-AES-GCM/CBC, 3DES); `-groups P-256:P-384`; `-sigalgs` RSA/ECDSA + SHA1/256/384.
- Каждый хост проверен 3 раза, результаты стабильны.

Ограничение: это эмуляция клиента Windows 7 в OpenSSL, а не реальный SChannel. Результат может отличаться между узлами CDN и со временем.

| Хост | Результат | Согласованный набор |
|---|---|---|
| api.openai.com | OK | ECDHE-ECDSA-AES128-GCM-SHA256 |
| api.anthropic.com | OK | ECDHE-ECDSA-AES128-GCM-SHA256 |
| generativelanguage.googleapis.com | OK | ECDHE-ECDSA-AES256-GCM-SHA384 |
| openrouter.ai | OK | ECDHE-ECDSA-AES128-GCM-SHA256 |
| api.mistral.ai | OK | ECDHE-ECDSA-AES128-GCM-SHA256 |
| api.groq.com | OK | ECDHE-ECDSA-AES128-GCM-SHA256 |
| llm.api.cloud.yandex.net | OK | ECDHE-RSA-AES128-SHA (принимает также RSA-AES-GCM и RSA-AES-CBC) |
| **api.deepseek.com** | **FAIL** (сервер закрывает соединение) | без ограничений согласуется `ECDHE-RSA-AES128-GCM-SHA256`, а в Windows 7 такого набора нет |

Статус — **эмпирически**.
**Вывод:** провайдеры за Cloudflare и Google отдают ECDSA-сертификат и принимают ECDHE-ECDSA-AES-GCM, который в Windows 7 есть. Провайдеры только с RSA-сертификатом и политикой «ECDHE-RSA-GCM only» (так сейчас у DeepSeek на AWS) с Win7 через SChannel недоступны.

### 2.7 Доверенные корневые сертификаты

- Цепочки перечисленных API ведут к GlobalSign Root CA (через кросс-подпись GTS Root R1/R4), к Amazon Root CA 1 / Starfield, к GlobalSign Root R3. — **эмпирически** (вывод `openssl s_client`).
- Список доверенных корней Microsoft (`authrootstl.cab`) продолжает публиковаться: HEAD к `http://ctldl.windowsupdate.com/msdownload/update/v3/static/trustedr/en/authrootstl.cab` вернул `Last-Modified: 2026-08-27`. — **эмпирически**.
- Корректно ли Win7 SP1 сейчас обрабатывает этот CTL, и есть ли на целевых машинах нужные корни без доступа в интернет — **не удалось проверить**. Проверять нужно на реальной машине.

### 2.8 Альтернатива для HTTPS-вызовов — через Python-worker

- Windows-сборка Python 3.8.10 содержит собственный **OpenSSL 1.1.1k** (bpo-43745: «Actually updates Windows release to OpenSSL 1.1.1k»). — **подтверждено** (https://docs.python.org/release/3.8.10/whatsnew/changelog.html).
- **Вывод:** HTTPS через Python (модуль `ssl`, OpenSSL) не зависит от SChannel и поддерживает TLS 1.3 и ECDHE-RSA-AES-GCM даже на Win7. Минус: OpenSSL 1.1.1 давно не сопровождается upstream, а доверие к корням всё равно берётся из системного хранилища или из собственного CA-bundle.

---

## 3. System.Data.SqlClient и Microsoft.Data.SqlClient; SQL Server 2025 и шифрование

### 3.1 Microsoft.Data.SqlClient (NuGet): поддерживаемые ОС

- Формулировка Microsoft: «Microsoft.Data.SqlClient supports: .NET Framework applications on operating systems supported by .NET Framework 4.6.2 or later.» Для 7.0 сказано: «.NET Framework 4.6.2 or later — Supported Windows versions for the selected .NET Framework version». — **подтверждено** (https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-driver-support-lifecycle?view=sql-server-ver17).
- Windows 7 в требованиях .NET Framework помечена как **out-of-support** (см. 1.1). **Вывод:** ни одна актуальная линия Microsoft.Data.SqlClient **официально не поддерживает Windows 7**.
  - Поддерживаемые сейчас линии: **7.0** (STS, 7.0.3 от 2026-09-10) и **6.1** (LTS до 2028-08-14, 6.1.7).
  - Линии 5.1 и 6.0 уже вне поддержки.
  - Явного упоминания «Windows 7» в release notes не нашёл: я просканировал 189 файлов `release-notes/**/*.md` репозитория dotnet/SqlClient. — **подтверждено** (lifecycle URL) / явный статус Win7 — **не удалось проверить**.
- Обе линии, 7.0 и 6.1, поддерживают **SQL Server 2025**. — **подтверждено** (тот же URL).
- GitHub issues о падении `Microsoft.Data.SqlClient.SNI` именно на Windows 7 **не нашёл**. Поиск по dotnet/SqlClient: «Windows 7», «Win7», «6.1.7601», «api-ms-win», «vcruntime140».
  - Issue #645 — это проблема загрузки `SNI.x86.dll` в F#-скрипте на машине с Win7; с ОС она не связана (https://github.com/dotnet/SqlClient/issues/645).
  - — **не удалось проверить** (специфичных для Win7 поломок не найдено).
- Зависимость нативного SNI от VC++ runtime — сведения противоречат друг другу:
  - в 1.0 SNI требовал VC++ 2015 Redistributable (https://github.com/dotnet/SqlClient/issues/211#issuecomment-541955056);
  - мейнтейнер в 2020 году писал, что упоминание зависимости убрано начиная с 1.1.1 (https://github.com/dotnet/SqlClient/pull/570#issuecomment-656864669);
  - мейнтейнер в 2026 году пишет, что `Microsoft.Data.SqlClient.SNI.dll` зависит от `VCRUNTIME140.dll`/`MSVCP140.dll` (https://github.com/dotnet/SqlClient/issues/3148#issuecomment-4508355938).

  — сообщество/мейнтейнеры; актуальный статус **не удалось проверить** без анализа таблицы импорта DLL.

### 3.2 System.Data.SqlClient (встроен в .NET Framework 4.8)

- Сравнение из официальной таблицы миграции:

  | | `System.Data.SqlClient` | `Microsoft.Data.SqlClient` |
  |---|---|---|
  | Strict encryption | «Not supported» | `Encrypt=Strict` начиная с 5.0 (для серверов с TDS 8.0) |
  | `Encrypt` по умолчанию | `false` | `true` начиная с 4.0 |

  — **подтверждено** (https://learn.microsoft.com/en-us/sql/connect/ado-net/migrate-system-data-sql-client-to-microsoft-data-sql-client?view=sql-server-ver17).
- Страница TDS 8.0 называет минимальные драйверы для `strict`: «Microsoft ADO.NET … version 5.1 or higher», ODBC 18.1.2.1+, OLE DB 19.2.0+, JDBC 11.2.0+, PHP 5.10+, `mssql-python`. `System.Data.SqlClient` в списке нет. — **подтверждено** (https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/tds-8?view=sql-server-ver17).
- ADO.NET (то есть `System.Data.SqlClient` в .NET Framework) поддерживает TLS 1.2 начиная с .NET Framework 4.6. — **подтверждено** (https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/connect/tls-1-2-support-microsoft-sql-server).
- Пакет System.Data.SqlClient на NuGet объявлен deprecated. Поддержка пользователей .NET Framework 4.6.2+ сохраняется, security servicing идёт через сам .NET Framework. — **подтверждено** (https://techcommunity.microsoft.com/blog/sqlserver/announcement-system-data-sqlclient-package-is-now-deprecated/4227205).

### 3.3 SQL Server 2025: шифрование, TLS 1.3, TDS 8.0

- Совместимость TDS 7.x, TDS 8.0 и TLS:
  - «TDS 7.x supports encryption using TLS up to version 1.2. TDS 8.0 requires encryption … (Encrypt=Strict)… supports TLS 1.3.»
  - По матрице совместимости `Encrypt=Mandatory` или `Optional` при включённом TLS 1.2 дают **Success** (согласуется TLS 1.2, TDS 8.0 не используется).
  - `Optional`/`Mandatory` при сервере **только** с TLS 1.3 дают **Failure**.

  — **подтверждено** (https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/tds-8?view=sql-server-ver17).
- «Even with TLS 1.3 support for TDS connections, TLS 1.2 is still required for starting up SQL Server satellite services. Don't disable TLS 1.2 on the machine.» Кроме того, установка SQL Server 2025 падает, если на ОС включён только TLS 1.3. Серверная поддержка TLS 1.3 есть на Windows 11 и Windows Server 2022. — **подтверждено** (https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/tls-1-3?view=sql-server-ver17).
- «Secure by default» в SQL Server 2025 касается **серверных компонентов**: Agent, linked servers, репликация, log shipping, AG/FCI, PolyBase, VSS Writer. Они используют OLE DB 19 / ODBC 18, как правило с `Encrypt=Mandatory`. Исключение — Database Mail: по умолчанию `Encrypt=Optional` и `TrustServerCertificate=Yes`. Про клиентские подключения приложений (TDS 7.x) в этом разделе ограничений нет. — **подтверждено** (тот же TLS 1.3 URL; https://learn.microsoft.com/en-us/sql/database-engine/breaking-changes-to-database-engine-features-in-sql-server-2025?view=sql-server-ver17).
- **Force Encryption / Force Strict Encryption** включаются вручную в SQL Server Configuration Manager (вкладка Flags, значение Yes; Force Strict — начиная с 2022). В документации это описано как необязательная настройка («only required if you want to force encrypted communications for all the clients»). — **подтверждено** (https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/configure-sql-server-encryption?view=sql-server-ver17 ; https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/connect-with-strict-encryption?view=sql-server-ver17).
  - Явной фразы «по умолчанию = No» для SQL Server 2025 не нашёл — **не удалось проверить**.
  - Ответ модератора на Microsoft Q&A утверждает обратное: «By default, SQL Server 2025 with 'Force Strict Encryption = Yes'…». Это не документация и противоречит описанию процедуры в docs (https://learn.microsoft.com/en-us/answers/questions/5793397/confirming-net-4-8-compatibility-with-sql-server-2). Настройку нужно проверить на реальном сервере.
- Даже при `Encrypt=false` логин-пакет шифруется всегда. Если доверенного сертификата нет, SQL Server генерирует самоподписанный. — **подтверждено** (configure-sql-server-encryption URL).
  **Вывод:** TLS-рукопожатие с Win7-клиентом происходит в любом случае, поэтому TLS 1.2 на Win7 нужен даже без шифрования данных.
- **Вывод по Win7 + .NET 4.8.** С `System.Data.SqlClient` (TDS 7.4) к SQL Server 2025 подключиться можно при трёх условиях:
  1. на Win7 включён TLS 1.2 client (2.3);
  2. на сервере **не** включён Force Strict Encryption и TLS 1.2 не отключён;
  3. у сервера и Win7 есть общий шифронабор (2.5).

  Если сертификат сервера не доверен на Win7 и `Encrypt=true`, нужен `TrustServerCertificate=true` или развёртывание CA на клиентах.

---

## 4. SQL Server 2025: поддерживаемые ОС, редакции, Full-Text Search

- Требования к ОС: «Windows 10 or greater; Windows Server 2019 or greater». Только x64. Нужен .NET Framework 4.7.2. Enterprise не ставится на клиентские Windows 10/11. **Windows 7 не поддерживается.** — **подтверждено** (https://learn.microsoft.com/en-us/sql/sql-server/install/hardware-and-software-requirements-for-installing-sql-server-2025?view=sql-server-ver17).
- Setup устанавливает ODBC Driver 17 и 18 и OLE DB Driver 18 и 19. — **подтверждено** (тот же URL).
- Редакции: Enterprise, Standard, Enterprise Developer, Standard Developer, Evaluation, Express.
  - «Starting with SQL Server 2025 (17.x), Express edition includes all the functionality that was available in SQL Server Express edition with Advanced Services.»
  - «Full-text and semantic search» есть во всех редакциях: Enterprise, Standard, **Express**.
  - Лимиты Express: БД до **50 GB**, меньшее из 1 сокета / 4 ядер, buffer pool 1410 MB.
  - Web edition упразднена.

  — **подтверждено** (https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025?view=sql-server-ver17 ; https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025?view=sql-server-ver17).
- Full-Text Search — необязательный компонент Database Engine. Его нужно выбрать при установке, иначе потом придётся снова запускать Setup. — **подтверждено** (https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-search?view=sql-server-ver17).
- **Изменения FTS в 2025** — **подтверждено** (https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-index-version-upgrade?view=sql-server-ver17 ; https://learn.microsoft.com/en-us/sql/database-engine/breaking-changes-to-database-engine-features-in-sql-server-2025?view=sql-server-ver17):
  - Прежние word breaker, stemmer и filter удалены. Новые компоненты собраны современным toolset и называются **version 2**; компоненты 2022 и более ранних версий — **version 1**.
  - После in-place upgrade индексы с `index_version = 1` перестают работать: запросы дают Msg 30010, при population фильтры не находятся. Индексы нужно перестроить (`ALTER FULLTEXT CATALOG … REBUILD`) либо оставить version 1 через `ALTER DATABASE SCOPED CONFIGURATION SET FULLTEXT_INDEX_VERSION = 1` и вручную скопировать старые DLL в `Binn` (скрипт есть в документации).
  - Новые языки: финский (1035), венгерский (1038), эстонский (1061).
  - Новые фильтры по умолчанию: `.msg`, `.odp/.ods/.odt`, форматы Office (`.doc…/.xlsx`), `.one`, `.zip`.
  - Настройка version 2 больше не читается из реестра: используется JSON-файл экземпляра.
  - Токенизация может отличаться. Пример для английского: `cat_dog` → `cat_dog`, `cat`, `dog`.
- **Русский (LCID 1049) в version 2:** word breaker `MSWB7.dll` + `prm0019.bin`, word breaker CLSID `AAA3D3BD-6DE7-4317-91A0-D25E7D3BABC3`, stemmer CLSID `D42C8B70-ADEB-4B81-A52F-C09F24F77DFA`. В version 1 было `MsWb7.dll` + `Prm0019.bin`. — **подтверждено** (https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-word-breaker-and-stemmer-binaries?view=sql-server-ver17).

---

## 5. Python 3.8 на Windows 7

- python.org, страница Windows downloads: у всех 3.9.x и новее — «Note that Python 3.x.y cannot be used on Windows 7 or earlier», у 3.8.x — «…cannot be used on Windows XP or earlier». — **подтверждено** (https://www.python.org/downloads/windows/).
- Документация 3.9: «This means that Python 3.9 supports Windows 8.1 and newer. If you require Windows 7 support, please install Python 3.8.» — **подтверждено** (https://docs.python.org/3.9/using/windows.html).
- PEP 569: «3.8.10 final: Monday, 2021-05-03 (Final regular bugfix release with binary installers)». Релизы 3.8.11–3.8.20 выходили только в исходниках. Последний — 3.8.20 (2024-09-06). «As of 2024-10-07, 3.8 has reached the end-of-life». — **подтверждено** (https://peps.python.org/pep-0569/). На странице Windows downloads бинарники есть только у 3.8.0–3.8.10 (https://www.python.org/downloads/windows/).
- Страница релиза 3.8.10: «Python 3.8.10 reached end-of-life on 2024-10-07… superseded by Python 3.8.20». В списке файлов есть «Windows embeddable package (64-bit)». — **подтверждено** (https://www.python.org/downloads/release/python-3810/).
- **Предусловия для Win7** (документация 3.8, раздел «The embeddable package»):
  - «When running on Windows 7, Python 3.8 requires the KB2533623 update to be installed. The embeddable distribution does not detect this update, and may fail at runtime.»
  - «The embedded distribution does not include the Microsoft C Runtime… can be detected by finding `ucrtbase.dll` in the system directory.»

  — **подтверждено** (https://docs.python.org/3.8/using/windows.html).
- KB2533623 добавляет `SetDefaultDllDirectories`/`AddDllDirectory`/`RemoveDllDirectory` для Vista, 2008, 7 и 2008 R2. — **подтверждено** (https://support.microsoft.com/en-us/topic/microsoft-security-advisory-insecure-library-loading-could-allow-remote-code-execution-486ea436-2d47-27e5-6cb9-26ab7230c704).
  - Сотрудник Microsoft в dotnet/docs пишет, что старые пакеты сняты с загрузки, потому что были подписаны SHA-1.
  - Как рабочую замену сообщество и тот же сотрудник называют **KB3063858**.
  - — **подтверждено** (https://github.com/dotnet/docs/issues/20459#issuecomment-754952044), сообщество. Официальной страницы о том, что KB3063858 заменяет KB2533623, **не нашёл**.
- **Universal CRT** для Win7:
  - Первый выпуск — **KB2999226**, исправленное обновление — **KB3118401**. Для Vista–8.1 последняя доступная UCRT основана на 10.0.14393.
  - Redistributable ставится только на Windows 7 **SP1**.
  - Локальное развёртывание UCRT (`ucrtbase.dll` + `api-ms-win-*.dll`) поддерживается. До Windows 8 файлы должны лежать в каталоге главного exe.

  — **подтверждено** (https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment?view=msvc-170 ; https://support.microsoft.com/en-us/kb/3118401).
- Состав embeddable-архива. Скрипт раскладки CPython v3.8.10 (`PC/layout/main.py`) для всех раскладок, включая `embed`, копирует `vcruntime*.dll`. UCRT в архив не попадает. — **подтверждено** (https://github.com/python/cpython/blob/v3.8.10/PC/layout/main.py). Фактический список файлов в zip — **не удалось проверить** (архив не скачивал).
- В embeddable для `import site` / `site-packages` нужно править файл `python38._pth` (строка `import site` и пути). — **подтверждено** (https://docs.python.org/3.8/using/windows.html).

---

## 6. pandas / numpy и зависимости: версии, колёса, SHA256

Все хэши взяты из PyPI JSON API (`https://pypi.org/pypi/<project>/<version>/json`) и перепроверены через PyPI Simple API (PEP 691). Значения совпали.

### 6.1 Последние версии с поддержкой Python 3.8

| Пакет | Последняя версия для 3.8 | Первая без 3.8 | Доказательство |
|---|---|---|---|
| numpy | **1.24.4** (`requires_python >=3.8`) | 1.25.0 (`>=3.9`, cp38-колёс 0) | PyPI JSON; «The Python versions supported by this release are 3.8-3.11» (https://numpy.org/doc/stable/release/1.24.4-notes.html); «supported in this release are 3.9-3.11» (https://numpy.org/doc/stable/release/1.25.0-notes.html) — **подтверждено** |
| pandas | **2.0.3** (`requires_python >=3.8`) | 2.1.0 (`>=3.9`, cp38-колёс 0) | PyPI JSON; «pandas 2.1.0 supports Python 3.9 and higher» (https://pandas.pydata.org/docs/whatsnew/v2.1.0.html); «Officially Python 3.8, 3.9, 3.10 and 3.11» (https://pandas.pydata.org/pandas-docs/version/2.0.3/getting_started/install.html) — **подтверждено** |

### 6.2 Основной вариант: pandas 2.0.3 + numpy 1.24.4 (win_amd64, cp38)

| Файл | SHA256 | Размер, байт | Загружен |
|---|---|---|---|
| `numpy-1.24.4-cp38-cp38-win_amd64.whl` | `692f2e0f55794943c5bfff12b3f56f99af76f902fc47487bdfe97856de51a706` | 14868112 | 2023-06-26 |
| `pandas-2.0.3-cp38-cp38-win_amd64.whl` | `69d7f3884c95da3a31ef82b7618af5710dba95bb885ffab339aad925c3e8ce78` | 10777638 | 2023-06-28 |

Источники: https://pypi.org/pypi/numpy/1.24.4/json , https://pypi.org/pypi/pandas/2.0.3/json — **подтверждено**.

`requires_dist` у pandas 2.0.3 (PyPI):
- `python-dateutil (>=2.8.2)`
- `pytz (>=2020.1)`
- `tzdata (>=2022.1)`
- `numpy (>=1.20.3) ; python_version < "3.10"`

— **подтверждено**. Замечание: в таблице «Required dependencies» документации 2.0.3 `tzdata` не указан. При этом whatsnew 2.0.0 помечает `tzdata 2022.1` как Required (https://pandas.pydata.org/docs/whatsnew/v2.0.0.html), и в метаданных колеса он есть.

### 6.3 Зависимости: последние версии, совместимые с Python 3.8

У всех перечисленных ниже `requires_python` допускает 3.8. — **подтверждено** (PyPI JSON).

| Файл | SHA256 | Размер | Загружен | Примечание |
|---|---|---|---|---|
| `python_dateutil-2.9.0.post0-py2.py3-none-any.whl` | `a8b2bc7bffae282281c8140a97d3aa9c14da0b136dfe83f850eea9a5f7470427` | 229892 | 2024-03-01 | последняя; зависит от `six >=1.5` |
| `six-1.17.0-py2.py3-none-any.whl` | `4721f391ed90541fddacab5acf947aa0d3dc7d27b2e1e8eda2be8970586c3274` | 11050 | 2024-12-04 | последняя; нужна python-dateutil |
| `pytz-2026.4-py2.py3-none-any.whl` | `9d514388fbc89ca0833203464272ac485b8828568ab73532f5020f17e892a0ff` | 506747 | 2026-09-24 | последняя; вышла в день исследования |
| `pytz-2026.3.post1-py2.py3-none-any.whl` | `dd95840dd199baea12d9cc096a1d452caa6596a1c1e4b5f3dbd1541855d5e815` | 508283 | 2026-07-25 | запасной вариант |
| `tzdata-2026.4-py2.py3-none-any.whl` | `c2169a8b0a7a5e9674da5a135ccdfb2b3e671b333ed9fed17b41f73c34476e81` | 347494 | 2026-09-12 | последняя (`requires_python >=2`) |
| `tzdata-2026.3-py2.py3-none-any.whl` | `dc096730c87af6cab1b171c9d532be840741ff5d459015e7f6947bd7d7e54931` | 348168 | 2026-07-10 | запасной вариант |
| `defusedxml-0.7.1-py2.py3-none-any.whl` | `a352e7e428770286cc899e2542b6cdaedb2b4953ff269a210103ec58f6198a61` | 25604 | 2021-03-08 | pure-python; `requires_python >=2.7, !=3.0.*…!=3.4.*` |

Источники: https://pypi.org/pypi/python-dateutil/2.9.0.post0/json , https://pypi.org/pypi/six/1.17.0/json , https://pypi.org/pypi/pytz/2026.4/json , https://pypi.org/pypi/pytz/2026.3.post1/json , https://pypi.org/pypi/tzdata/2026.4/json , https://pypi.org/pypi/tzdata/2026.3/json , https://pypi.org/pypi/defusedxml/0.7.1/json

### 6.4 Консервативный вариант: pandas 1.5.3 + numpy 1.23.5

| Файл | SHA256 | Размер | Загружен |
|---|---|---|---|
| `numpy-1.23.5-cp38-cp38-win_amd64.whl` | `ca51fcfcc5f9354c45f400059e88bc09215fb71a48d3768fb80e357f3b457e1e` | 14672878 | 2022-11-20 |
| `pandas-1.5.3-cp38-cp38-win_amd64.whl` | `41179ce559943d83a9b4bbacb736b04c928b095b5f25dd2b7389eda08f46f373` | 10959031 | 2023-01-19 |

- `requires_dist` pandas 1.5.3: `python-dateutil (>=2.8.1)`, `pytz (>=2020.1)`, `numpy (>=1.20.3) ; python_version < "3.10"`. **`tzdata` не требуется.** — **подтверждено** (https://pypi.org/pypi/pandas/1.5.3/json , https://pypi.org/pypi/numpy/1.23.5/json).
- numpy 1.23.5: «The Python versions supported for this release are 3.8-3.11». — **подтверждено** (https://numpy.org/doc/stable/release/1.23.5-notes.html).
- **Гибрид pandas 2.0.3 + numpy 1.23.5** по метаданным совместим (numpy ≥ 1.20.3). **Вывод:** этот вариант снимает риск numpy 1.24.x из раздела 7, но **не тестировался**.

---

## 7. Известные поломки numpy/pandas на Windows 7 и зависимости от runtime

### 7.1 numpy

- **numpy 1.24.x падает при `import numpy` на Windows 7 с 32-битным Python 3.8.** — сообщество.
  - Сбой: `APPCRASH`, `Fault Module Name: _multiarray_umath.cp38-win32.pyd`, `Exception Code: c000001d` (illegal instruction), `OS Version: 6.1.7601`.
  - По сообщению: «1.24.0 through 1.24.5 all instantly crash on import. 1.23.x work flawlessly».
  - Тот же пользователь: «Other arrangements, 64bit python … All work correctly. It's only 32 bit, python 3.8, windows 7».
  - Мейнтейнер (rgommers): «the workaround is to use numpy 1.23.5 and avoid 1.24.x».
  - Мейнтейнер (mattip, 2025): Windows 7 «is not a supported platform… Use v1.23.5 or earlier».

  Замечание: версии 1.24.5 на PyPI нет, последняя — 1.24.4. Источники: https://github.com/numpy/numpy/issues/24832 , https://github.com/numpy/numpy/issues/23324 («numpy v1.23.5 latest version that works on windows 7», Python 3.8.10 32-bit), https://github.com/numpy/numpy/issues/23731 (1.24.3, Win7 32-bit), https://github.com/numpy/numpy/issues/23074 (1.24.1, Win7 32-bit, старый CPU Westmere).
- Для pandas 2.0.3 на **embedded Python 3.8 win32** сообщение аналогичное: `import numpy` и `import pandas` падают. Лечится установкой numpy 1.23.5. — сообщество (https://github.com/pandas-dev/pandas/issues/57963).
- Про **64-битный** Python на Win7 с numpy 1.24.x отдельных жалоб не нашёл. Есть только косвенное «64bit python … work correctly» из #24832. Официального подтверждения работоспособности нет — **не удалось проверить** (нужен тест на целевой машине).
- Версия OpenBLAS, которую подтягивает сборка колёс: numpy v1.24.4 → **0.3.21**, v1.23.5 → **0.3.20** (`OPENBLAS_V` в `tools/openblas_support.py`). — **подтверждено** (https://github.com/numpy/numpy/blob/v1.24.4/tools/openblas_support.py , https://github.com/numpy/numpy/blob/v1.23.5/tools/openblas_support.py).
- **GetSystemTimePreciseAsFileTime / Win8+ API в OpenBLAS или numpy** — поиск по issues numpy/numpy и OpenMathLib/OpenBLAS дал 0 результатов. — **не удалось проверить** (свидетельств не найдено).
- Шаблон лицензии Windows-колёс numpy (v1.24.4 и v1.23.5) перечисляет `extra-dll\msvcp140.dll` (Microsoft Visual C++ Runtime Files). Лежит ли `msvcp140.dll` в конкретном колесе 1.24.4, **не удалось проверить** (колесо не скачивал). — https://github.com/numpy/numpy/blob/v1.24.4/tools/wheels/LICENSE_win32.txt

### 7.2 pandas: C++ runtime

- История: pandas 1.0.2/1.0.3 падали с «DLL load failed while importing aggregations». Причина — отсутствие `msvcp140.dll` и `concrt140.dll`, а при сборке в VS2019 ещё и `vcruntime140_1.dll`. Среди затронутых был Win7 x64 с Python 3.8.2. — сообщество/мейнтейнеры (https://github.com/pandas-dev/pandas/issues/32936 , https://github.com/pandas-dev/pandas/issues/32857).
- Для **pandas 2.0.3** скрипт сборки `ci/fix_wheels.py` вкладывает в колесо `msvcp140.dll`, `concrt140.dll` и (для x64) `vcruntime140_1.dll` из VS2019 redist `14.29.30133`, кладёт их в `pandas/_libs/window`. — **подтверждено** (https://github.com/pandas-dev/pandas/blob/v2.0.3/ci/fix_wheels.py). Как упакован 1.5.3 (сборка шла в отдельном репозитории колёс) — **не удалось проверить**.

### 7.3 VC++ Redistributable на Win7

- «The latest version of the Visual C++ v14 Redistributable included with Visual Studio 2026 supports only … Windows 10 and 11; Windows Server 2016, 2019, 2022, and 2025.» — **подтверждено** (https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170).
- **Вывод:** на Win7 **не** ставить «последний» `vc_redist`. Рассчитывать на DLL, которые идут в комплекте с Python и pandas, и на UCRT из KB2999226/KB3118401. Если отдельный VC++ runtime всё же нужен, брать старую версию 2015–2019 и тестировать.

---

## 8. Python 3.8.10 embeddable amd64 zip

| Параметр | Значение | Статус |
|---|---|---|
| URL | `https://www.python.org/ftp/python/3.8.10/python-3.8.10-embed-amd64.zip` | подтверждено (https://www.python.org/downloads/release/python-3810/) |
| MD5 (опубликован python.org) | **`3acb1d7d9bde5a79f840167b166bb633`** | подтверждено: страница релиза (колонка «MD5 checksum») и API https://www.python.org/api/v2/downloads/release_file/?release=617 (`md5_sum`) |
| Размер | 8 211 403 байт (API `filesize`; HEAD `Content-Length` совпадает; на странице «7.8 MB») | подтверждено + эмпирически (HEAD 200, `Last-Modified: Mon, 03 May 2021 12:06:59 GMT`) |
| GPG-подпись | `https://www.python.org/ftp/python/3.8.10/python-3.8.10-embed-amd64.zip.asc` | подтверждено (страница релиза) |
| SHA256 | python.org **не публикует** (в API поле `sha256_sum` пустое, Sigstore-полей нет) | подтверждено (API). Сам SHA256 **не удалось проверить**: файл не скачивал |

---

## 9. Сводка рисков и рекомендуемая базовая конфигурация (выводы)

Предусловия на клиенте Win7 SP1 x64. Всё должно быть в офлайн-пакете установщика, если машины без WSUS.

1. SP1.
2. **KB3042058** — шифры GCM по умолчанию.
3. Корневые сертификаты. В офлайн-среде — Microsoft Root CA 2011 для установщика .NET и корни, нужные для LLM API.
4. **KB4019990** (D3DCompiler_47).
5. **.NET Framework 4.8**: `NDP48-x86-x64-AllOS-ENU.exe`.
6. **UCRT** (KB2999226 / KB3118401).
7. **KB2533623** или его замена (KB3063858).

Реестр SChannel: TLS 1.2 Client `DisabledByDefault=0`, `Enabled=1` (см. приложение B).
Ключи .NET `SystemDefaultTlsVersions=1` и `SchUseStrongCrypto=1` (оба узла, 64/32) — для надёжности. Для таргета 4.8 это и так значения по умолчанию.

Прочие решения:

- **HTTP-клиент в WPF:** на Win7 дополнительно явно задавать `SecurityProtocolType.Tls12` (страховка на случай, если реестр не применён). На новых ОС оставлять `SystemDefault`.
- **Выбор LLM-провайдера:** предварительно проверить рукопожатие с Win7-набором шифров. На 2026-09-24 DeepSeek через SChannel Win7 недоступен.
- **SQL:** `System.Data.SqlClient` (TDS 7.4, TLS 1.2). На сервере **не** включать Force Strict Encryption и **не** отключать TLS 1.2. Сохранить на сервере хотя бы один набор, общий с Win7: `TLS_ECDHE_RSA_WITH_AES_*_CBC_SHA256/SHA384` или `TLS_RSA_WITH_AES_*_GCM`. Сертификат сервера должен быть доверен на Win7; иначе `TrustServerCertificate=true`, осознанно.
- **FTS:** у индексов на SQL Server 2025 сразу создавать `FULLTEXT_INDEX_VERSION = 2`. Русский (1049) поддерживается.
- **Python:** 3.8.10 embed amd64; numpy — **1.23.5**, как самый безопасный для Win7 (или 1.24.4 после теста на целевой машине); pandas **2.0.3** или **1.5.3**; `python38._pth` дополнить `import site` и путём к `site-packages`. Устанавливать офлайн: `pip install --no-index --only-binary=:all: --require-hashes`.

---

## 10. Что не удалось проверить

- Явное требование KB4019990 для .NET Framework **4.8** (документировано только для 4.7 и роллапов 4.6–4.7.2).
- Установится ли переподписанный в 2025-02 `NDP48-x86-x64-AllOS-ENU.exe` на **чистую офлайн** Win7 без обновления корней.
- Явная фраза «Force Strict Encryption по умолчанию = No» для SQL Server 2025 (есть противоречащий ответ на Microsoft Q&A).
- Официальный статус `Microsoft.Data.SqlClient` на Win7 (явного упоминания нет) и точные нативные зависимости `Microsoft.Data.SqlClient.SNI.dll`.
- Работоспособность numpy 1.24.4 `win_amd64` на Win7: подтверждения только от сообщества.
- Наличие `msvcp140.dll` в колесе numpy 1.24.4 и способ упаковки DLL в pandas 1.5.3.
- Использование Win8+ API (например, `GetSystemTimePreciseAsFileTime`) в OpenBLAS/numpy: свидетельств не найдено.
- SHA256 для `python-3.8.10-embed-amd64.zip` (python.org его не публикует) и фактический состав zip.
- Корректная обработка текущего CTL корневых сертификатов на Win7. Результаты TLS-проверки через реальный SChannel Win7, а не через openssl-эмуляцию.
- Официальное подтверждение, что KB3063858 заменяет KB2533623.

---

## Приложение A. requirements с хэшами для офлайн-установки (Python 3.8, win_amd64)

Основной вариант (pandas 2.0.3 + numpy 1.24.4):

```
numpy==1.24.4 --hash=sha256:692f2e0f55794943c5bfff12b3f56f99af76f902fc47487bdfe97856de51a706
pandas==2.0.3 --hash=sha256:69d7f3884c95da3a31ef82b7618af5710dba95bb885ffab339aad925c3e8ce78
python-dateutil==2.9.0.post0 --hash=sha256:a8b2bc7bffae282281c8140a97d3aa9c14da0b136dfe83f850eea9a5f7470427
six==1.17.0 --hash=sha256:4721f391ed90541fddacab5acf947aa0d3dc7d27b2e1e8eda2be8970586c3274
pytz==2026.4 --hash=sha256:9d514388fbc89ca0833203464272ac485b8828568ab73532f5020f17e892a0ff
tzdata==2026.4 --hash=sha256:c2169a8b0a7a5e9674da5a135ccdfb2b3e671b333ed9fed17b41f73c34476e81
defusedxml==0.7.1 --hash=sha256:a352e7e428770286cc899e2542b6cdaedb2b4953ff269a210103ec58f6198a61
```

Чтобы уйти от numpy 1.24.x (см. раздел 7), замените строку numpy:

```
numpy==1.23.5 --hash=sha256:ca51fcfcc5f9354c45f400059e88bc09215fb71a48d3768fb80e357f3b457e1e
```

Консервативный вариант — pandas 1.5.3; `tzdata` для него не нужен:

```
pandas==1.5.3 --hash=sha256:41179ce559943d83a9b4bbacb736b04c928b095b5f25dd2b7389eda08f46f373
```

## Приложение B. .reg: включение TLS 1.2 (client) на Windows 7

Ключи SChannel взяты из официальной статьи о TLS 1.2 для SQL Server. Ключи .NET — из статьи «TLS best practices with .NET Framework».

```
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client]
"DisabledByDefault"=dword:00000000
"Enabled"=dword:00000001

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\.NETFramework\v4.0.30319]
"SystemDefaultTlsVersions"=dword:00000001
"SchUseStrongCrypto"=dword:00000001

[HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319]
"SystemDefaultTlsVersions"=dword:00000001
"SchUseStrongCrypto"=dword:00000001
```

После изменения SChannel нужна перезагрузка или перезапуск приложений: SChannel переиспользует credential handles.

---

## Источники (сгруппировано)

**.NET Framework / Windows**
- https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements
- https://devblogs.microsoft.com/dotnet/announcing-the-net-framework-4-8/
- https://support.microsoft.com/en-us/topic/microsoft-net-framework-4-8-offline-installer-for-windows-9d23f658-3b97-68ab-d013-aa3c3e7495e0
- https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48
- https://learn.microsoft.com/en-us/dotnet/framework/install/troubleshoot-blocked-installations-and-uninstallations
- https://support.microsoft.com/en-us/help/4019990/update-for-the-d3dcompiler-47-dll-component-on-windows
- https://support.microsoft.com/en-us/topic/the-net-framework-4-7-installation-is-blocked-on-windows-7-windows-server-2008-r2-and-windows-server-2012-because-of-a-missing-d3dcompiler-update-0869046a-0972-7824-1bb8-5d89bf99e112
- https://support.microsoft.com/en-us/topic/november-8-2022-security-and-quality-rollup-for-net-framework-3-5-1-4-6-2-4-7-4-7-1-4-7-2-4-8-for-windows-7-sp1-and-windows-server-2008-r2-sp1-kb5020688-5dff36fc-5033-47c2-919b-90edd25e2885
- https://learn.microsoft.com/en-us/lifecycle/products/windows-7
- https://github.com/dotnet/docs/issues/22308 ; https://learn.microsoft.com/en-us/archive/blogs/vsnetsetup/a-certificate-chain-could-not-be-built-to-a-trusted-root-authority-2

**TLS**
- https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls
- https://github.com/dotnet/docs/blob/3ff4927b96813d031bf03620551e85ed9721d2d5/docs/framework/network-programming/tls.md
- https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-
- https://learn.microsoft.com/en-us/windows-server/security/tls/tls-registry-settings
- https://github.com/MicrosoftDocs/windowsserverdocs/blob/75eddbd58bb1a6961e0f8cb87eb8c710190ccec7/WindowsServerDocs/security/tls/tls-registry-settings.md
- https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/connect/tls-1-2-support-microsoft-sql-server
- https://support.microsoft.com/topic/update-to-enable-tls-1-1-and-tls-1-2-as-default-secure-protocols-in-winhttp-in-windows-c4bd73d2-31d7-761e-0178-11268bb10392
- https://learn.microsoft.com/en-us/intune/configmgr/core/plan-design/security/enable-tls-1-2-client
- https://learn.microsoft.com/en-us/windows/win32/secauthn/tls-cipher-suites-in-windows-7
- https://support.microsoft.com/en-us/help/2992611/ms14-066-vulnerability-in-schannel-could-allow-remote-code-execution-n
- https://learn.microsoft.com/en-us/security-updates/SecurityAdvisories/2015/3042058
- https://learn.microsoft.com/en-us/windows/win32/secauthn/tls-cipher-suites-in-windows-server-2022
- https://learn.microsoft.com/en-us/windows/win32/secauthn/tls-cipher-suites-in-windows-server-2025

**SqlClient / SQL Server 2025**
- https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-driver-support-lifecycle?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/connect/ado-net/introduction-microsoft-data-sqlclient-namespace?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/connect/ado-net/migrate-system-data-sql-client-to-microsoft-data-sql-client?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/connect/ado-net/download-microsoft-sqlclient-data-provider?view=sql-server-ver17
- https://techcommunity.microsoft.com/blog/sqlserver/announcement-system-data-sqlclient-package-is-now-deprecated/4227205
- https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/tds-8?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/tls-1-3?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/relational-databases/security/networking/connect-with-strict-encryption?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/configure-sql-server-encryption?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/database-engine/breaking-changes-to-database-engine-features-in-sql-server-2025?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/sql-server/install/hardware-and-software-requirements-for-installing-sql-server-2025?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-search?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-index-version-upgrade?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-word-breaker-and-stemmer-binaries?view=sql-server-ver17
- https://learn.microsoft.com/en-us/answers/questions/5793397/confirming-net-4-8-compatibility-with-sql-server-2 (не документация)
- GitHub dotnet/SqlClient: #211, #570, #645, #3148

**Python**
- https://www.python.org/downloads/windows/
- https://www.python.org/downloads/release/python-3810/
- https://www.python.org/api/v2/downloads/release_file/?release=617
- https://peps.python.org/pep-0569/
- https://docs.python.org/3.8/using/windows.html
- https://docs.python.org/3.9/using/windows.html
- https://docs.python.org/release/3.8.10/whatsnew/changelog.html
- https://github.com/python/cpython/blob/v3.8.10/PC/layout/main.py
- https://support.microsoft.com/en-us/topic/microsoft-security-advisory-insecure-library-loading-could-allow-remote-code-execution-486ea436-2d47-27e5-6cb9-26ab7230c704
- https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment?view=msvc-170
- https://support.microsoft.com/en-us/kb/3118401
- https://github.com/dotnet/docs/issues/20459
- https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170

**numpy / pandas / PyPI**
- https://pypi.org/pypi/numpy/1.24.4/json ; https://pypi.org/pypi/numpy/1.23.5/json ; https://pypi.org/pypi/numpy/1.25.0/json
- https://pypi.org/pypi/pandas/2.0.3/json ; https://pypi.org/pypi/pandas/1.5.3/json ; https://pypi.org/pypi/pandas/2.1.0/json
- https://pypi.org/pypi/python-dateutil/2.9.0.post0/json ; https://pypi.org/pypi/six/1.17.0/json ; https://pypi.org/pypi/pytz/2026.4/json ; https://pypi.org/pypi/tzdata/2026.4/json ; https://pypi.org/pypi/defusedxml/0.7.1/json
- https://numpy.org/doc/stable/release/1.24.4-notes.html ; https://numpy.org/doc/stable/release/1.25.0-notes.html ; https://numpy.org/doc/stable/release/1.23.5-notes.html
- https://pandas.pydata.org/docs/whatsnew/v2.1.0.html ; https://pandas.pydata.org/docs/whatsnew/v2.0.0.html ; https://pandas.pydata.org/pandas-docs/version/2.0.3/getting_started/install.html
- GitHub numpy: #23074, #23324, #23731, #24832; tools/openblas_support.py и tools/wheels/LICENSE_win32.txt (v1.24.4, v1.23.5)
- GitHub pandas: #32857, #32936, #57963; ci/fix_wheels.py (v2.0.3)
