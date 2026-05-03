using System.Net;
using System.Net.Http.Headers;

namespace JobRadar.Tests.TestUtils;

public sealed class StaticHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<HttpRequestMessage> Requests { get; } = new();

    public StaticHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static StaticHttpHandler FromFixture(string mediaType, string body) =>
        new(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return resp;
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_responder(request));
    }
}

public sealed class StaticHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StaticHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
