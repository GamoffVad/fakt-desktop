using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.UnitTests.TestSupport;

/// <summary>Запрос, полученный имитатором сервера.</summary>
internal sealed class RecordedRequest
{
    public RecordedRequest(string method, string rawUrl, IReadOnlyDictionary<string, string> headers, string body)
    {
        Method = method;
        RawUrl = rawUrl;
        Headers = headers;
        Body = body;
        var question = rawUrl.IndexOf('?');
        Path = question < 0 ? rawUrl : rawUrl.Substring(0, question);
        Query = question < 0 ? string.Empty : rawUrl.Substring(question + 1);
    }

    public string Method { get; }

    /// <summary>Путь с запросом, как передан клиентом (без хоста).</summary>
    public string RawUrl { get; }

    public string Path { get; }

    public string Query { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public string Body { get; }

    public JObject Json => JObject.Parse(Body);

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Ответ имитатора: код, заголовки и тело либо обрыв соединения.</summary>
internal sealed class MockResponse
{
    public int StatusCode { get; set; } = 200;

    public string Body { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/json; charset=utf-8";

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AbortConnection { get; set; }

    public static MockResponse Json(string body, int status = 200) => new() { StatusCode = status, Body = body };

    public static MockResponse Json(object body, int status = 200) => Json(JsonConvert.SerializeObject(body), status);

    public static MockResponse Text(string body, int status, string contentType = "text/html; charset=utf-8") =>
        new() { StatusCode = status, Body = body, ContentType = contentType };

    public static MockResponse Abort() => new() { AbortConnection = true };

    public MockResponse WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }
}

/// <summary>
/// Имитатор HTTP-сервера провайдера LLM на System.Net.HttpListener: только http://127.0.0.1:&lt;свободный порт&gt;/.
/// Записывает запросы (метод, путь, заголовки, тело) и отвечает по обработчику теста.
/// </summary>
internal sealed class MockHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly SemaphoreSlim _received = new(0);
    private readonly Task _loop;
    private Func<RecordedRequest, CancellationToken, Task<MockResponse>> _handler;

    public MockHttpServer()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener { IgnoreWriteExceptions = true };
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }

        BaseUrl = $"http://127.0.0.1:{Port}/";
        _handler = (_, _) => Task.FromResult(MockResponse.Json("{\"error\":{\"message\":\"no handler configured\"}}", 500));
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    /// <summary>Корень сервера со слешем в конце: http://127.0.0.1:port/.</summary>
    public string BaseUrl { get; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public RecordedRequest SingleRequest()
    {
        var requests = Requests;
        if (requests.Count != 1)
        {
            throw new InvalidOperationException($"Ожидался один запрос, получено {requests.Count}.");
        }

        return requests[0];
    }

    public void Respond(Func<RecordedRequest, MockResponse> handler) =>
        _handler = (request, _) => Task.FromResult(handler(request));

    public void Respond(MockResponse response) => Respond(_ => response);

    public void RespondAsync(Func<RecordedRequest, CancellationToken, Task<MockResponse>> handler) => _handler = handler;

    /// <summary>Не отвечать, пока сервер не остановлен (для проверки тайм-аута и отмены).</summary>
    public void Hang() => RespondAsync(async (_, stop) =>
    {
        await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
        return MockResponse.Abort();
    });

    /// <summary>Дождаться поступления очередного запроса.</summary>
    public Task<bool> WaitForRequestAsync(TimeSpan timeout) => _received.WaitAsync(timeout);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in context.Request.Headers.AllKeys)
            {
                headers[name] = context.Request.Headers[name];
            }

            var recorded = new RecordedRequest(context.Request.HttpMethod, context.Request.RawUrl, headers, body);
            lock (_requests)
            {
                _requests.Add(recorded);
            }

            _received.Release();

            MockResponse response;
            try
            {
                response = await _handler(recorded, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                response = MockResponse.Abort();
            }

            if (response == null || response.AbortConnection)
            {
                context.Response.Abort();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
            context.Response.StatusCode = response.StatusCode;
            if (response.StatusCode == 529)
            {
                context.Response.StatusDescription = "Overloaded";
            }

            foreach (var header in response.Headers)
            {
                context.Response.AddHeader(header.Key, header.Value);
            }

            context.Response.ContentType = response.ContentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
