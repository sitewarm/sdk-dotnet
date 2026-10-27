using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sitewarm.Sdk;
using Xunit;

namespace Sitewarm.Sdk.Tests;

public class HardeningTests
{
    private const string Key = "swk_secret_key";

    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _h;
        public int Calls;
        public List<string> Urls { get; } = new();

        public Scripted(Func<int, HttpResponseMessage> h) => _h = h;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Urls.Add(req.RequestUri!.AbsoluteUri);
            return Task.FromResult(_h(Interlocked.Increment(ref Calls)));
        }
    }

    /// <summary>A body that delivers a few bytes and then fails like a dropped connection.</summary>
    private sealed class BrokenContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("{\"id\"".Select(c => (byte)c).ToArray());
            throw new IOException("connection reset");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private static HttpResponseMessage Ok(string body = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Status(int s, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)s) { Content = new StringContent("{}") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private static SiteWarmClient Make(HttpMessageHandler h, Action<SiteWarmOptions>? cfg = null)
    {
        var o = new SiteWarmOptions { Handler = h, Delay = (_, _) => Task.CompletedTask };
        cfg?.Invoke(o);
        return new SiteWarmClient(Key, "https://api.example.test/v1", o);
    }

    private static Request Site() => new Request().WithPath("siteId", "s1");

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveTimeoutKeepsDefault(int seconds)
    {
        // A zero timeout would cancel the call at once and a negative one would throw.
        var c = Make(new Scripted(_ => Ok("{\"id\":\"s1\"}")), o => o.Timeout = TimeSpan.FromSeconds(seconds));
        var r = await c.CallAsync<JsonElement>("getSite", Site());
        Assert.Equal("s1", r.GetProperty("id").GetString());
    }

    [Fact]
    public async Task NegativeMaxRetriesKeepsDefault()
    {
        var h = new Scripted(_ => Status(503));
        var c = Make(h, o => o.MaxRetries = -1);
        await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<JsonElement>("getSite", Site()));
        Assert.Equal(4, h.Calls);
    }

    [Fact]
    public async Task BrokenBodyIsRetriedOnRepeatableCalls()
    {
        var h = new Scripted(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() } : Ok("{\"id\":\"s1\"}"));
        var r = await Make(h).CallAsync<JsonElement>("getSite", Site());
        Assert.Equal("s1", r.GetProperty("id").GetString());
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task BrokenBodyOnDeleteIsNotRetriedButAfter429Is()
    {
        var h = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() });
        await Assert.ThrowsAsync<ConnectionException>(() => Make(h, o => o.MaxRetries = 2).CallAsync<JsonElement>("deleteSite", Site()));
        Assert.Equal(1, h.Calls);

        var h2 = new Scripted(n => n == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new BrokenContent() }
            : Ok());
        await Make(h2).CallAsync<JsonElement>("deleteSite", Site());
        Assert.Equal(2, h2.Calls);
    }

    [Fact]
    public async Task OversizedBodyIsTypedAndNotRetried()
    {
        var h = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[SiteWarmClient.MaxBodyBytes + 1]) });
        await Assert.ThrowsAsync<ResponseTooLargeException>(() => Make(h).CallAsync<JsonElement>("getSite", Site()));
        Assert.Equal(1, h.Calls);

        // exactly the limit is still read
        var ok = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Enumerable.Repeat((byte)' ', SiteWarmClient.MaxBodyBytes).ToArray()) });
        var r = await Make(ok).ExchangeAsync("getSite", Site());
        Assert.Equal(SiteWarmClient.MaxBodyBytes, r.Body.Length);
    }

    [Theory]
    [InlineData("getSite", 3)]
    [InlineData("replaceUrlSet", 3)]
    [InlineData("deleteSite", 1)]
    [InlineData("updateSite", 1)]
    public async Task OnlyRepeatableMethodsRetryServerErrors(string op, int want)
    {
        var h = new Scripted(_ => Status(503));
        var req = Site().WithPath("urlSetId", "u1");
        await Assert.ThrowsAsync<ApiException>(() => Make(h, o => o.MaxRetries = 2).CallAsync<JsonElement>(op, req));
        Assert.Equal(want, h.Calls);
    }

    [Fact]
    public async Task Status429IsRetriedForEveryMethod()
    {
        var h = new Scripted(n => n < 3 ? Status(429, ("Retry-After", "0")) : Ok());
        await Make(h).CallAsync<JsonElement>("deleteSite", Site());
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task PaginationErrorsOnRepeatedCursorAndAtPageCap()
    {
        var cyc = new Scripted(n => Ok("{\"data\":[{\"id\":\"x\"}],\"page\":{\"has_more\":true,\"next_cursor\":\"" + (n % 2 == 1 ? "b" : "a") + "\"}}"));
        var e = await Assert.ThrowsAsync<SiteWarmException>(async () =>
        {
            await foreach (var _ in Make(cyc).ItemsAsync<JsonElement>("listSites")) { }
        });
        Assert.Contains("did not advance", e.Message);

        var endless = new Scripted(n => Ok("{\"data\":[],\"page\":{\"has_more\":true,\"next_cursor\":\"c" + n + "\"}}"));
        var cap = await Assert.ThrowsAsync<SiteWarmException>(async () =>
        {
            await foreach (var _ in Make(endless).ItemsAsync<JsonElement>("listSites", null, default, 5)) { }
        });
        Assert.Contains("stopped after 5 pages", cap.Message);
        Assert.Equal(5, endless.Calls);
    }

    [Fact]
    public async Task ListLimitIsValidatedBeforeSending()
    {
        var h = new Scripted(_ => Ok("{\"data\":[],\"page\":{\"has_more\":false}}"));
        var c = Make(h);
        foreach (var bad in new[] { "-1", "101", "x", "1.5", "+5" })
            await Assert.ThrowsAsync<SiteWarmException>(() => c.CallAsync<JsonElement>("listSites", new Request().WithQuery("limit", bad)));
        Assert.Equal(0, h.Calls);
        foreach (var ok in new[] { "1", "100", "0", "", null })
            await c.CallAsync<JsonElement>("listSites", new Request().WithQuery("limit", ok));
        Assert.Equal(new[]
        {
            "https://api.example.test/v1/sites?limit=1", "https://api.example.test/v1/sites?limit=100",
            "https://api.example.test/v1/sites", "https://api.example.test/v1/sites", "https://api.example.test/v1/sites",
        }, h.Urls);
    }

    [Fact]
    public async Task KeyIsInNoDefaultDump()
    {
        var o = new SiteWarmOptions { Handler = new Scripted(_ => Status(401)), MaxRetries = 0 };
        var c = new SiteWarmClient(Key, "https://api.example.test/v1", o);
        var e = await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<JsonElement>("getSite", Site()));
        foreach (var dump in new[]
        {
            c.ToString(), $"{c}", JsonSerializer.Serialize(c), JsonSerializer.Serialize(o), o.ToString() ?? "",
            c.Sites.ToString() ?? "", c.WarmRuns.ToString() ?? "", Site().ToString() ?? "", e.ToString(), e.Message,
        })
            Assert.DoesNotContain(Key, dump);
    }

    [Fact]
    public void SuppliedHandlersDoNotFollowRedirects()
    {
        var hc = new HttpClientHandler { AllowAutoRedirect = true };
        new SiteWarmClient(Key, "https://api.example.test/v1", new SiteWarmOptions { Handler = hc }).Dispose();
        Assert.False(hc.AllowAutoRedirect);

        var sh = new SocketsHttpHandler { AllowAutoRedirect = true };
        var chain = new Wrapper { InnerHandler = sh };
        new SiteWarmClient(Key, "https://api.example.test/v1", new SiteWarmOptions { Handler = chain }).Dispose();
        Assert.False(sh.AllowAutoRedirect);
    }

    private sealed class Wrapper : DelegatingHandler { }

    // ---- real sockets: a loopback listener exercises the default and the supplied handlers end to end ----

    private static (string Base, HttpListener Listener, List<string> Hits) Serve(Func<HttpListenerContext, int, Task> handle)
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var hits = new List<string>();
        _ = Task.Run(async () =>
        {
            int n = 0;
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { return; }
                lock (hits) hits.Add(ctx.Request.Url!.AbsolutePath);
                try { await handle(ctx, ++n); }
                catch { /* the client went away */ }
            }
        });
        return ($"http://127.0.0.1:{port}/v1", listener, hits);
    }

    private static SiteWarmClient Real(string baseUrl, Action<SiteWarmOptions>? cfg = null)
    {
        var o = new SiteWarmOptions { Delay = (_, _) => Task.CompletedTask };
        cfg?.Invoke(o);
        return new SiteWarmClient(Key, baseUrl, o);
    }

    [Fact]
    public async Task RealRedirectIsNotFollowedEvenWithASuppliedHandler()
    {
        var (url, l, hits) = Serve((ctx, _) =>
        {
            if (ctx.Request.Url!.AbsolutePath.EndsWith("/other")) { ctx.Response.StatusCode = 200; ctx.Response.Close(); return Task.CompletedTask; }
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = url0(ctx) + "/other";
            ctx.Response.Close();
            return Task.CompletedTask;

            static string url0(HttpListenerContext c) => $"http://{c.Request.Url!.Authority}";
        });
        using var _ = l;
        foreach (HttpMessageHandler? handler in new HttpMessageHandler?[] { null, new HttpClientHandler() })
        {
            var c = Real(url, o => { o.MaxRetries = 0; o.Handler = handler; });
            var e = await Assert.ThrowsAsync<ApiException>(() => c.CallAsync<JsonElement>("getSite", Site()));
            Assert.Equal(302, e.Status);
        }
        lock (hits) Assert.DoesNotContain(hits, p => p.EndsWith("/other"));
    }

    [Fact]
    public async Task RealDroppedBodyIsRetriedForGetAndNotForDelete()
    {
        var (url, l, hits) = Serve(async (ctx, n) =>
        {
            if (ctx.Request.HttpMethod == "GET" && n > 1)
            {
                var ok = "{\"id\":\"s1\"}"u8.ToArray();
                ctx.Response.ContentLength64 = ok.Length;
                await ctx.Response.OutputStream.WriteAsync(ok);
                ctx.Response.Close();
                return;
            }
            ctx.Response.ContentLength64 = 100;
            await ctx.Response.OutputStream.WriteAsync("{\"id\""u8.ToArray());
            await ctx.Response.OutputStream.FlushAsync();
            ctx.Response.Abort();
        });
        using var _ = l;
        var r = await Real(url).CallAsync<JsonElement>("getSite", Site());
        Assert.Equal("s1", r.GetProperty("id").GetString());
        int afterGet;
        lock (hits) afterGet = hits.Count;
        Assert.Equal(2, afterGet);

        await Assert.ThrowsAsync<ConnectionException>(() => Real(url, o => o.MaxRetries = 2).CallAsync<JsonElement>("deleteSite", Site()));
        lock (hits) Assert.Equal(afterGet + 1, hits.Count);
    }

    [Fact]
    public async Task RealOversizedBodyIsTyped()
    {
        var (url, l, hits) = Serve(async (ctx, _) =>
        {
            ctx.Response.SendChunked = true;
            var chunk = new byte[1 << 20];
            for (int i = 0; i < 40; i++) await ctx.Response.OutputStream.WriteAsync(chunk);
            ctx.Response.Close();
        });
        using var _ = l;
        await Assert.ThrowsAsync<ResponseTooLargeException>(() => Real(url).CallAsync<JsonElement>("getSite", Site()));
        lock (hits) Assert.Single(hits);
    }
}
