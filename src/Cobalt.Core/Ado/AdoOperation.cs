using System.Text.RegularExpressions;

namespace Cobalt.Core.Ado;

/// <summary>
/// One recorded ADO request for the <c>:log</c> operations view: name, masked route
/// shape, duration, and outcome. Never carries tokens, headers, or full query text — the
/// only way to construct one is <see cref="FromRoute"/>, which always pipes the route
/// through <see cref="RouteShape.Of"/>, so a caller cannot smuggle a raw secret string in.
/// </summary>
public sealed record AdoOperation
{
    public string Name { get; }
    public string RouteShape { get; }
    public TimeSpan Duration { get; }
    public int? Status { get; }
    public DateTimeOffset At { get; }

    private AdoOperation(string name, string routeShape, TimeSpan duration, int? status, DateTimeOffset at)
    {
        Name = name;
        RouteShape = routeShape;
        Duration = duration;
        Status = status;
        At = at;
    }

    /// <summary>Builds an operation from a raw path or absolute URL, redacting it via <see cref="RouteShape.Of"/>.</summary>
    public static AdoOperation FromRoute(string name, string rawPathOrUrl, TimeSpan duration, int? status, DateTimeOffset at) =>
        new(name, global::Cobalt.Core.Ado.RouteShape.Of(rawPathOrUrl), duration, status, at);
}

/// <summary>
/// Reduces a request path (+ optional query) to a stable shape safe to log: numeric IDs
/// and GUID path segments are masked to <c>{id}</c>, and the query is trimmed to
/// <c>api-version</c> only — no token, auth, or other query text ever survives.
/// </summary>
public static partial class RouteShape
{
    // ADO api-version values are "7.2" or "7.2-preview" or "7.2-preview.1" — anything else
    // (e.g. a smuggled `;`-separated segment) is untrusted and the whole query is dropped.
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)*(-preview(\.[0-9]+)?)?$")]
    private static partial Regex ApiVersionPattern();

    public static string Of(string pathAndQuery)
    {
        string path;
        string? query;

        // An absolute URL may carry userinfo (user:PAT@host) and a host — neither may ever
        // reach the log, so only its path + query survive; scheme, userinfo, host, and port
        // are dropped up front rather than string-sliced (a colon/@ in a token could otherwise
        // fool a naive split).
        if (Uri.TryCreate(pathAndQuery, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath.TrimStart('/');
            query = absolute.Query.Length > 1 ? absolute.Query[1..] : null;
        }
        else
        {
            var queryIndex = pathAndQuery.IndexOf('?');
            path = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
            query = queryIndex >= 0 ? pathAndQuery[(queryIndex + 1)..] : null;
        }

        var maskedPath = string.Join('/', path.Split('/').Select(MaskSegment));

        var apiVersion = query is null ? null : ExtractApiVersion(query);
        return apiVersion is null ? maskedPath : $"{maskedPath}?api-version={apiVersion}";
    }

    private static string MaskSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return segment;
        }
        if (Guid.TryParse(segment, out _) || segment.All(char.IsAsciiDigit))
        {
            return "{id}";
        }
        return segment;
    }

    private static string? ExtractApiVersion(string query)
    {
        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("api-version", StringComparison.OrdinalIgnoreCase))
            {
                return ApiVersionPattern().IsMatch(parts[1]) ? parts[1] : null;
            }
        }
        return null;
    }
}
