using System.Net;
using System.Text;

namespace Cobalt.Core.Tests.Fakes;

/// <summary>
/// Scriptable HttpMessageHandler: enqueue responses, then assert on captured requests.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    // Assert bodies/content types via RequestBodies/ContentTypes, never Requests[i].Content —
    // AdoHttp disposes the request message after sending, so Content is unreadable afterwards.
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string?> RequestBodies { get; } = [];
    public List<string?> ContentTypes { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string json) =>
        Respond(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public FakeHttpHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken));
        ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeHttpHandler: no scripted response for {request.Method} {request.RequestUri}");
        }

        return _responses.Dequeue()(request);
    }
}
