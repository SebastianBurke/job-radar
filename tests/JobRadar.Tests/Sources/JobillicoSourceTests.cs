using JobRadar.Core.Config;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class JobillicoSourceTests
{
    [Fact]
    public async Task Fetches_and_parses_real_jobillico_html()
    {
        // Fixture captured 2026-05-08 via:
        //   curl -A 'JobRadar/1.0 (personal-bot)' \
        //     'https://www.jobillico.com/search-jobs?skwd=software+developer&sloc=Outaouais&srdb=10&jobAge=14'
        var fixture = await File.ReadAllTextAsync("Fixtures/Jobillico/jobillico_software_outaouais.html");
        var handler = StaticHttpHandler.FromFixture("text/html", fixture);
        var factory = new StaticHttpClientFactory(handler);

        // One search × one location keeps the cross product to a single fetch
        // so we don't have to handle the static fixture being served multiple times.
        var sourcesConfig = new SourcesConfig
        {
            Jobillico = new JobillicoSourceConfig
            {
                SearchTerms = { "software developer" },
                Locations = { "Outaouais" },
            },
        };

        var source = new JobillicoSource(
            factory,
            new HostRateLimiter(TimeSpan.Zero),
            NullLogger<JobillicoSource>.Instance,
            sourcesConfig);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        // The page that fixture was captured from showed 11 listings;
        // assert at least 5 to leave headroom for HTML drift.
        Assert.True(postings.Count >= 5, $"Expected ≥5 postings, got {postings.Count}");

        Assert.All(postings, p =>
        {
            Assert.Equal("jobillico", p.Source);
            Assert.False(string.IsNullOrWhiteSpace(p.Title), "title required");
            Assert.False(string.IsNullOrWhiteSpace(p.Company), "company required");
            Assert.StartsWith("https://www.jobillico.com/en/job-offer/", p.Url, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(p.AtsId), "AtsId required for live-check / dedup");
            Assert.Equal(Core.Models.LocationConfidence.AggregatorOnly, p.LocationConfidence);
        });

        // The first listing on the captured page was Nord Quantique / Software Developer in Sherbrooke;
        // verify a posting with that signature comes through with NormalizeLocation expanding "QC".
        var nordQ = postings.FirstOrDefault(p =>
            p.Company.Contains("Nord Quantique", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(nordQ);
        Assert.Contains("Software Developer", nordQ!.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quebec", nordQ.Location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Canada", nordQ.Location, StringComparison.OrdinalIgnoreCase);
        // Salary and posted-at should land on the JobPosting.
        Assert.Contains("Salary:", nordQ.Description, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(nordQ.PostedAt);
    }

    [Fact]
    public void NormalizeLocation_expands_province_codes_so_filter_matches()
    {
        // Direct check of the two- vs full-province transformation. The pipeline's
        // location_allow has 'quebec'/'ontario'/'canada' but not 'qc'/'on', so the
        // raw "Sherbrooke - QC" string would be filtered out before scoring.
        var sample = "<article data-job-url=\"foo/bar/123\" data-company-id=\"1\">" +
                     "<h2 class=\"h3\"><a href=\"#\">T</a></h2>" +
                     "<h3 class=\"h4\"><a class=\"link companyLink\" href=\"#\">C</a></h3>" +
                     "<p class=\"xs word-break\">desc</p>" +
                     "<span class=\"icon icon--information--position\"></span><p>Sherbrooke - QC</p>" +
                     "</article>";
        var posting = JobillicoSource.Parse(sample).Single();
        Assert.Equal("Sherbrooke, Quebec, Canada", posting.Location);
    }

    [Fact]
    public void Parse_dedupes_identical_ids_across_calls()
    {
        // The cross product of search_terms × locations may surface the same
        // posting twice; the AtsId-based dedupe inside Parse should catch it.
        var sample = "<article data-job-url=\"foo/bar/999\" data-company-id=\"1\">" +
                     "<h2 class=\"h3\"><a href=\"#\">T</a></h2>" +
                     "<h3 class=\"h4\"><a class=\"link companyLink\" href=\"#\">C</a></h3>" +
                     "<p class=\"xs word-break\">d</p>" +
                     "</article>";
        var seen = new HashSet<string>();
        Assert.Single(JobillicoSource.Parse(sample, seen));
        Assert.Empty(JobillicoSource.Parse(sample, seen));
    }
}
