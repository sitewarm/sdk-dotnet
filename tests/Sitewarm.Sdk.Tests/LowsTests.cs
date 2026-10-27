using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Sitewarm.Sdk;
using Xunit;

namespace Sitewarm.Sdk.Tests;

public class LowsTests
{
    private const string Key = "swk_secret_key";

    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _h;
        public int Calls;
        public List<string> UserAgents { get; } = new();
        public List<string> Urls { get; } = new();

        public Scripted(Func<int, HttpRequestMessage, HttpResponseMessage> h) => _h = h;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            UserAgents.Add(string.Join(" ", req.Headers.GetValues("User-Agent")));
            Urls.Add(req.RequestUri!.AbsoluteUri);
            return Task.FromResult(_h(Interlocked.Increment(ref Calls), req));
        }
    }

    private static HttpResponseMessage Reply(int status, string body = "{}", params (string, string)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private static HttpResponseMessage Problem(int status, string slug) =>
        Reply(status, "{\"type\":\"https://x.test/problems/" + slug + "\",\"title\":\"c\",\"status\":" + status + "}");

    private static SiteWarmClient Make(HttpMessageHandler h, List<TimeSpan>? slept = null, Action<SiteWarmOptions>? cfg = null)
    {
        var o = new SiteWarmOptions
        {
            Handler = h,
            RetryBase = TimeSpan.Zero,
            RetryMax = TimeSpan.Zero,
            Delay = (d, _) => { slept?.Add(d); return Task.CompletedTask; },
        };
        cfg?.Invoke(o);
        return new SiteWarmClient(Key, "https://api.example.test/v1", o);
    }

    private static Request Site() => new Request().WithPath("siteId", "s1");

    [Theory]
    [InlineData("conflict", 3)]
    [InlineData("idempotency-key-reused", 1)]
    public async Task ConflictOnKeyedPostIsRetriedButKeyReusedIsNot(string slug, int calls)
    {
        var h = new Scripted((_, _) => Problem(409, slug));
        var c = Make(h, null, o => o.MaxRetries = 2);
        var e = await Assert.ThrowsAsync<ApiException>(() =>
            c.CallAsync<System.Text.Json.JsonElement>("createSite", new Request().WithBody(new { hostname = "example.com" })));
        Assert.Equal(slug, e.Code);
        Assert.Equal(calls, h.Calls);
    }

    [Fact]
    public async Task ConflictOnUnkeyedOperationIsNotRetried()
    {
        var h = new Scripted((_, _) => Problem(409, "conflict"));
        var c = Make(h, null, o => o.MaxRetries = 2);
        await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<System.Text.Json.JsonElement>("deleteSite", Site()));
        Assert.Equal(1, h.Calls);
    }

    private sealed class Throwing : HttpMessageHandler
    {
        public int Calls;
        private readonly Func<Exception> _make;
        public Throwing(Func<Exception> make) => _make = make;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw _make();
        }
    }

    [Theory]
    [InlineData("tls", 1)]
    [InlineData("tls-inner", 1)]
    [InlineData("unknown-host", 1)]
    [InlineData("dns-not-found-socket", 1)]
    [InlineData("dns-try-again", 3)]
    [InlineData("reset", 3)]
    [InlineData("refused", 3)]
    public async Task NetworkErrorClasses(string kind, int calls)
    {
        Func<Exception> make = kind switch
        {
            "tls" => () => new HttpRequestException(HttpRequestError.SecureConnectionError, "x"),
            "tls-inner" => () => new HttpRequestException("x", new AuthenticationException("The remote certificate is invalid")),
            "unknown-host" => () => new HttpRequestException(HttpRequestError.NameResolutionError, "x"),
            "dns-not-found-socket" => () => new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)),
            "dns-try-again" => () => new HttpRequestException(HttpRequestError.NameResolutionError, "x", new SocketException((int)SocketError.TryAgain)),
            "reset" => () => new HttpRequestException(HttpRequestError.ConnectionError, "x", new System.IO.IOException("reset")),
            _ => () => new HttpRequestException(HttpRequestError.ConnectionError, "x", new SocketException((int)SocketError.ConnectionRefused)),
        };
        var h = new Throwing(make);
        var c = Make(h, null, o => o.MaxRetries = 2);
        await Assert.ThrowsAsync<ConnectionException>(() => c.CallAsync<System.Text.Json.JsonElement>("getSite", Site()));
        Assert.Equal(calls, h.Calls);
    }

    [Fact]
    public async Task InvalidHeaderValueIsRejectedBeforeSendingAndNotEchoed()
    {
        var h = new Scripted((_, _) => Reply(200));
        var c = Make(h, null, o => o.UserAgent = "evil\r\nX-Injected: 1");
        var e = await Assert.ThrowsAsync<SiteWarmException>(() => c.CallAsync<System.Text.Json.JsonElement>("getSite", Site()));
        Assert.DoesNotContain("Injected", e.Message);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task RetryAfterZeroWaitsTheBaseWithJitter()
    {
        var slept = new List<TimeSpan>();
        var h = new Scripted((n, _) => n == 1 ? Reply(429, "{}", ("Retry-After", "0")) : Reply(200, "{\"id\":\"s1\"}"));
        var c = Make(h, slept, o => { o.RetryBase = TimeSpan.FromMilliseconds(200); o.RetryMax = TimeSpan.FromSeconds(5); });
        await c.CallAsync<System.Text.Json.JsonElement>("getSite", Site());
        var d = Assert.Single(slept);
        Assert.InRange(d, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task PastHttpDateIsFlooredToo()
    {
        var slept = new List<TimeSpan>();
        var h = new Scripted((n, _) => n == 1 ? Reply(429, "{}", ("Retry-After", "Mon, 01 Jan 2001 00:00:00 GMT")) : Reply(200, "{\"id\":\"s1\"}"));
        var c = Make(h, slept, o => { o.RetryBase = TimeSpan.FromMilliseconds(200); o.RetryMax = TimeSpan.FromSeconds(5); });
        await c.CallAsync<System.Text.Json.JsonElement>("getSite", Site());
        Assert.True(Assert.Single(slept) >= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task RetryAfterAboveTheCapIsSurfacedNotSlept()
    {
        var slept = new List<TimeSpan>();
        var h = new Scripted((_, _) => Reply(429, "{}", ("Retry-After", "60")));
        var c = Make(h, slept, o => { o.RetryBase = TimeSpan.FromMilliseconds(200); o.RetryMax = TimeSpan.FromSeconds(5); });
        var e = await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<System.Text.Json.JsonElement>("getSite", Site()));
        Assert.Equal(TimeSpan.FromSeconds(60), e.RetryAfter);
        Assert.Equal(1, h.Calls);
        Assert.Empty(slept);
    }

    [Fact]
    public async Task NonJsonSuccessIsATypedDecodeError()
    {
        var c = Make(new Scripted((_, _) => Reply(200, "<html>secret-proxy-page</html>")));
        var e = await Assert.ThrowsAsync<DecodeException>(() => c.CallAsync<System.Text.Json.JsonElement>("getSite", Site()));
        Assert.Equal(200, e.Status);
        Assert.IsAssignableFrom<SiteWarmException>(e);
        Assert.DoesNotContain("secret-proxy-page", e.Message);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task UserAgentCarriesVersionAndUrlAndEmptyKeepsTheDefault()
    {
        var def = "sitewarm-dotnet/" + SiteWarmClient.Version + " (+https://github.com/sitewarm/sdk-dotnet)";
        Assert.Equal("0.1.0-preview", SiteWarmClient.Version);
        var seen = new List<string>();
        foreach (var ua in new string?[] { null, "", "mine/1" })
        {
            var h = new Scripted((_, _) => Reply(200));
            var c = Make(h, null, o => { if (ua != null) o.UserAgent = ua; });
            await c.CallAsync<System.Text.Json.JsonElement>("getSite", Site());
            seen.Add(h.UserAgents[0]);
        }
        Assert.Equal(new[] { def, def, "mine/1" }, seen);
    }

    [Fact]
    public async Task BasePathKeepsPercentEncoding()
    {
        var h = new Scripted((_, _) => Reply(200));
        var c = new SiteWarmClient(Key, "https://x.test/a%2Fb/v1", new SiteWarmOptions { Handler = h });
        await c.CallAsync<System.Text.Json.JsonElement>("getSite", Site());
        Assert.Equal("https://x.test/a%2Fb/v1/sites/s1", h.Urls[0]);
    }
}
