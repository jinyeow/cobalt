using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cobalt.Core.Ado;

/// <summary>
/// Thin JSON transport over one org's HttpClient: source-generated (de)serialization
/// and translation of ADO error envelopes into <see cref="AdoApiException"/>.
/// </summary>
public sealed class AdoHttp(HttpClient client)
{
    public async Task<T> GetJsonAsync<T>(
        string path, JsonTypeInfo<T> type, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(
            new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
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
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, requestType), Encoding.UTF8, contentType),
        };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
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

        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AdoApiException(response.StatusCode, ExtractError(response, bodyText));
        }

        var value = JsonSerializer.Deserialize(bodyText, type);
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
