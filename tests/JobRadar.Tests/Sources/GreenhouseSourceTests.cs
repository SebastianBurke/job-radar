using JobRadar.Core.Config;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class GreenhouseSourceTests
{
    [Fact]
    public async Task Fetches_and_parses_real_greenhouse_response()
    {
        var fixture = await File.ReadAllTextAsync("Fixtures/Greenhouse/airbnb_sample.json");
        var handler = StaticHttpHandler.FromFixture("application/json", fixture);
        var factory = new StaticHttpClientFactory(handler);

        var companies = new CompaniesConfig
        {
            Companies = { new CompanyEntry { Name = "Airbnb", Region = "global", Ats = "greenhouse", Token = "airbnb" } },
        };

        var source = new GreenhouseSource(
            factory,
            new HostRateLimiter(TimeSpan.Zero),
            companies,
            NullLogger<GreenhouseSource>.Instance);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync())
        {
            postings.Add(p);
        }

        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("greenhouse", p.Source);
            Assert.Equal("Airbnb", p.Company);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Url));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.DoesNotContain("<", p.Description);
            // ATS source must mark its location as authoritative.
            Assert.Equal(Core.Models.LocationConfidence.Authoritative, p.LocationConfidence);
            Assert.Equal("airbnb", p.AtsToken);
        });

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal("https://boards-api.greenhouse.io/v1/boards/airbnb/jobs?content=true", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Skips_companies_with_unknown_token()
    {
        var handler = StaticHttpHandler.FromFixture("application/json", "{\"jobs\":[]}");
        var factory = new StaticHttpClientFactory(handler);

        var companies = new CompaniesConfig
        {
            Companies =
            {
                new CompanyEntry { Name = "Unknown", Region = "ca", Ats = "greenhouse", Token = "?" },
                new CompanyEntry { Name = "Lever Co", Region = "ca", Ats = "lever", Token = "leverco" },
            },
        };

        var source = new GreenhouseSource(
            factory,
            new HostRateLimiter(TimeSpan.Zero),
            companies,
            NullLogger<GreenhouseSource>.Instance);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync())
        {
            postings.Add(p);
        }

        Assert.Empty(postings);
        Assert.Empty(handler.Requests);
    }
}
