namespace Amane.Mailer.Setup.NonInteractive;

internal static class SetupNonInteractiveResultCode
{
    internal const string Success = "setup.non_interactive.succeeded";
    internal const string Cancelled = "setup.non_interactive.cancelled";
    internal const string ConfigNotFound = "config_not_found";
    internal const string ConfigPathRejected = "config_path_rejected";
    internal const string ConfigPathUnsafe = "config_path_unsafe";
    internal const string ConfigNotRegularFile = "config_not_regular_file";
    internal const string ConfigPermissionsRejected = "config_permissions_rejected";
    internal const string ConfigTooLarge = "config_too_large";
    internal const string UnsupportedPlatform = "unsupported_platform";
    internal const string InvalidUtf8 = "invalid_utf8";
    internal const string InvalidJson = "invalid_json";
    internal const string DuplicateProperty = "duplicate_property";
    internal const string NestedTooDeep = "nested_too_deep";
    internal const string UnsupportedSchema = "unsupported_schema";
    internal const string InvalidMode = "invalid_mode";
    internal const string ModeNotSupported = "mode_not_supported";
    internal const string InvalidTenant = "invalid_tenant";
    internal const string MissingRequiredField = "missing_required_field";
    internal const string InvalidTenantId = "invalid_tenant_id";
    internal const string InvalidIdentifier = "invalid_identifier";
    internal const string InvalidSourceService = "invalid_source_service";
    internal const string InvalidEmail = "invalid_email";
    internal const string InvalidDisplayName = "invalid_display_name";
    internal const string InvalidSecret = "invalid_secret";
    internal const string InvalidAcs = "invalid_acs";
    internal const string ForbiddenField = "forbidden_field";
    internal const string UnknownField = "unknown_field";
    internal const string ConfirmationPhraseMismatch = "confirmation_phrase_mismatch";
    internal const string AdminInputRejected = "admin_input_rejected";
    internal const string UseSetupAssistantAction = "use_setup_assistant";
}
