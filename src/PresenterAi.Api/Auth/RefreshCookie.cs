namespace PresenterAi.Api.Auth;

public static class RefreshCookie
{
    public const string Name = "presenter_rt";
    public const string Path = "/v1/auth";

    public static void Append(HttpResponse response, string value, DateTimeOffset expiresAt)
    {
        response.Cookies.Append(Name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = Path,
            Expires = expiresAt
        });
    }

    public static void Delete(HttpResponse response) =>
        response.Cookies.Delete(Name, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = Path
        });
}
