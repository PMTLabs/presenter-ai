namespace PresenterAi.Application.Auth;

public interface ISsoStateStore
{
    Task<bool> TryConsumeNonceAsync(string nonce, CancellationToken cancellationToken = default);
}
