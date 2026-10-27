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

- **Auth**: `Authorization: Bearer <key>`. The key is not in `ToString()`, exception messages or logs (the SDK never logs). The base URL must be https (http only for localhost) with no credentials or query; redirects are not followed, so the key never goes to another origin.
- **No default base URL**: pass the API host you were given.
- **Retries**: up to 3 on `429`, `5xx` (not `501`) and network errors, exponential backoff with equal jitter, capped at 30 s. `Retry-After` is honored; if it exceeds the cap the error is thrown instead of sleeping. `429` is always retried; `5xx` and network errors only when repeating is safe (not a POST, or a POST with an `Idempotency-Key`).
- **Idempotency**: every POST that accepts `Idempotency-Key` sends one (random GUID unless you pass one), generated once per call and reused by its retries, so a retried `WarmRuns.CreateAsync` never starts two runs.
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
