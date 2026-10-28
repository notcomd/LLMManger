using System.Net;
using System.Text;
using Mono.LLM.Agent.Mcp;

namespace Mono.LLM.Agent.Tests.Mcp;

/// <summary>
/// 记录请求的 HTTP 桩，用于验证 OAuth 各步骤实际发出的报文。
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _responder;

    public RecordingHttpMessageHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<RecordedRequest> Requests { get; } = new();

    public RecordedRequest Single(string pathSuffix) =>
        Requests.Single(r => r.Uri.AbsolutePath.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!,
            body,
            request.Headers.ToDictionary(
                h => h.Key,
                h => string.Join(",", h.Value),
                StringComparer.OrdinalIgnoreCase)));

        return _responder(request, body);
    }

    public sealed record RecordedRequest(
        string Method,
        Uri Uri,
        string? Body,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string? Form(string name) =>
            Body is null ? null : QueryParameters.Parse(Body).GetValueOrDefault(name);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}
