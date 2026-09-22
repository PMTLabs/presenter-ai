using Microsoft.EntityFrameworkCore;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Cli;

internal static class OwnerResolver
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public static async Task<(User? User, string? Error)> ResolveAsync(
        PresenterAiDbContext db,
        string email,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(email);
        var user = await db.Users.SingleOrDefaultAsync(
            candidate => candidate.Email == normalized,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return (null, $"Unknown owner: {normalized}");
        }

        return user.IsDisabled
            ? (null, $"Owner is disabled: {normalized}")
            : (user, null);
    }
}
