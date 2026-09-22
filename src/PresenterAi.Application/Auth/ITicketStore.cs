namespace PresenterAi.Application.Auth;

public interface ITicketStore
{
    Task IssueAsync(string ticketId, string userId, CancellationToken cancellationToken = default);

    Task<string?> ClaimAsync(string ticketId, CancellationToken cancellationToken = default);
}
