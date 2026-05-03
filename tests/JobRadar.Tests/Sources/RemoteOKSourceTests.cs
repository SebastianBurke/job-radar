using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class RemoteOKSourceTests
{
    [Fact]
    public async Task Parses_real_remoteok_response()
    {
        var fixture = await File.ReadAllTextAsync("Fixtures/RemoteOK/remoteok_sample.json");
        var handler = StaticHttpHandler.FromFixture("application/json", fixture);
        var factory = new StaticHttpClientFactory(handler);

        var source = new RemoteOKSource(factory, new HostRateLimiter(TimeSpan.Zero), NullLogger<RemoteOKSource>.Instance);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        // First entry in RemoteOK API response is a metadata banner; the other 3 are jobs.
        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("remoteok", p.Source);
            Assert.False(string.IsNullOrWhiteSpace(p.Company));
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Url));
        });
    }
}
