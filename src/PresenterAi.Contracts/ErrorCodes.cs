using System.ComponentModel;

namespace PresenterAi.Contracts;

public sealed record ErrorCodeInfo(string Code, string Title, int Status);

public static class ErrorCodes
{
    [Description("Validation failed")] public const string ValidationFailed = "validation.failed";
    [Description("Unsupported media type")] public const string ValidationUnsupportedMediaType = "validation.unsupported_media_type";
    [Description("Payload too large")] public const string ValidationPayloadTooLarge = "validation.payload_too_large";
    [Description("Authentication required")] public const string AuthRequired = "auth.required";
    [Description("Forbidden")] public const string AuthForbidden = "auth.forbidden";
    [Description("SSO provider disabled")] public const string AuthSsoProviderDisabled = "auth.sso_provider_disabled";
    [Description("Invalid SSO state")] public const string AuthSsoStateInvalid = "auth.sso_state_invalid";
    [Description("SSO code already used")] public const string AuthSsoCodeUsed = "auth.sso_code_used";
    [Description("Sign-up not allowed")] public const string AuthSignupNotAllowed = "auth.signup_not_allowed";
    [Description("Account disabled")] public const string AuthAccountDisabled = "auth.account_disabled";
    [Description("Concurrency conflict")] public const string ConcurrencyConflict = "concurrency.conflict";
    [Description("Presentation not found")] public const string PresentationNotFound = "presentation.not_found";
    [Description("Invalid presentation script")] public const string PresentationInvalidScript = "presentation.invalid_script";
    [Description("Presentation slide count mismatch")] public const string PresentationSlideCountMismatch = "presentation.slide_count_mismatch";
    [Description("Revision not found")] public const string RevisionNotFound = "revision.not_found";
    [Description("Deck not found")] public const string DeckNotFound = "deck.not_found";
    [Description("Unsupported deck format")] public const string DeckUnsupportedFormat = "deck.unsupported_format";
    [Description("Deck driver not found")] public const string DeckNoDriver = "deck.no_driver";
    [Description("Session slots busy")] public const string SessionSlotsBusy = "session.slots_busy";
    [Description("Session already running")] public const string SessionAlreadyRunning = "session.already_running";
    [Description("Session quota exceeded")] public const string SessionQuotaExceeded = "session.quota_exceeded";
    [Description("Invalid session ticket")] public const string SessionTicketInvalid = "session.ticket_invalid";
    [Description("Upstream unavailable")] public const string UpstreamUnavailable = "upstream.unavailable";
    [Description("Upstream rate limited")] public const string UpstreamRateLimited = "upstream.rate_limited";
    [Description("Upstream rejected request")] public const string UpstreamRejected = "upstream.rejected";
    [Description("Generation quota exceeded")] public const string GenerationQuotaExceeded = "generation.quota_exceeded";
    [Description("Generation job not found")] public const string GenerationJobNotFound = "generation.job_not_found";
    [Description("Generation job failed")] public const string GenerationJobFailed = "generation.job_failed";
    [Description("Generation busy")] public const string GenerationBusy = "generation.busy";
    [Description("Provider not found")] public const string ProviderNotFound = "provider.not_found";
    [Description("Provider disabled")] public const string ProviderDisabled = "provider.disabled";
    [Description("Provider test failed")] public const string ProviderTestFailed = "provider.test_failed";
    [Description("Model not found")] public const string ModelNotFound = "model.not_found";
    [Description("Model retired")] public const string ModelRetired = "model.retired";
    [Description("Model not allowed")] public const string ModelNotAllowed = "model.not_allowed";
    [Description("Document not found")] public const string DocumentNotFound = "document.not_found";
    [Description("Document not ready")] public const string DocumentNotReady = "document.not_ready";
    [Description("Document too large")] public const string DocumentTooLarge = "document.too_large";
    [Description("Rate limit exceeded")] public const string RateLimitExceeded = "rate_limit.exceeded";
    [Description("Internal server error")] public const string InternalError = "internal.error";
    [Description("Not implemented")] public const string InternalNotImplemented = "internal.not_implemented";
    [Description("Tool server URL is invalid")] public const string ToolsUrlInvalid = "tools_url_invalid";
    [Description("Tool server URL is blocked")] public const string ToolsUrlBlocked = "tools_url_blocked";
    [Description("Tool server limit reached")] public const string ToolsServerLimit = "tools_server_limit";
    [Description("Tool server name is invalid")] public const string ToolsNameInvalid = "tools_name_invalid";
    [Description("Tool server not found")] public const string ToolsServerNotFound = "tools_server_not_found";
    [Description("Tool header is invalid")] public const string ToolsHeaderInvalid = "tools_header_invalid";
    [Description("Tool credentials unavailable")] public const string ToolsCredentialsUnavailable = "tools_credentials_unavailable";
    [Description("Tool OAuth unsupported")] public const string ToolsOAuthUnsupported = "tools_oauth_unsupported";
    [Description("Tool OAuth client required")] public const string ToolsOAuthClientRequired = "tools_oauth_client_required";
    [Description("Tool server unreachable")] public const string ToolsUnreachable = "tools_unreachable";
    [Description("Tool redirect refused")] public const string ToolsRedirectRefused = "tools_redirect_refused";
    [Description("Tool OAuth state invalid")] public const string ToolsOAuthStateInvalid = "tools_oauth_state_invalid";
    [Description("Tool OAuth failed")] public const string ToolsOAuthFailed = "tools_oauth_failed";
    [Description("Tool server authentication failed")] public const string ToolsAuth = "tools_auth";
    [Description("Tool credential key changed")] public const string ToolsCredentialKeyChanged = "tools_credential_key_changed";
    [Description("Tool credential unreadable")] public const string ToolsCredentialUnreadable = "tools_credential_unreadable";
    [Description("Tool response too large")] public const string ToolsResponseTooLarge = "tools_response_too_large";
    [Description("Tool OAuth invalid grant")] public const string ToolsOAuthInvalidGrant = "tools_oauth_invalid_grant";

    public static readonly IReadOnlyDictionary<string, ErrorCodeInfo> Catalogue =
        new Dictionary<string, ErrorCodeInfo>(StringComparer.Ordinal)
        {
            [ValidationFailed] = new(ValidationFailed, "Validation failed", 400),
            [ValidationUnsupportedMediaType] = new(ValidationUnsupportedMediaType, "Unsupported media type", 415),
            [ValidationPayloadTooLarge] = new(ValidationPayloadTooLarge, "Payload too large", 413),
            [AuthRequired] = new(AuthRequired, "Authentication required", 401),
            [AuthForbidden] = new(AuthForbidden, "Forbidden", 403),
            [AuthSsoProviderDisabled] = new(AuthSsoProviderDisabled, "SSO provider disabled", 400),
            [AuthSsoStateInvalid] = new(AuthSsoStateInvalid, "Invalid SSO state", 400),
            [AuthSsoCodeUsed] = new(AuthSsoCodeUsed, "SSO code already used", 400),
            [AuthSignupNotAllowed] = new(AuthSignupNotAllowed, "Sign-up not allowed", 403),
            [AuthAccountDisabled] = new(AuthAccountDisabled, "Account disabled", 403),
            [ConcurrencyConflict] = new(ConcurrencyConflict, "Concurrency conflict", 412),
            [PresentationNotFound] = new(PresentationNotFound, "Presentation not found", 404),
            [PresentationInvalidScript] = new(PresentationInvalidScript, "Invalid presentation script", 400),
            [PresentationSlideCountMismatch] = new(PresentationSlideCountMismatch, "Presentation slide count mismatch", 409),
            [RevisionNotFound] = new(RevisionNotFound, "Revision not found", 404),
            [DeckNotFound] = new(DeckNotFound, "Deck not found", 404),
            [DeckUnsupportedFormat] = new(DeckUnsupportedFormat, "Unsupported deck format", 415),
            [DeckNoDriver] = new(DeckNoDriver, "Deck driver not found", 422),
            [SessionSlotsBusy] = new(SessionSlotsBusy, "Session slots busy", 429),
            [SessionAlreadyRunning] = new(SessionAlreadyRunning, "Session already running", 409),
            [SessionQuotaExceeded] = new(SessionQuotaExceeded, "Session quota exceeded", 429),
            [SessionTicketInvalid] = new(SessionTicketInvalid, "Invalid session ticket", 401),
            [UpstreamUnavailable] = new(UpstreamUnavailable, "Upstream unavailable", 503),
            [UpstreamRateLimited] = new(UpstreamRateLimited, "Upstream rate limited", 429),
            [UpstreamRejected] = new(UpstreamRejected, "Upstream rejected request", 502),
            [GenerationQuotaExceeded] = new(GenerationQuotaExceeded, "Generation quota exceeded", 429),
            [GenerationJobNotFound] = new(GenerationJobNotFound, "Generation job not found", 404),
            [GenerationJobFailed] = new(GenerationJobFailed, "Generation job failed", 500),
            [GenerationBusy] = new(GenerationBusy, "Generation busy", 409),
            [ProviderNotFound] = new(ProviderNotFound, "Provider not found", 404),
            [ProviderDisabled] = new(ProviderDisabled, "Provider disabled", 409),
            [ProviderTestFailed] = new(ProviderTestFailed, "Provider test failed", 502),
            [ModelNotFound] = new(ModelNotFound, "Model not found", 404),
            [ModelRetired] = new(ModelRetired, "Model retired", 410),
            [ModelNotAllowed] = new(ModelNotAllowed, "Model not allowed", 403),
            [DocumentNotFound] = new(DocumentNotFound, "Document not found", 404),
            [DocumentNotReady] = new(DocumentNotReady, "Document not ready", 409),
            [DocumentTooLarge] = new(DocumentTooLarge, "Document too large", 413),
            [RateLimitExceeded] = new(RateLimitExceeded, "Rate limit exceeded", 429),
            [InternalError] = new(InternalError, "Internal server error", 500),
            [InternalNotImplemented] = new(InternalNotImplemented, "Not implemented", 501),
            [ToolsUrlInvalid] = new(ToolsUrlInvalid, "Tool server URL is invalid", 400),
            [ToolsUrlBlocked] = new(ToolsUrlBlocked, "Tool server URL is blocked", 400),
            [ToolsServerLimit] = new(ToolsServerLimit, "Tool server limit reached", 400),
            [ToolsNameInvalid] = new(ToolsNameInvalid, "Tool server name is invalid", 400),
            [ToolsServerNotFound] = new(ToolsServerNotFound, "Tool server not found", 404),
            [ToolsHeaderInvalid] = new(ToolsHeaderInvalid, "Tool header is invalid", 400),
            [ToolsCredentialsUnavailable] = new(ToolsCredentialsUnavailable, "Tool credentials unavailable", 503),
            [ToolsOAuthUnsupported] = new(ToolsOAuthUnsupported, "Tool OAuth unsupported", 400),
            [ToolsOAuthClientRequired] = new(ToolsOAuthClientRequired, "Tool OAuth client required", 400),
            [ToolsUnreachable] = new(ToolsUnreachable, "Tool server unreachable", 502),
            [ToolsRedirectRefused] = new(ToolsRedirectRefused, "Tool redirect refused", 400),
            [ToolsOAuthStateInvalid] = new(ToolsOAuthStateInvalid, "Tool OAuth state invalid", 400),
            [ToolsOAuthFailed] = new(ToolsOAuthFailed, "Tool OAuth failed", 400),
            [ToolsAuth] = new(ToolsAuth, "Tool server authentication failed", 401),
            [ToolsCredentialKeyChanged] = new(ToolsCredentialKeyChanged, "Tool credential key changed", 401),
            [ToolsCredentialUnreadable] = new(ToolsCredentialUnreadable, "Tool credential unreadable", 401),
            [ToolsResponseTooLarge] = new(ToolsResponseTooLarge, "Tool response too large", 502),
            [ToolsOAuthInvalidGrant] = new(ToolsOAuthInvalidGrant, "Tool OAuth invalid grant", 400)
        };
}
