using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class WeWorkRemotelySourceTests
{
    [Fact]
    public async Task Parses_real_wwr_rss_feed()
    {
        var xml = await File.ReadAllTextAsync("Fixtures/WeWorkRemotely/wwr_sample.xml");
        var handler = StaticHttpHandler.FromFixture("application/rss+xml", xml);
        var factory = new StaticHttpClientFactory(handler);

        var source = new WeWorkRemotelySource(
            factory,
            new HostRateLimiter(TimeSpan.Zero),
            NullLogger<WeWorkRemotelySource>.Instance,
            feeds: new[] { "https://weworkremotely.com/categories/remote-programming-jobs.rss" });

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("weworkremotely", p.Source);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Url));
            Assert.False(string.IsNullOrWhiteSpace(p.Company));
        });
    }
}
