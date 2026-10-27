using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sitewarm.Sdk;

/// <summary>Client options. All are optional.</summary>
public sealed class SiteWarmOptions
{
    /// <summary>Retries after the first attempt. Default 3.</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>First backoff step. Default 500 ms.</summary>
    public TimeSpan RetryBase { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Backoff cap, also the longest server-provided Retry-After that is waited out. Default 30 s.</summary>
    public TimeSpan RetryMax { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Per-attempt timeout. Default 30 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Default <c>sitewarm-dotnet/&lt;version&gt; (+https://github.com/sitewarm/sdk-dotnet)</c>; null or empty keeps the default.</summary>
    public string UserAgent { get; set; } = SiteWarmClient.DefaultUserAgent;
    /// <summary>Custom handler (tests, proxies). The default never follows redirects, so the key never goes to another origin.</summary>
    public HttpMessageHandler? Handler { get; set; }
    /// <summary>Replaces <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; for tests.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
    /// <summary>Replaces the clock used by <c>WaitAsync</c>; for tests.</summary>
    public Func<DateTimeOffset>? Clock { get; set; }
}
