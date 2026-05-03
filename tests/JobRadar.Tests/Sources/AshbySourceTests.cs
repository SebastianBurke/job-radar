using JobRadar.Core.Config;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class AshbySourceTests
{
    [Fact]
    public async Task Parses_real_ashby_response()
    {
        var fixture = await File.ReadAllTextAsync("Fixtures/Ashby/ashby_sample.json");
        var handler = StaticHttpHandler.FromFixture("application/json", fixture);
        var factory = new StaticHttpClientFactory(handler);

        var companies = new CompaniesConfig
        {
            Companies = { new CompanyEntry { Name = "Ashby", Region = "global", Ats = "ashby", Token = "ashby" } },
        };

        var source = new AshbySource(factory, new HostRateLimiter(TimeSpan.Zero), companies, NullLogger<AshbySource>.Instance);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("ashby", p.Source);
            Assert.Equal("Ashby", p.Company);
            Assert.StartsWith("https://jobs.ashbyhq.com/ashby/", p.Url);
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.DoesNotContain("<p", p.Description);
        });
    }
}
