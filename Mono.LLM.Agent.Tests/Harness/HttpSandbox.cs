using System.Net;
using System.Text;

namespace Mono.LLM.Agent.Tests.Harness;

/// <summary>
/// 记录一次出站 HTTP 请求及其响应状态。
/// </summary>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Uri">请求地址。</param>
/// <param name="Body">请求体；无请求体时为 <c>null</c>。</param>
/// <param name="StatusCode">返回的状态码。</param>
public sealed record HttpRecord(string Method, string Uri, string? Body, int StatusCode);

/// <summary>
/// 拦截并记录出站 HTTP 请求，用预设响应替代真实网络。
/// </summary>
/// <remarks>
/// <para>
/// 测试一旦意外访问真实服务，轻则因网络抖动变得不稳定，重则消耗真实额度。
/// 沙箱接管全部出站请求，既能断言「发了什么」，也能断言「有没有多发」。
/// </para>
/// <para>
/// 适用于 <c>OllamaChatClient</c> 这类接受注入 <see cref="HttpClient"/> 的通道；
/// 内部自行创建 <c>HttpClient</c> 的 Provider 无法被此沙箱约束。
/// </para>
/// </remarks>
public sealed class HttpSandbox
{
    private readonly List<HttpRecord> _requests = new();
    private readonly object _gate = new();
    private Func<HttpRequestMessage, (string Body, HttpStatusCode Status)> _responder;

    /// <summary>创建一个 HTTP 沙箱，默认对任何请求返回空 JSON。</summary>
    public HttpSandbox()
    {
        _responder = _ => ("{}", HttpStatusCode.OK);
    }

    /// <summary>创建 <see cref="HttpClient"/> 时使用的默认基地址。</summary>
    public string BaseAddress { get; set; } = "http://localhost:11434";

    /// <summary>已记录的请求，按发生顺序排列。</summary>
    public IReadOnlyList<HttpRecord> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    /// <summary>已拦截的请求总数。</summary>
    public int RequestCount
    {
        get
        {
            lock (_gate)
                return _requests.Count;
        }
    }

    /// <summary>
    /// 让后续所有请求返回固定响应体。
    /// </summary>
    /// <param name="body">响应体内容。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <returns>当前沙箱，支持链式配置。</returns>
    public HttpSandbox RespondWith(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(body);

        lock (_gate)
            _responder = _ => (body, status);

        return this;
    }

    /// <summary>
    /// 按请求内容动态生成响应体。
    /// </summary>
    /// <param name="factory">根据请求返回响应体。</param>
    /// <returns>当前沙箱，支持链式配置。</returns>
    public HttpSandbox RespondWith(Func<HttpRequestMessage, string> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
            _responder = request => (factory(request), HttpStatusCode.OK);

        return this;
    }

    /// <summary>
    /// 让请求依次返回不同的响应体，超出序列长度后重复最后一个。
    /// </summary>
    /// <param name="bodies">按顺序使用的响应体。</param>
    /// <returns>当前沙箱，支持链式配置。</returns>
    /// <exception cref="ArgumentException">当序列为空时抛出。</exception>
    public HttpSandbox RespondWithSequence(params string[] bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);

        if (bodies.Length == 0)
            throw new ArgumentException("响应序列不能为空。", nameof(bodies));

        var index = -1;

        lock (_gate)
        {
            _responder = _ =>
            {
                var current = Interlocked.Increment(ref index);
                var body = bodies[Math.Min(current, bodies.Length - 1)];
                return (body, HttpStatusCode.OK);
            };
        }

        return this;
    }

    /// <summary>
    /// 创建一个由本沙箱接管的 <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="baseAddress">基地址，省略时使用 <see cref="BaseAddress"/>。</param>
    /// <returns>不会访问真实网络的客户端。</returns>
    public HttpClient CreateClient(string? baseAddress = null)
    {
        var client = new HttpClient(new RecordingHandler(this))
        {
            BaseAddress = new Uri(baseAddress ?? BaseAddress)
        };

        return client;
    }

    /// <summary>清空请求记录，保留响应配置。</summary>
    public void ClearRequests()
    {
        lock (_gate)
            _requests.Clear();
    }

    private (string Body, HttpStatusCode Status) Respond(HttpRequestMessage request)
    {
        lock (_gate)
            return _responder(request);
    }

    private void Record(HttpRequestMessage request, string? body, int statusCode)
    {
        var record = new HttpRecord(
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            body,
            statusCode);

        lock (_gate)
            _requests.Add(record);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpSandbox _owner;

        public RecordingHandler(HttpSandbox owner) => _owner = owner;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var (responseBody, status) = _owner.Respond(request);

            _owner.Record(request, body, (int)status);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
