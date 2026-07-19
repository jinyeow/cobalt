using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cobalt.Core.Ado;

/// <summary>
/// Thin JSON transport over one org's HttpClient: source-generated (de)serialization
/// and translation of ADO error envelopes into <see cref="AdoApiException"/>.
/// </summary>
public sealed class AdoHttp(HttpClient client, Action<AdoOperation>? operationObserver = null)
{
    /// <summary>
    /// Fires once per request made through <see cref="GetJsonAsync{T}"/>,
    /// <see cref="SendJsonAsync{TRequest,TResponse}"/>, <see cref="GetTextOrNullAsync"/>, and
    /// <see cref="SendRawAsync{TResponse}"/> — feeds the <c>:log</c> operations view. Never sees
    /// headers or the full query string (<see cref="RouteShape"/> strips both), so it can never
    /// leak a token.
    /// </summary>
    public Action<AdoOperation>? OperationObserver { get; set; } = operationObserver;

    private void Report(HttpMethod method, string path, long startTimestamp, HttpStatusCode? status)
    {
        if (OperationObserver is null)
        {
            return;
        }
        var operation = AdoOperation.FromRoute(
            method.Method,
            path,
            Stopwatch.GetElapsedTime(startTimestamp),
            status.HasValue ? (int)status.Value : null,
            DateTimeOffset.UtcNow);
        try
        {
            OperationObserver(operation);
        }
        catch (Exception)
        {
            // A misbehaving :log subscriber is a bug in that subscriber, not in the request
            // that just completed (or failed) — it must never mask the real outcome the caller
            // is awaiting, so it is swallowed here rather than propagated (ADR 0013 carve-out
            // for an optional, best-effort observer with no bearing on the ADO call itself).
        }
    }

    /// <summary>
    /// Best-effort connection warm-up: pays the cold DNS + TCP + TLS cost (~700ms to
    /// dev.azure.com) on a cheap route so the first real API call does not. Callers
    /// fire-and-forget it after auth.
    ///
    /// <para>Expected failures are swallowed, not surfaced: the warm-up has no user-visible job,
    /// so an auth/network fault here must not reach the message bar or the crash log — the first
    /// real request hits the same fault and reports it with proper context. Anything outside the
    /// expected set is a bug and still propagates (ADR 0013).</para>
    ///
    /// <para>Deliberately does not report to <see cref="OperationObserver"/>: it is not a
    /// request the user asked for, so it would only be noise in the <c>:log</c> view.</para>
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync(
                new Uri("_apis/connectionData?api-version=7.2-preview.1", UriKind.Relative),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or ObjectDisposedException || AdoExceptions.IsExpected(ex))
        {
            // Not signed in yet, offline, or shutting down. ObjectDisposedException is the quit
            // race: the app can dispose the connection while this request is still in flight, and
            // a fire-and-forget fault would then log a crash for a warm-up nobody was waiting on.
        }
    }

    public async Task<T> GetJsonAsync<T>(
        string path, JsonTypeInfo<T> type, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        HttpStatusCode? status = null;
        try
        {
            using var response = await client.GetAsync(
                new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Report(HttpMethod.Get, path, start, status);
        }
    }

    public async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        string contentType = "application/json",
        CancellationToken cancellationToken = default)
    {
        // Serialize straight to UTF-8 bytes (no intermediate string) and set an explicit
        // charset=utf-8 so the wire Content-Type stays byte-identical to the old StringContent.
        var payload = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, requestType));
        payload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType, "utf-8");
        var start = Stopwatch.GetTimestamp();
        HttpStatusCode? status = null;
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = payload };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            return await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Report(method, path, start, status);
        }
    }

    /// <summary>GETs a text resource (e.g. a file blob). Returns null on 404 so callers can treat a missing side as empty.</summary>
    public async Task<string?> GetTextOrNullAsync(string path, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        HttpStatusCode? status = null;
        try
        {
            using var response = await client.GetAsync(
                new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
            {
                throw new AdoApiException(HttpStatusCode.Unauthorized, "Azure DevOps did not accept the access token");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AdoApiException(response.StatusCode, $"Azure DevOps returned {(int)response.StatusCode}");
            }
            return body;
        }
        finally
        {
            Report(HttpMethod.Get, path, start, status);
        }
    }

    /// <summary>Sends a pre-serialized body (e.g. a JSON Patch document) and reads a typed response.</summary>
    public async Task<TResponse> SendRawAsync<TResponse>(
        HttpMethod method,
        string path,
        string body,
        JsonTypeInfo<TResponse> responseType,
        string contentType = "application/json",
        CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        HttpStatusCode? status = null;
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            return await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Report(method, path, start, status);
        }
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        // ADO answers 203 + an HTML sign-in page (instead of 401) when the token is bad.
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
        {
            throw new AdoApiException(
                HttpStatusCode.Unauthorized,
                "Azure DevOps did not accept the access token (are you signed in to the right tenant?)");
        }

        // Error path only: materialize the body as text so ExtractError can pull the ADO
        // `message` (or fall back to the status line for a non-JSON envelope like the HTML page).
        if (!response.IsSuccessStatusCode)
        {
            var bodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new AdoApiException(response.StatusCode, ExtractError(response, bodyText));
        }

        // Success path: deserialize straight from the buffered response stream (the default,
        // untimed buffering keeps HttpClient.Timeout coverage), skipping the intermediate string.
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false);
        return value is not null
            ? value
            : throw new AdoApiException(response.StatusCode, "Azure DevOps returned an empty response body");
    }

    private static string ExtractError(HttpResponseMessage response, string bodyText)
    {
        if (bodyText.AsSpan().TrimStart().StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(bodyText);
                if (doc.RootElement.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString()!;
                }
            }
            catch (JsonException)
            {
                // fall through to the generic message
            }
        }
        return $"Azure DevOps returned {(int)response.StatusCode} {response.ReasonPhrase}";
    }
}
