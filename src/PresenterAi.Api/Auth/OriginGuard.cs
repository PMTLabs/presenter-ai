namespace PresenterAi.Api.Auth;

public static class OriginGuard
{
    public static bool IsAllowed(HttpRequest request, IConfiguration configuration)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        return configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?
            .Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
