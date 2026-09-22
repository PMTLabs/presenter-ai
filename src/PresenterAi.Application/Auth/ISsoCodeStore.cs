namespace PresenterAi.Application.Auth;

public sealed record SsoCode(string UserId, string CodeChallenge, string RedirectUri);

public interface ISsoCodeStore
{
    Task IssueAsync(string codeHash, SsoCode code, CancellationToken cancellationToken = default);

    Task<SsoCode?> ClaimAsync(string codeHash, CancellationToken cancellationToken = default);
}
