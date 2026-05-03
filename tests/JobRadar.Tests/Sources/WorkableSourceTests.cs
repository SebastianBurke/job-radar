using JobRadar.Core.Config;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class WorkableSourceTests
{
    [Fact]
    public async Task Parses_real_workable_response()
    {
        var fixture = await File.ReadAllTextAsync("Fixtures/Workable/blueground_sample.json");
        var handler = StaticHttpHandler.FromFixture("application/json", fixture);
        var factory = new StaticHttpClientFactory(handler);

        var companies = new CompaniesConfig
        {
            Companies = { new CompanyEntry { Name = "Blueground", Region = "global", Ats = "workable", Token = "blueground" } },
        };

        var source = new WorkableSource(factory, new HostRateLimiter(TimeSpan.Zero), companies, NullLogger<WorkableSource>.Instance);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        Assert.Equal(3, postings.Count);
        Assert.All(postings, p =>
        {
            Assert.Equal("workable", p.Source);
            Assert.Equal("Blueground", p.Company);
            Assert.StartsWith("https://apply.workable.com/", p.Url);
            // Workable widget API has no per-job description; we synthesize from job + company metadata.
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
        });
    }
}
