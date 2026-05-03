using System.Net.Http.Headers;

namespace JobRadar.Sources.Internal;

public static class HttpClientFactoryExtensions
{
    public const string UserAgent = "JobRadar/1.0 (personal-bot)";

    public static HttpClient CreateJobRadarClient(this IHttpClientFactory factory, string? name = null)
    {
        var client = string.IsNullOrEmpty(name) ? factory.CreateClient() : factory.CreateClient(name);
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}
