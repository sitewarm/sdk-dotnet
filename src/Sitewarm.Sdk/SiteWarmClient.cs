using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Sitewarm.Sdk.Client;
using Sitewarm.Sdk.Model;

namespace Sitewarm.Sdk;

/// <summary>A decoded response with its transport data.</summary>
public sealed record SiteWarmResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Client for the sitewarm API (pre-release). Thread-safe. Every operation of the spec is reachable by its
/// operationId through <see cref="CallAsync{T}"/>; <see cref="Sites"/> and <see cref="WarmRuns"/> are typed conveniences.
/// </summary>
public sealed class SiteWarmClient : IDisposable
{
    private static readonly Regex KeyPattern = new("^[\\x21-\\x7e]+$", RegexOptions.Compiled);
    private static readonly Regex PathParam = new("\\{(\\w+)\\}", RegexOptions.Compiled);
    private static readonly Regex Slug = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    internal static readonly JsonSerializerOptions Json = BuildJsonOptions();

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly SiteWarmOptions _o;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;

    public SitesApi Sites { get; }
    public WarmRunsApi WarmRuns { get; }

    public SiteWarmClient(string apiKey, string baseUrl, SiteWarmOptions? options = null)
    {
        // Messages below never echo the key.
        if (string.IsNullOrEmpty(apiKey) || !KeyPattern.IsMatch(apiKey))
            throw new SiteWarmException("apiKey must be non-empty printable ASCII without whitespace");
        _apiKey = apiKey;
        _baseUrl = Normalize(baseUrl);
        _o = options ?? new SiteWarmOptions();
        _http = new HttpClient(_o.Handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: _o.Handler == null)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        _delay = _o.Delay ?? ((d, ct) => Task.Delay(d, ct));
        _clock = _o.Clock ?? (() => DateTimeOffset.UtcNow);
        Sites = new SitesApi(this);
        WarmRuns = new WarmRunsApi(this);
    }

    /// <summary>Never contains the key.</summary>
    public override string ToString() => "SiteWarmClient{baseUrl=" + _baseUrl + "}";

    public void Dispose() => _http.Dispose();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new DateTimeJsonConverter());
        o.Converters.Add(new DateTimeNullableJsonConverter());
        o.Converters.Add(new DateOnlyJsonConverter());
        o.Converters.Add(new DateOnlyNullableJsonConverter());
        // The generator registers its model and enum converters at host start-up; there is no host here.
        foreach (var t in typeof(SiteWarmClient).Assembly.GetTypes())
        {
            if (t.Namespace == "Sitewarm.Sdk.Model" && !t.IsAbstract && typeof(JsonConverter).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) != null)
                o.Converters.Add((JsonConverter)Activator.CreateInstance(t)!);
        }
        return o;
    }

    private static string Normalize(string raw)
    {
        const string hint = "baseUrl must be an absolute URL such as https://<host>/v1";
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) throw new SiteWarmException(hint);
        bool loopback = u.Host is "localhost" or "127.0.0.1" or "[::1]";
        if (u.Scheme != "https" && !(u.Scheme == "http" && loopback))
            throw new SiteWarmException("baseUrl must use https (http is only allowed for localhost)");
        if (!string.IsNullOrEmpty(u.UserInfo)) throw new SiteWarmException("baseUrl must not contain credentials");
        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || raw.Contains('?') || raw.Contains('#'))
            throw new SiteWarmException("baseUrl must not contain a query or fragment");
        return u.GetLeftPart(UriPartial.Authority) + u.AbsolutePath.TrimEnd('/');
    }

    /// <summary>Calls one operation by operationId and maps the body to <typeparamref name="T"/> (a generated model, or <see cref="JsonElement"/>).</summary>
    public async Task<T?> CallAsync<T>(string operationId, Request? request = null, CancellationToken ct = default)
    {
        var r = await ExchangeAsync(operationId, request, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(r.Body)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(r.Body, Json);
        }
        catch (JsonException)
        {
            throw new SiteWarmException("response does not match " + typeof(T).Name);
        }
    }

    public async Task<SiteWarmResponse> ExchangeAsync(string operationId, Request? request = null, CancellationToken ct = default)
    {
        request ??= new Request();
        if (!Operations.Table.TryGetValue(operationId, out var op)) throw new SiteWarmException($"unknown operation \"{operationId}\"");
        var url = new StringBuilder(_baseUrl).Append(Expand(op.Path, request.PathParams));
        var query = request.QueryParams.Where(kv => kv.Value != null).ToList();
        if (query.Count > 0)
            url.Append('?').Append(string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!))));
        string? body = null;
        if (request.Body != null)
        {
            try { body = JsonSerializer.Serialize(request.Body, request.Body.GetType(), Json); }
            catch (Exception e) when (e is JsonException or NotSupportedException) { throw new SiteWarmException("could not encode the request body"); }
        }
        bool sendsKey = op.Idempotency != Idempotency.None && op.Method == "POST";
        // One key per call, reused by every retry, so a retried POST never starts a second run.
        string? key = sendsKey ? (request.IdempotencyKey ?? Guid.NewGuid().ToString()) : null;
        // 429 = not processed. 5xx and network errors may have been, so retry them only when repeating is safe.
        bool retryAmbiguous = op.Method != "POST" || sendsKey;

        for (int attempt = 0; ; attempt++)
        {
            bool last = attempt >= _o.MaxRetries;
            using var msg = new HttpRequestMessage(new HttpMethod(op.Method), url.ToString());
            msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            msg.Headers.TryAddWithoutValidation("Accept", "application/json, application/problem+json");
            msg.Headers.TryAddWithoutValidation("User-Agent", _o.UserAgent);
            if (key != null) msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            if (body != null) msg.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

            HttpResponseMessage resp;
            string text;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_o.Timeout);
            try
            {
                resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                text = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (!last && retryAmbiguous)
                {
                    await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                // The cause is dropped on purpose: transport errors can carry the request URL.
                bool timedOut = e is OperationCanceledException;
                throw new ConnectionException(timedOut ? "request timed out" : "request failed", timedOut);
            }

            using (resp)
            {
                int status = (int)resp.StatusCode;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
                foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
                if (status is >= 200 and < 300) return new SiteWarmResponse(status, headers, text);

                var retryAfter = ParseRetryAfter(headers.GetValueOrDefault("Retry-After"), _clock());
                bool retryable = status == 429 || (status >= 500 && status != 501 && retryAmbiguous);
                if (retryable && !last)
                {
                    var wait = retryAfter ?? Backoff(attempt);
                    if (wait <= _o.RetryMax) // a longer server-provided wait is surfaced, not ignored
                    {
                        await _delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }
                }
                throw ApiError(status, text, headers, retryAfter);
            }
        }
    }

    private static ApiException ApiError(int status, string text, Dictionary<string, string> headers, TimeSpan? retryAfter)
    {
        JsonElement p;
        try
        {
            using var doc = JsonDocument.Parse(text);
            p = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : Synthesized(status);
        }
        catch (JsonException)
        {
            p = Synthesized(status);
        }
        string? code = p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (code == null && p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var s = t.GetString()!.TrimEnd('/');
            var slug = s[(s.LastIndexOf('/') + 1)..];
            if (Slug.IsMatch(slug)) code = slug;
        }
        int? remaining = headers.TryGetValue("RateLimit-Remaining", out var rl) && int.TryParse(rl.Trim(), out var n) && n >= 0 ? n : null;
        return new ApiException(status, p, code, retryAfter, remaining);
    }

    private static JsonElement Synthesized(int status)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "about:blank", title = "HTTP " + status, status }));
        return doc.RootElement.Clone();
    }

    internal static TimeSpan? ParseRetryAfter(string? v, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim();
        if (s.All(char.IsAsciiDigit) && long.TryParse(s, out var secs)) return TimeSpan.FromSeconds(secs);
        if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
        {
            var d = at - now;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
        return null;
    }

    private TimeSpan Backoff(int attempt)
    {
        double ceil = Math.Min(_o.RetryMax.TotalMilliseconds, _o.RetryBase.TotalMilliseconds * Math.Pow(2, attempt));
        double jitter = RandomNumberGenerator.GetInt32(0, 1_000_000) / 1_000_000.0;
        return TimeSpan.FromMilliseconds(ceil / 2 + jitter * ceil / 2);
    }

    private static string Expand(string template, IReadOnlyDictionary<string, string> values) =>
        PathParam.Replace(template, m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var v) || v.Length == 0)
                throw new SiteWarmException($"missing path parameter \"{m.Groups[1].Value}\"");
            return Uri.EscapeDataString(v);
        });

    /// <summary>Iterates every item of a list operation across pages. Cursors are opaque.</summary>
    public async IAsyncEnumerable<T> ItemsAsync<T>(string operationId, Request? request = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        request ??= new Request();
        string? cursor = request.QueryParams.GetValueOrDefault("cursor");
        for (int pages = 0; pages < 10_000; pages++)
        {
            var r = cursor == null ? request : request.WithQuery("cursor", cursor);
            var res = await ExchangeAsync(operationId, r, ct).ConfigureAwait(false);
            List<T> items = new();
            string? next = null;
            bool more = false;
            using (var doc = JsonDocument.Parse(res.Body))
            {
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var el in data.EnumerateArray())
                    {
                        try { items.Add(el.Deserialize<T>(Json)!); }
                        catch (JsonException) { throw new SiteWarmException("response does not match " + typeof(T).Name); }
                    }
                if (doc.RootElement.TryGetProperty("page", out var page))
                {
                    more = page.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                    if (page.TryGetProperty("next_cursor", out var nc) && nc.ValueKind == JsonValueKind.String) next = nc.GetString();
                }
            }
            foreach (var i in items) yield return i;
            if (!more || string.IsNullOrEmpty(next)) yield break;
            if (next == cursor) throw new SiteWarmException("pagination cursor did not advance");
            cursor = next;
        }
    }

    internal DateTimeOffset Now() => _clock();
    internal Task PauseAsync(TimeSpan d, CancellationToken ct) => _delay(d, ct);
}

/// <summary>Arguments of one operation call. Immutable; the <c>With*</c> methods return copies.</summary>
public sealed class Request
{
    internal IReadOnlyDictionary<string, string> PathParams { get; init; } = new Dictionary<string, string>();
    internal IReadOnlyDictionary<string, string?> QueryParams { get; init; } = new Dictionary<string, string?>();
    internal object? Body { get; init; }
    internal string? IdempotencyKey { get; init; }

    public Request WithPath(string name, string value) => Copy(path: With(PathParams, name, value)!);
    public Request WithQuery(string name, string? value) => Copy(query: With(QueryParams, name, value));
    public Request WithBody(object body) => Copy(body: body);
    public Request WithIdempotencyKey(string? key) => Copy(key: key);

    private static Dictionary<string, T> With<T>(IReadOnlyDictionary<string, T> d, string k, T v)
    {
        var c = d.ToDictionary(kv => kv.Key, kv => kv.Value);
        c[k] = v;
        return c;
    }

    private Request Copy(IReadOnlyDictionary<string, string>? path = null, IReadOnlyDictionary<string, string?>? query = null,
        object? body = null, string? key = null) => new()
    {
        PathParams = path ?? PathParams,
        QueryParams = query ?? QueryParams,
        Body = body ?? Body,
        IdempotencyKey = key ?? IdempotencyKey,
    };
}

public sealed class SitesApi
{
    private readonly SiteWarmClient _c;
    internal SitesApi(SiteWarmClient c) => _c = c;

    public Task<Site?> GetAsync(string siteId, CancellationToken ct = default) =>
        _c.CallAsync<Site>("getSite", new Request().WithPath("siteId", siteId), ct);

    public Task<Site?> CreateAsync(SiteCreate body, CancellationToken ct = default) =>
        _c.CallAsync<Site>("createSite", new Request().WithBody(body), ct);

    public IAsyncEnumerable<Site> AllAsync(CancellationToken ct = default) => _c.ItemsAsync<Site>("listSites", null, ct);
}

public sealed class WarmRunsApi
{
    private readonly SiteWarmClient _c;
    internal WarmRunsApi(SiteWarmClient c) => _c = c;

    /// <summary>Starts a run (202). An Idempotency-Key is always sent (random unless <paramref name="idempotencyKey"/> is given).</summary>
    public Task<WarmRun?> CreateAsync(string siteId, WarmRunCreate body, string? idempotencyKey = null, CancellationToken ct = default) =>
        _c.CallAsync<WarmRun>("createWarmRun",
            new Request().WithPath("siteId", siteId).WithBody(body).WithIdempotencyKey(idempotencyKey), ct);

    public Task<WarmRun?> GetAsync(string runId, CancellationToken ct = default) =>
        _c.CallAsync<WarmRun>("getWarmRun", new Request().WithPath("runId", runId), ct);

    public Task<WarmRun?> CancelAsync(string runId, CancellationToken ct = default) =>
        _c.CallAsync<WarmRun>("cancelWarmRun", new Request().WithPath("runId", runId), ct);

    /// <summary>All results of a run; <paramref name="finalState"/> (null for all) filters by final cache state.</summary>
    public IAsyncEnumerable<Result> ResultsAsync(string runId, string? finalState = null, CancellationToken ct = default) =>
        _c.ItemsAsync<Result>("listWarmRunResults",
            new Request().WithPath("runId", runId).WithQuery("final_state", finalState), ct);

    /// <summary>
    /// Polls until the run is completed, canceled or failed, honoring Retry-After (bounded to 1 s .. 60 s).
    /// Throws <see cref="WaitTimeoutException"/> on timeout and does NOT cancel the run.
    /// </summary>
    public async Task<WarmRun> WaitAsync(string runId, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var deadline = _c.Now() + (timeout ?? TimeSpan.FromMinutes(10));
        var poll = pollInterval ?? TimeSpan.FromSeconds(2);
        while (true)
        {
            var r = await _c.ExchangeAsync("getWarmRun", new Request().WithPath("runId", runId), ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(r.Body);
            var status = doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (status is "completed" or "canceled" or "failed")
                return doc.RootElement.Deserialize<WarmRun>(SiteWarmClient.Json)!;
            var hint = SiteWarmClient.ParseRetryAfter(r.Header("Retry-After"), _c.Now());
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Max(1, (hint ?? poll).TotalSeconds)));
            if (_c.Now() + delay > deadline) throw new WaitTimeoutException("timed out waiting for the run; it was not canceled");
            await _c.PauseAsync(delay, ct).ConfigureAwait(false);
        }
    }
}
