using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sitewarm.Sdk;
using Sitewarm.Sdk.Model;
using Xunit;

namespace Sitewarm.Sdk.Tests;

public class SiteWarmClientTests
{
    private const string Key = "swk_secret_key";

    private sealed class Fake : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _h;
        public List<(HttpMethod Method, string Url, Dictionary<string, string> Headers, string? Body)> Seen { get; } = new();

        public Fake(Func<int, HttpRequestMessage, HttpResponseMessage> h) => _h = h;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var headers = req.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = req.Content == null ? null : await req.Content.ReadAsStringAsync(ct);
            Seen.Add((req.Method, req.RequestUri!.AbsoluteUri, headers, body));
            return _h(Seen.Count, req);
        }
    }

    private static HttpResponseMessage Reply(int status, string body = "", params (string, string)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Slept { get; } = new();
    }

    private static (SiteWarmClient, Fake, Clock) Make(Func<int, HttpRequestMessage, HttpResponseMessage> h, Action<SiteWarmOptions>? cfg = null)
    {
        var fake = new Fake(h);
        var clock = new Clock();
        var o = new SiteWarmOptions
        {
            Handler = fake,
            Clock = () => clock.Now,
            Delay = (d, _) => { clock.Slept.Add(d); clock.Now += d; return Task.CompletedTask; },
        };
        cfg?.Invoke(o);
        return (new SiteWarmClient(Key, "https://api.example.test/v1", o), fake, clock);
    }

    [Fact]
    public async Task RequestShapeAndAuth()
    {
        var (c, f, _) = Make((_, _) => Reply(200, "{\"id\":\"s1\"}"));
        var r = await c.CallAsync<JsonElement>("getSite", new Request().WithPath("siteId", "a/b"));
        Assert.Equal("s1", r.GetProperty("id").GetString());
        Assert.Equal("https://api.example.test/v1/sites/a%2Fb", f.Seen[0].Url);
        Assert.Equal("Bearer " + Key, f.Seen[0].Headers["Authorization"]);
        Assert.False(f.Seen[0].Headers.ContainsKey("Idempotency-Key"));
    }

    [Theory]
    [InlineData(401, ApiErrorKind.Authentication)]
    [InlineData(403, ApiErrorKind.Permission)]
    [InlineData(404, ApiErrorKind.NotFound)]
    [InlineData(409, ApiErrorKind.Conflict)]
    [InlineData(422, ApiErrorKind.Validation)]
    [InlineData(429, ApiErrorKind.RateLimited)]
    [InlineData(500, ApiErrorKind.Server)]
    public async Task ProblemBecomesTypedErrorWithoutKey(int status, ApiErrorKind kind)
    {
        var (c, _, _) = Make((_, _) => Reply(status, "{\"type\":\"https://x.test/problems/some-slug\",\"title\":\"T\",\"detail\":\"D\"}",
            ("Retry-After", "0"), ("RateLimit-Remaining", "0")), o => o.MaxRetries = 0);
        var e = await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<JsonElement>("getSite", new Request().WithPath("siteId", "s1")));
        Assert.Equal(kind, e.Kind);
        Assert.Equal(status, e.Status);
        Assert.Equal("some-slug", e.Code);
        Assert.Equal(0, e.RateLimitRemaining);
        Assert.DoesNotContain(Key, e.Message);
    }

    [Fact]
    public async Task NonProblemBodyIsSynthesized()
    {
        var (c, _, _) = Make((_, _) => Reply(502, "<html>bad gateway</html>"), o => o.MaxRetries = 0);
        var e = await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<JsonElement>("getSite", new Request().WithPath("siteId", "s1")));
        Assert.Equal("HTTP 502", e.Problem.GetProperty("title").GetString());
        Assert.Null(e.Code);
    }

    [Fact]
    public async Task Retries429ThenSucceeds()
    {
        var (c, f, clock) = Make((n, _) => n < 3 ? Reply(429, "{}", ("Retry-After", "2")) : Reply(200, "{\"id\":\"s1\"}"));
        await c.CallAsync<JsonElement>("getSite", new Request().WithPath("siteId", "s1"));
        Assert.Equal(3, f.Seen.Count);
        Assert.Equal(2, clock.Slept.Count);
        // Retry-After plus up to 25% jitter
        Assert.All(clock.Slept, d => Assert.InRange(d, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5)));
    }

    [Fact]
    public async Task RetryAfterBeyondCapIsSurfaced()
    {
        var (c, f, clock) = Make((_, _) => Reply(429, "{}", ("Retry-After", "120")));
        var e = await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<JsonElement>("getSite", new Request().WithPath("siteId", "s1")));
        Assert.Single(f.Seen);
        Assert.Equal(TimeSpan.FromSeconds(120), e.RetryAfter);
        Assert.Empty(clock.Slept);
    }

    [Fact]
    public async Task KeyedPostIsRetriedWithOneKey()
    {
        var (c, f, _) = Make((_, _) => Reply(503, "{}"), o => o.MaxRetries = 2);
        await Assert.ThrowsAsync<ApiException>(() => c.WarmRuns.CreateAsync("s1", new WarmRunCreate()));
        Assert.Equal(3, f.Seen.Count);
        var keys = f.Seen.Select(s => s.Headers["Idempotency-Key"]).Distinct().ToList();
        Assert.Single(keys);
        Assert.True(Guid.TryParse(keys[0], out _));
    }

    [Fact]
    public async Task GivenIdempotencyKeyIsSent()
    {
        var (c, f, _) = Make((_, _) => Reply(202, "{\"id\":\"run1\"}"));
        await c.CallAsync<JsonElement>("createWarmRun", new Request().WithPath("siteId", "s1").WithBody(new WarmRunCreate()).WithIdempotencyKey("mine"));
        Assert.Equal("mine", f.Seen[0].Headers["Idempotency-Key"]);
        Assert.Equal("{}", f.Seen[0].Body);
    }

    [Fact]
    public async Task ConnectionErrorHasNoKeyOrCause()
    {
        var (c, f, _) = Make((_, _) => throw new HttpRequestException("boom https://api.example.test/v1 " + Key), o => o.MaxRetries = 1);
        var e = await Assert.ThrowsAsync<ConnectionException>(() => c.CallAsync<JsonElement>("getSite", new Request().WithPath("siteId", "s1")));
        Assert.Equal(2, f.Seen.Count);
        Assert.DoesNotContain(Key, e.Message);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task PaginationFollowsCursorAndDetectsStall()
    {
        var (c, _, _) = Make((n, _) => n == 1
            ? Reply(200, "{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}],\"page\":{\"has_more\":true,\"next_cursor\":\"c1\"}}")
            : Reply(200, "{\"data\":[{\"id\":\"c\"}],\"page\":{\"has_more\":false,\"next_cursor\":null}}"));
        var ids = new List<string>();
        await foreach (var s in c.ItemsAsync<JsonElement>("listSites")) ids.Add(s.GetProperty("id").GetString()!);
        Assert.Equal(new[] { "a", "b", "c" }, ids);

        var (stuck, _, _) = Make((_, _) => Reply(200, "{\"data\":[{\"id\":\"a\"}],\"page\":{\"has_more\":true,\"next_cursor\":\"c1\"}}"));
        var e = await Assert.ThrowsAsync<SiteWarmException>(async () => { await foreach (var _ in stuck.ItemsAsync<JsonElement>("listSites")) { } });
        Assert.Contains("did not advance", e.Message);
    }

    private static string RunJson(string status) =>
        "{\"created_at\":\"2026-01-01T00:00:00Z\",\"id\":\"r1\",\"options\":{\"fail_on\":[]},\"outcome\":null,\"site_id\":\"s\",\"status\":\"" + status +
        "\",\"trigger\":{\"type\":\"api\"},\"vantage_points\":[]}";

    [Fact]
    public async Task WaitPollsUntilTerminalAndTimesOutWithoutCancel()
    {
        var statuses = new[] { "queued", "running", "completed" };
        var (c, _, _) = Make((n, _) => Reply(200, RunJson(statuses[Math.Min(n, 3) - 1]), ("Retry-After", "0")));
        var run = await c.WarmRuns.WaitAsync("r1", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1));
        Assert.Equal(RunStatus.Completed, run.Status);

        var (never, f, _) = Make((_, _) => Reply(200, RunJson("running")));
        await Assert.ThrowsAsync<WaitTimeoutException>(() => never.WarmRuns.WaitAsync("r1", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));
        Assert.All(f.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
    }

    [Fact]
    public void Validation()
    {
        foreach (var u in new[] { "http://api.example.test/v1", "https://u:p@api.example.test", "https://x.test/v1?a=1", "api.example.test", "" })
            Assert.Throws<SiteWarmException>(() => new SiteWarmClient(Key, u));
        using (new SiteWarmClient(Key, "http://localhost:8080/v1")) { }
        foreach (var k in new[] { "", "has space", "tab\t" })
        {
            var e = Assert.Throws<SiteWarmException>(() => new SiteWarmClient(k, "https://x.test/v1"));
            Assert.False(k.Length > 0 && e.Message.Contains(k));
        }
        using var c = new SiteWarmClient(Key, "https://x.test/v1");
        Assert.DoesNotContain(Key, c.ToString());
    }

    [Fact]
    public async Task UnknownOperationAndMissingPathParam()
    {
        var (c, _, _) = Make((_, _) => Reply(200, "{}"));
        var e = await Assert.ThrowsAsync<SiteWarmException>(() => c.ExchangeAsync("nope"));
        Assert.Contains("unknown operation", e.Message);
        e = await Assert.ThrowsAsync<SiteWarmException>(() => c.ExchangeAsync("getSite"));
        Assert.Contains("missing path parameter", e.Message);
    }

    [Fact]
    public void RetryAfterParsing()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(7), SiteWarmClient.ParseRetryAfter("7", now));
        Assert.Null(SiteWarmClient.ParseRetryAfter("garbage", now));
        Assert.Null(SiteWarmClient.ParseRetryAfter(null, now));
    }

    [Fact]
    public void OperationTableMatchesSpec()
    {
        var table = typeof(SiteWarmClient).Assembly.GetType("Sitewarm.Sdk.Operations")!
            .GetField("Table", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var ids = ((System.Collections.IDictionary)table).Keys.Cast<string>().ToList();
        Assert.Equal(36, ids.Count);
        var spec = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "openapi.yaml"));
        Assert.DoesNotContain("x-open-question", spec);
        foreach (var id in ids) Assert.Contains("operationId: " + id, spec);
    }
}
