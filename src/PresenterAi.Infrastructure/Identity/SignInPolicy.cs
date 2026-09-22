using Microsoft.Extensions.Configuration;

namespace PresenterAi.Infrastructure.Identity;

public sealed class SignInPolicy(IConfiguration configuration)
{
    private readonly HashSet<string> _allowedDomains = configuration
        .GetSection("Auth:SignIn:AllowedEmailDomains")
        .Get<string[]>()?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().TrimStart('@').ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

    private readonly HashSet<string> _allowedEmails = configuration
        .GetSection("Auth:SignIn:AllowedEmails")
        .Get<string[]>()?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

    private readonly HashSet<string> _bootstrapAdmins = configuration
        .GetSection("Admin:BootstrapEmails")
        .Get<string[]>()?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

    public bool IsAllowed(string? email)
    {
        // An unrestricted policy is useful for a deployment that intentionally allows both
        // providers. Once either allow-list is configured, an asserted email is required.
        if (_allowedDomains.Count == 0 && _allowedEmails.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalized = email.Trim().ToLowerInvariant();
        var at = normalized.LastIndexOf('@');
        var domain = at >= 0 ? normalized[(at + 1)..] : string.Empty;
        return _allowedEmails.Contains(normalized) || _allowedDomains.Contains(domain);
    }

    public bool IsBootstrapAdmin(string email) => _bootstrapAdmins.Contains(email.Trim().ToLowerInvariant());
}
