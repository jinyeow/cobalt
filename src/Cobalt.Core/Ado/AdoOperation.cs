namespace Cobalt.Core.Ado;

/// <summary>
/// One recorded ADO request for the <c>:log</c> operations view: name, masked route
/// shape, duration, and outcome. Never carries tokens, headers, or full query text —
/// see <see cref="RouteShape"/>.
/// </summary>
public sealed record AdoOperation(string Name, string RouteShape, TimeSpan Duration, int? Status, DateTimeOffset At);

/// <summary>
/// Reduces a request path (+ optional query) to a stable shape safe to log: numeric IDs
/// and GUID path segments are masked to <c>{id}</c>, and the query is trimmed to
/// <c>api-version</c> only — no token, auth, or other query text ever survives.
/// </summary>
public static class RouteShape
{
    public static string Of(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var path = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        var query = queryIndex >= 0 ? pathAndQuery[(queryIndex + 1)..] : null;

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
                return parts[1];
            }
        }
        return null;
    }
}
