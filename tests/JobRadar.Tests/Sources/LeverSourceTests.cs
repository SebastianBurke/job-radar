using JobRadar.Core.Config;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class LeverSourceTests
{
    [Fact]
    public async Task Parses_real_lever_response()
    {
        var fixture = await File.ReadAllTextAsync("Fixtures/Lever/leverdemo_sample.json");
        var handler = StaticHttpHandler.FromFixture("application/json", fixture);
        var factory = new StaticHttpClientFactory(handler);

        var companies = new CompaniesConfig
        {
            Companies = { new CompanyEntry { Name = "Lever Demo", Region = "global", Ats = "lever", Token = "leverdemo" } },
        };

        var source = new LeverSource(factory, new HostRateLimiter(TimeSpan.Zero), companies, NullLogger<LeverSource>.Instance);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("lever", p.Source);
            Assert.Equal("Lever Demo", p.Company);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.StartsWith("https://jobs.lever.co/leverdemo/", p.Url);
        });

        Assert.Equal("https://api.lever.co/v0/postings/leverdemo?mode=json", handler.Requests[0].RequestUri!.ToString());
    }
}
