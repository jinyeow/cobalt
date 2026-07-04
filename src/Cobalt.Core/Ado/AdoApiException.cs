using System.Net;

namespace Cobalt.Core.Ado;

public sealed class AdoApiException : Exception
{
    public AdoApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
