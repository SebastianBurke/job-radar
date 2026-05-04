using JobRadar.Core.Config;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class RemotiveSourceTests
{
    [Fact]
    public async Task Parses_real_remotive_response_and_dedupes_across_terms()
    {
        var fixture = await File.ReadAllTextAsync("Fixtures/Remotive/remotive_sample.json");
        var handler = StaticHttpHandler.FromFixture("application/json", fixture);
        var factory = new StaticHttpClientFactory(handler);

        var sources = new SourcesConfig { Remotive = new() { SearchTerms = { ".net", "c#" } } };
        var source = new RemotiveSource(
            factory,
            new HostRateLimiter(TimeSpan.Zero),
            NullLogger<RemotiveSource>.Instance,
            sources);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        // Both search-term calls return the same fixture; the second should be deduped by id.
        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("remotive", p.Source);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Url));
        });
        Assert.Equal(2, handler.Requests.Count);
    }
}
