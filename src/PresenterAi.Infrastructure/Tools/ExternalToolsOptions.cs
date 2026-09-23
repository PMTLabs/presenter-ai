namespace PresenterAi.Infrastructure.Tools;

// Separate from Application.Tools.ToolsOptions (MaxInlineTools), but bound to the same Tools section.
public sealed class ExternalToolsOptions
{
    public string? CredentialKey { get; set; }
    public string? OAuthRedirectUri { get; set; }
    public McpBudgets Mcp { get; set; } = new();

    public sealed class McpBudgets
    {
        public int StartBudgetMs { get; set; } = 3000;
        public int CallTimeoutSeconds { get; set; } = 10;
    }

    public static bool ValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return true;
        try { return Convert.FromBase64String(key).Length == 32; }
        catch (FormatException) { return false; }
    }

    public static bool ValidRedirect(string? uri) => string.IsNullOrEmpty(uri) ||
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http";
}
