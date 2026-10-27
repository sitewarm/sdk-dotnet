using System;
using System.Text.Json;

namespace Sitewarm.Sdk;

/// <summary>Base class of every error the SDK throws. Messages never contain the API key.</summary>
public class SiteWarmException : Exception
{
    public SiteWarmException(string message) : base(message) { }
}

/// <summary>The request did not produce an HTTP response. No cause is attached: transport errors can carry the URL.</summary>
public sealed class ConnectionException : SiteWarmException
{
    public bool Timeout { get; }

    public ConnectionException(string message, bool timeout = false) : base(message) => Timeout = timeout;
}

/// <summary>The wait ran out. The run itself is not canceled.</summary>
public sealed class WaitTimeoutException : SiteWarmException
{
    public WaitTimeoutException(string message) : base(message) { }
}

/// <summary>Coarse class of an API error, by HTTP status.</summary>
public enum ApiErrorKind { Authentication, Permission, NotFound, Conflict, Validation, RateLimited, Server, Other }

/// <summary>A non-2xx answer. <see cref="Problem"/> is the RFC 9457 body (or a synthesized one when the body was not a problem).</summary>
public sealed class ApiException : SiteWarmException
{
    public int Status { get; }
    public JsonElement Problem { get; }
    /// <summary>Last path segment of the problem <c>type</c> (or the <c>code</c> member), e.g. <c>idempotency-key-reused</c>.</summary>
    public string? Code { get; }
    public TimeSpan? RetryAfter { get; }
    public int? RateLimitRemaining { get; }

    public ApiErrorKind Kind => Status switch
    {
        401 => ApiErrorKind.Authentication,
        403 => ApiErrorKind.Permission,
        404 => ApiErrorKind.NotFound,
        409 => ApiErrorKind.Conflict,
        422 => ApiErrorKind.Validation,
        429 => ApiErrorKind.RateLimited,
        >= 500 => ApiErrorKind.Server,
        _ => ApiErrorKind.Other,
    };

    internal ApiException(int status, JsonElement problem, string? code, TimeSpan? retryAfter, int? remaining)
        : base(Describe(status, problem))
    {
        Status = status;
        Problem = problem;
        Code = code;
        RetryAfter = retryAfter;
        RateLimitRemaining = remaining;
    }

    private static string Describe(int status, JsonElement p)
    {
        string title = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()! : "HTTP " + status;
        string detail = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
            ? ": " + d.GetString() : "";
        return $"sitewarm API {status} {title}{detail}";
    }
}
