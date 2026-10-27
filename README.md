# sdk-dotnet

.NET client for the sitewarm API. Models in [`src/Sitewarm.Sdk/Generated`](src/Sitewarm.Sdk/Generated) are generated from the OpenAPI 3.1 spec in [`openapi/openapi.yaml`](openapi/openapi.yaml) with [OpenAPI Generator](https://openapi-generator.tech) (System.Text.Json); the client (`SiteWarmClient`) is hand-written on `HttpClient`. .NET 8+. No NuGet dependencies.

**Status: pre-release (`0.1.0-preview`, not on NuGet).** The API and this client can still change. The spec describes the whole planned surface; the service may not offer every operation yet.

```csharp
using Sitewarm.Sdk;
using Sitewarm.Sdk.Model;

using var c = new SiteWarmClient(Environment.GetEnvironmentVariable("SITEWARM_API_KEY")!, "https://<api host>/v1");

var site = await c.Sites.CreateAsync(new SiteCreate("example.com"));
var run = await c.WarmRuns.CreateAsync(site!.Id, new WarmRunCreate());
var done = await c.WarmRuns.WaitAsync(run!.Id, TimeSpan.FromMinutes(10));
await foreach (var r in c.WarmRuns.ResultsAsync(done.Id, "miss"))
    Console.WriteLine(r.Url);
```

A site must be verified before a run is accepted; verification happens outside this API.

## Surface

- `c.Sites` (`GetAsync`, `CreateAsync`, `AllAsync`) and `c.WarmRuns` (`CreateAsync`, `GetAsync`, `CancelAsync`, `ResultsAsync`, `WaitAsync`).
- Every operation of the spec is reachable with `c.CallAsync<T>("<operationId>", new Request().WithPath(...).WithBody(...))`; `c.ItemsAsync<T>(...)` iterates list operations as `IAsyncEnumerable<T>` (cursors are opaque). `T` can be a generated model or `JsonElement`. The operation table is generated and a test checks it against the spec.
- Webhook callbacks are server-to-customer and not covered.

## Behavior

- **Auth**: `Authorization: Bearer <key>`. The key is not in `ToString()`, JSON serialization, exception messages or logs (the SDK never logs). The base URL must be https (http only for localhost) with no credentials or query. Redirects are never followed, so the key never goes to another origin: the default handler has `AllowAutoRedirect = false`, and `AllowAutoRedirect` is forced to `false` on every `HttpClientHandler`/`SocketsHttpHandler` in a handler you supply (a handler already in use that cannot be changed is rejected). A custom handler of another kind that follows redirects by itself cannot be switched off; a followed redirect is detected and rejected, but the header may already have been sent, so such a handler must not follow redirects.
- **No default base URL**: pass the API host you were given.
- **Retries**: up to 3 (`MaxRetries`; a negative value keeps the default) on `429`, `5xx` (not `501`), network errors and a response body that breaks off, exponential backoff with equal jitter, capped at 30 s. `Retry-After` is honored; if it exceeds the cap the error is thrown instead of sleeping. `429` is always retried; `5xx`, network errors and broken bodies only when repeating is safe (`GET`, `HEAD`, `PUT`, `OPTIONS`, or a POST with an `Idempotency-Key`; never `DELETE`/`PATCH`). A `Timeout` <= 0 keeps the default (30 s).
- **Idempotency**: every POST that accepts `Idempotency-Key` sends one (random GUID unless you pass one), generated once per call and reused by its retries, so a retried `WarmRuns.CreateAsync` never starts two runs.
- **Limits**: response bodies over 32 MiB throw `ResponseTooLargeException` (not retried); `ItemsAsync` throws on a repeated cursor or after 10,000 pages (`maxPages`) instead of stopping silently; a `limit` query value outside 1..100 throws before the request (null, empty or `0` = server default).
- **Errors**: `ApiException` (`Status`, `Kind`, RFC 9457 `Problem`, `Code`, `RetryAfter`, `RateLimitRemaining`), `ConnectionException`, and `WaitTimeoutException` from `WaitAsync`, which does not cancel the run. All derive from `SiteWarmException`.
- Options (`SiteWarmOptions`): `MaxRetries`, `RetryBase`, `RetryMax`, `Timeout`, `UserAgent`, `Handler`.

## Development

No local .NET needed; build and test in the official image:

```
docker run --rm -v "$PWD":/work -w /work mcr.microsoft.com/dotnet/sdk:8.0 dotnet test tests/Sitewarm.Sdk.Tests
```

To regenerate after changing `openapi/openapi.yaml` (needs Python 3 with PyYAML and Docker): `sh scripts/generate.sh`. CI fails if the generated sources are out of date.

## License

Apache-2.0, see [LICENSE](LICENSE).
