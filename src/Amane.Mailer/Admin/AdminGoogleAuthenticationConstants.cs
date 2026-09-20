namespace Amane.Mailer.Admin;

public static class AdminGoogleAuthenticationConstants
{
    public const string AuthenticationScheme = "Google";
    public const string ExternalScheme = "AmaneAdminExternal";
    public const string Issuer = "https://accounts.google.com";
    public const string CallbackPath = "/admin/signin-google";
    public const string CompletionPath = "/admin/signin-google/complete";
    public const string LinkPath = "/admin/signin-google/link";
    public const string ChallengePath = "/admin/api/login-google";
}
