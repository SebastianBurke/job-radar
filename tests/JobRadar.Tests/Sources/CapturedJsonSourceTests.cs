using JobRadar.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources;

public sealed class CapturedJsonSourceTests
{
    private static CapturedJsonSource New(string dir) =>
        new(dir, stalenessWarningDays: 7, NullLogger<CapturedJsonSource>.Instance);

    [Fact]
    public async Task Missing_directory_returns_empty_without_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"job-radar-captured-missing-{Guid.NewGuid():N}");
        // Don't create the dir.
        var source = New(dir);

        var postings = new List<Core.Models.JobPosting>();
        await foreach (var p in source.FetchAsync()) postings.Add(p);

        Assert.Empty(postings);
    }

    [Fact]
    public async Task Empty_directory_returns_empty_without_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"job-radar-captured-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var source = New(dir);

            var postings = new List<Core.Models.JobPosting>();
            await foreach (var p in source.FetchAsync()) postings.Add(p);

            Assert.Empty(postings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Valid_file_emits_postings_tagged_authoritative()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"job-radar-captured-valid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "gcjobs.json"), """
                {
                  "captured_at": "2026-05-08T10:00:00Z",
                  "source": "gcjobs",
                  "postings": [
                    {
                      "ats_id": "1234567",
                      "title": "Web Developer",
                      "company": "Service Canada",
                      "location": "Ottawa, Ontario, Canada",
                      "url": "https://emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/x/1234567",
                      "description": "Full description here.",
                      "posted_at": "2026-05-01",
                      "department": "Service Canada",
                      "metadata": { "classification": "IT-02", "language_requirement": "BBB" }
                    },
                    {
                      "ats_id": "1234568",
                      "title": "Senior Solutions Architect",
                      "company": "IRCC",
                      "location": "Ottawa, Ontario, Canada",
                      "url": "https://emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/x/1234568",
                      "description": "Architect role JD.",
                      "metadata": { "classification": "IT-04" }
                    }
                  ]
                }
                """);
            var source = New(dir);

            var postings = new List<Core.Models.JobPosting>();
            await foreach (var p in source.FetchAsync()) postings.Add(p);

            Assert.Equal(2, postings.Count);
            Assert.All(postings, p =>
            {
                Assert.Equal("gcjobs", p.Source);
                Assert.Equal(Core.Models.LocationConfidence.Authoritative, p.LocationConfidence);
            });
            Assert.Equal("Web Developer", postings[0].Title);
            Assert.Equal("Service Canada", postings[0].Company);
            Assert.Equal("1234567", postings[0].AtsId);
            Assert.NotNull(postings[0].PostedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Malformed_json_is_skipped_and_other_files_still_processed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"job-radar-captured-malformed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), "{not real json at all}");
            await File.WriteAllTextAsync(Path.Combine(dir, "good.json"), """
                {
                  "captured_at": "2026-05-08T10:00:00Z",
                  "source": "test",
                  "postings": [
                    {
                      "title": "OK Posting",
                      "company": "Acme",
                      "location": "Remote",
                      "url": "https://x/1",
                      "description": "ok"
                    }
                  ]
                }
                """);
            var source = New(dir);

            var postings = new List<Core.Models.JobPosting>();
            await foreach (var p in source.FetchAsync()) postings.Add(p);

            Assert.Single(postings);
            Assert.Equal("OK Posting", postings[0].Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Postings_missing_required_fields_are_individually_skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"job-radar-captured-incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "mixed.json"), """
                {
                  "captured_at": "2026-05-08T10:00:00Z",
                  "source": "test",
                  "postings": [
                    { "title": "", "company": "Acme", "location": "Remote", "url": "https://x/1", "description": "ok" },
                    { "title": "Real one", "company": "Acme", "location": "Remote", "url": "https://x/2", "description": "yes" },
                    { "title": "No URL", "company": "Acme", "location": "Remote", "url": "", "description": "no" }
                  ]
                }
                """);
            var source = New(dir);

            var postings = new List<Core.Models.JobPosting>();
            await foreach (var p in source.FetchAsync()) postings.Add(p);

            Assert.Single(postings);
            Assert.Equal("Real one", postings[0].Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Stale_captured_at_still_emits_postings()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"job-radar-captured-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "old.json"), """
                {
                  "captured_at": "2024-01-01T00:00:00Z",
                  "source": "ancient",
                  "postings": [
                    {
                      "title": "Old role",
                      "company": "Acme",
                      "location": "Remote",
                      "url": "https://x/1",
                      "description": "still ok"
                    }
                  ]
                }
                """);
            var source = New(dir);

            var postings = new List<Core.Models.JobPosting>();
            await foreach (var p in source.FetchAsync()) postings.Add(p);

            // Stale captures still flow — the warning is just diagnostic.
            Assert.Single(postings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task File_missing_source_field_is_skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"job-radar-captured-nosource-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // captured_at present but no `source` field — file is unusable because
            // we wouldn't know what to set on JobPosting.Source.
            await File.WriteAllTextAsync(Path.Combine(dir, "nosrc.json"), """
                {
                  "captured_at": "2026-05-08T10:00:00Z",
                  "postings": [
                    { "title": "x", "company": "y", "location": "z", "url": "https://q", "description": "w" }
                  ]
                }
                """);
            var source = New(dir);

            var postings = new List<Core.Models.JobPosting>();
            await foreach (var p in source.FetchAsync()) postings.Add(p);

            Assert.Empty(postings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
