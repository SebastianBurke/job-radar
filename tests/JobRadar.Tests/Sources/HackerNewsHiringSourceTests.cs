using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class HackerNewsHiringSourceTests
{
    [Fact]
    public async Task Parses_thread_then_top_level_comments()
    {
        var fixtureJson = await File.ReadAllTextAsync("Fixtures/HackerNews/whoishiring_sample.json");
        using var doc = JsonDocument.Parse(fixtureJson);
        var thread = doc.RootElement.GetProperty("thread");
        var comments = doc.RootElement.GetProperty("comments")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetInt64(), c => c.GetRawText());
        var threadId = thread.GetProperty("id").GetInt64();

        var algoliaPayload = $$"""{"hits":[{"objectID":"{{threadId}}"}]}""";

        var handler = new StaticHttpHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            string body;
            if (req.RequestUri!.Host == "hn.algolia.com") body = algoliaPayload;
            else if (req.RequestUri.AbsolutePath.EndsWith($"/v0/item/{threadId}.json", StringComparison.Ordinal))
                body = thread.GetRawText();
            else
            {
                var segs = req.RequestUri.AbsolutePath.Split('/');
                var idStr = segs[^1].Replace(".json", string.Empty);
                if (long.TryParse(idStr, out var id) && comments.TryGetValue(id, out var raw)) body = raw;
                else body = "{\"id\":0}";
            }
            resp.Content = new StringContent(body);
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return resp;
        });

        var source = new HackerNewsHiringSource(
            new StaticHttpClientFactory(handler),
            new HostRateLimiter(TimeSpan.Zero),
            NullLogger<HackerNewsHiringSource>.Instance);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("hackernews", p.Source);
            Assert.StartsWith("https://news.ycombinator.com/item?id=", p.Url);
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
        });
    }
}
