namespace Cobalt.Core.Auth;

public interface ITokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
