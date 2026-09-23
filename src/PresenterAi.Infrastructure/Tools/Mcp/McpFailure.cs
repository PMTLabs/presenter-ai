namespace PresenterAi.Infrastructure.Tools.Mcp;

public static class McpFailure
{
    public static string Status(string code) => code is "auth" or "tools_auth" or "oauth_invalid_grant" or
        "tools_oauth_invalid_grant" or "credential_unreadable" or "credential_key_changed"
        ? "needs_reconnect" : "error";

    public static int HttpStatus(string code) => code is "oauth_invalid_grant" or "tools_oauth_invalid_grant" ? 400 :
        Status(code) == "needs_reconnect" ? 401 : 502;

    public static string ProblemCode(string code) => code switch
    {
        "auth" or "tools_auth" => "tools_auth",
        "credential_key_changed" => "tools_credential_key_changed",
        "credential_unreadable" => "tools_credential_unreadable",
        "oauth_invalid_grant" or "tools_oauth_invalid_grant" => "tools_oauth_invalid_grant",
        _ => "tools_unreachable"
    };
}
