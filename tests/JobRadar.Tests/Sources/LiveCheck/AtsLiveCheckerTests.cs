using System.Net;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using JobRadar.Sources.LiveCheck;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Sources.LiveCheck;

public sealed class AtsLiveCheckerTests
{
    private static AtsLiveChecker NewChecker(StaticHttpHandler handler) =>
        new(
            new StaticHttpClientFactory(handler),
            new HostRateLimiter(TimeSpan.Zero),
            NullLogger<AtsLiveChecker>.Instance);

    private static JobPosting Greenhouse(string id = "12345", string token = "airbnb") =>
        new(
            Source: "greenhouse",
            Company: "Airbnb",
            Title: ".NET Engineer",
            Location: "Remote",
            Url: $"https://boards.greenhouse.io/{token}/jobs/{id}",
            Description: "stuff",
            AtsId: id,
            AtsToken: token);

    private static JobPosting Lever(string id = "abc-def", string company = "leverdemo") =>
        new(
            Source: "lever",
            Company: "Lever Demo",
            Title: ".NET Engineer",
            Location: "Remote",
            Url: $"https://jobs.lever.co/{company}/{id}",
            Description: "stuff",
            AtsId: id,
            AtsToken: company);

    private static JobPosting Ashby(string id = "xyz-789", string org = "lightspeedhq") =>
        new(
            Source: "ashby",
            Company: "Lightspeed",
            Title: ".NET Engineer",
            Location: "Remote",
            Url: $"https://jobs.ashbyhq.com/{org}/{id}",
            Description: "stuff",
            AtsId: id,
            AtsToken: org);

    private static JobPosting Workable(string id = "ABCDEF1234", string account = "genetec-inc") =>
        new(
            Source: "workable",
            Company: "Genetec",
            Title: ".NET Engineer",
            Location: "Remote",
            Url: $"https://apply.workable.com/{account}/j/{id}",
            Description: "stuff",
            AtsId: id,
            AtsToken: account);

    private static JobPosting Aggregator(string url = "https://example.com/jobs/42") =>
        new(
            Source: "remoteok",
            Company: "Anyone",
            Title: ".NET Engineer",
            Location: "Remote",
            Url: url,
            Description: "stuff");

    [Fact]
    public async Task None_mode_returns_Live_without_calling_http()
    {
        var handler = new StaticHttpHandler(_ => throw new InvalidOperationException("None mode must not GET"));
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Greenhouse(), LiveCheckMode.None);

        Assert.Equal(LiveCheckVerdict.Live, result.Verdict);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Greenhouse_200_returns_Live_and_hits_canonical_api()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":12345,"title":"."}"""),
        });
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Greenhouse(id: "12345", token: "airbnb"), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Live, result.Verdict);
        Assert.Single(handler.Requests);
        Assert.Equal(
            "https://boards-api.greenhouse.io/v1/boards/airbnb/jobs/12345",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Greenhouse_404_returns_Dead()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Greenhouse(), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Dead, result.Verdict);
    }

    [Fact]
    public async Task Greenhouse_500_returns_Unknown()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Greenhouse(), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public async Task Greenhouse_can_recover_token_and_id_from_URL_when_fields_missing()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        });
        var checker = NewChecker(handler);

        var posting = Greenhouse(id: "99", token: "wallapop") with { AtsId = null, AtsToken = null };
        var result = await checker.CheckAsync(posting, LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Live, result.Verdict);
        Assert.Equal(
            "https://boards-api.greenhouse.io/v1/boards/wallapop/jobs/99",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Lever_hits_canonical_api_and_classifies_status()
    {
        var handler = new StaticHttpHandler(req =>
        {
            return req.RequestUri!.AbsolutePath.EndsWith("/postings/leverdemo/abc-def", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"abc-def"}""") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Lever(), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Live, result.Verdict);
        Assert.Equal(
            "https://api.lever.co/v0/postings/leverdemo/abc-def",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Ashby_uses_public_jobs_url_for_liveness()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>posting page</html>"),
        });
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Ashby(), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Live, result.Verdict);
        Assert.StartsWith("https://jobs.ashbyhq.com/", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Workable_uses_public_apply_url_for_liveness()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Workable(), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Dead, result.Verdict);
        Assert.StartsWith("https://apply.workable.com/", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Aggregator_404_returns_Dead()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Aggregator(), LiveCheckMode.BestEffort);

        Assert.Equal(LiveCheckVerdict.Dead, result.Verdict);
    }

    [Fact]
    public async Task Aggregator_body_marker_no_longer_available_returns_Dead()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>This job is no longer available.</body></html>"),
        });
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Aggregator(), LiveCheckMode.BestEffort);

        Assert.Equal(LiveCheckVerdict.Dead, result.Verdict);
    }

    [Fact]
    public async Task Aggregator_200_with_normal_body_returns_Live()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Apply now! Senior .NET role.</body></html>"),
        });
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Aggregator(), LiveCheckMode.BestEffort);

        Assert.Equal(LiveCheckVerdict.Live, result.Verdict);
    }

    [Fact]
    public async Task Greenhouse_200_with_empty_body_returns_Dead()
    {
        // Spec: "200 with non-null body". Empty body is treated as gone.
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty),
        });
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Greenhouse(), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Dead, result.Verdict);
    }

    [Fact]
    public async Task Exception_during_check_is_swallowed_as_Unknown()
    {
        var handler = new StaticHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var checker = NewChecker(handler);

        var result = await checker.CheckAsync(Greenhouse(), LiveCheckMode.RequireOk);

        Assert.Equal(LiveCheckVerdict.Unknown, result.Verdict);
    }
}
