using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace Amane.Mailer.Admin;

/// <summary>
/// Instance-owner portable settings backup. This intentionally stays separate from
/// the full-instance database and attachment backup workflow.
/// </summary>
public static class AdminSettingsBackupPage
{
    public const string PagePath = "/admin/settings-backup";
    public const string ExportPath = PagePath + "/export";
    public const string PreviewPath = PagePath + "/preview";
    public const string RestorePath = PagePath + "/restore";
    public const int MaxRequestBodyBytes = 2 * 1024 * 1024;

    private const string GenericImportFailure = "設定ファイルまたはパスフレーズを確認してください。";
    private const string GenericOperationFailure = "設定backupを処理できませんでした。";
    private const string TargetUnavailable = "復元先のmanaged configurationを確認できません。";

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var access = accessResult.Access!;
        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);
        var csrfToken = HtmlEncoder.Default.Encode(
            antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);
        return Results.Content(
            RenderHome(access, deadLetterCount, csrfToken, message: null),
            "text/html; charset=utf-8");
    }

    public static async Task<IResult> ExportAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        InstanceConfigurationRepository instanceConfigurationRepository,
        SenderRepository senderRepository,
        AdminGoogleOptions googleOptions,
        IConfiguration configuration,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        ApplyRequestLimits(context);
        if (RequestContentLengthExceedsLimit(context))
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid CSRF token.");

        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var form = await TryReadFormAsync(context, cancellationToken);
        if (form is null)
            return await ExportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, cancellationToken);

        var passphrase = form["passphrase"].ToString();
        var confirmation = form["passphrase_confirmation"].ToString();
        if (!SettingsBackupFormat.IsValidPassphrase(passphrase)
            || !string.Equals(passphrase, confirmation, StringComparison.Ordinal))
        {
            return await ExportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, cancellationToken);
        }

        byte[]? encrypted = null;
        try
        {
            var current = await instanceConfigurationRepository.GetAsync(cancellationToken);
            if (!AdminSecretsPage.IsManagedAcs(current)
                || !FirstRunSetupStorage.TryReadValidAcsSecret(current!.ProviderSecretRef!, out var acsSecret))
            {
                return await ExportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, cancellationToken);
            }

            var googleStatus = AdminGoogleSettingsStatus.Evaluate(current, googleOptions, configuration);
            var googleIncluded = googleStatus.SavedUsesManaged;
            string? googleSecret = null;
            if (googleIncluded)
            {
                if (!string.IsNullOrWhiteSpace(current.GoogleClientSecretRef)
                    && AdminGoogleSecretStore.TryReadSecret(current.GoogleClientSecretRef, out var storedGoogleSecret))
                {
                    googleSecret = storedGoogleSecret;
                }

                if (current.GoogleLoginEnabled && (!googleStatus.SavedComplete || googleSecret is null))
                {
                    return await ExportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, cancellationToken);
                }
            }

            var senders = await senderRepository.ListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var payload = new SettingsBackupPayload
            {
                Format = SettingsBackupFormat.FormatIdentifier,
                Version = SettingsBackupFormat.CurrentVersion,
                ExportedAtUtc = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Settings = new SettingsBackupSettings
                {
                    Provider = new SettingsBackupProvider { Type = "acs" },
                    LiveSending = current.LiveSending,
                    GoogleLogin = new SettingsBackupGoogleLogin
                    {
                        Included = googleIncluded,
                        Enabled = googleIncluded && current.GoogleLoginEnabled,
                        ClientId = googleIncluded ? NormalizeOptional(current.GoogleClientId) : null,
                    },
                    Senders = senders.Select(sender => new SettingsBackupSender
                    {
                        Email = sender.Email,
                        DisplayName = sender.DisplayName,
                        Enabled = sender.Enabled,
                    }).ToArray(),
                },
                Secrets = new SettingsBackupSecrets
                {
                    AcsConnectionString = acsSecret,
                    GoogleClientSecret = googleIncluded ? googleSecret : null,
                },
            };

            var plaintext = SettingsBackupFormat.Serialize(payload);
            try
            {
                if (!SettingsBackupCrypto.TryEncrypt(plaintext, passphrase, out encrypted)
                    || encrypted is null)
                {
                    return await ExportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, cancellationToken);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await ExportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, cancellationToken);
        }

        await WriteAuditAsync(
            context,
            auditRepository,
            adminOptions,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.SettingsBackupExported,
            AdminAuditLog.Results.Success,
            errorCode: null,
            cancellationToken);

        var timestamp = timeProvider.GetUtcNow().ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture);
        var filename = $"amane-mailer-settings-{timestamp}.json.age";
        return Results.File(encrypted!, "application/octet-stream", filename);
    }

    public static async Task<IResult> PreviewAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        InstanceConfigurationRepository instanceConfigurationRepository,
        SenderRepository senderRepository,
        AdminGoogleOptions googleOptions,
        MailerOptions mailerOptions,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        IConfiguration configuration,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        ApplyRequestLimits(context);
        if (RequestContentLengthExceedsLimit(context))
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid CSRF token.");

        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var form = await TryReadFormAsync(context, cancellationToken);
        if (form is null)
            return await ImportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, PreviewPath, cancellationToken);

        var passphrase = form["passphrase"].ToString();
        if (!SettingsBackupFormat.IsValidPassphrase(passphrase))
            return await ImportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, PreviewPath, cancellationToken);

        var encrypted = await TryReadEncryptedUploadAsync(form, cancellationToken);
        if (encrypted is null
            || !SettingsBackupCrypto.TryDecryptAndValidate(encrypted, passphrase, out var payload)
            || payload is null)
        {
            return await ImportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, PreviewPath, cancellationToken);
        }

        RestoreTargetState? target;
        try
        {
            target = await LoadTargetStateAsync(
                payload,
                instanceConfigurationRepository,
                senderRepository,
                googleOptions,
                configuration,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            target = null;
        }

        if (target is null)
        {
            return await ImportFailureAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                PreviewPath,
                cancellationToken,
                targetUnavailable: true);
        }

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            accessResult.Access!.AllowedTenantIdsForQuery,
            cancellationToken);
        var preview = BuildPreview(payload, target, mailerOptions);
        await WriteAuditAsync(
            context,
            auditRepository,
            adminOptions,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.SettingsBackupImportPreviewed,
            AdminAuditLog.Results.Success,
            errorCode: null,
            cancellationToken);

        var csrfToken = HtmlEncoder.Default.Encode(
            antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);
        return Results.Content(
            RenderPreview(
                accessResult.Access!,
                deadLetterCount,
                csrfToken,
                Base64UrlEncode(encrypted),
                preview),
            "text/html; charset=utf-8");
    }

    public static async Task<IResult> RestoreAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        InstanceConfigurationRepository instanceConfigurationRepository,
        SenderRepository senderRepository,
        AdminGoogleOptions googleOptions,
        MailerOptions mailerOptions,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        IConfiguration configuration,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        ApplyRequestLimits(context);
        if (RequestContentLengthExceedsLimit(context))
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid CSRF token.");

        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var form = await TryReadFormAsync(context, cancellationToken);
        if (form is null)
            return await ImportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, RestorePath, cancellationToken);

        if (!HasConfirmation(form))
        {
            return await ImportFailureAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                RestorePath,
                cancellationToken,
                message: "設定を復元するには確認が必要です。");
        }

        var passphrase = form["passphrase"].ToString();
        var encodedCiphertext = form["encrypted_ciphertext"].ToString();
        if (!SettingsBackupFormat.IsValidPassphrase(passphrase)
            || !TryBase64UrlDecode(encodedCiphertext, out var encrypted)
            || !SettingsBackupCrypto.TryDecryptAndValidate(encrypted, passphrase, out var payload)
            || payload is null)
        {
            return await ImportFailureAsync(context, auditRepository, adminOptions, loggerFactory, timeProvider, RestorePath, cancellationToken);
        }

        RestoreTargetState? target;
        try
        {
            target = await LoadTargetStateAsync(
                payload,
                instanceConfigurationRepository,
                senderRepository,
                googleOptions,
                configuration,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            target = null;
        }

        if (target is null)
        {
            return await ImportFailureAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                RestorePath,
                cancellationToken,
                targetUnavailable: true);
        }

        var preview = BuildPreview(payload, target, mailerOptions);
        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            accessResult.Access!.AllowedTenantIdsForQuery,
            cancellationToken);
        var liveSendingDisabled = false;
        try
        {
            // This is intentionally the first durable restore mutation.
            if (!await instanceConfigurationRepository.SetLiveSendingAsync(false, cancellationToken))
            {
                return await ImportFailureAsync(
                    context,
                    auditRepository,
                    adminOptions,
                    loggerFactory,
                    timeProvider,
                    RestorePath,
                    cancellationToken,
                    targetUnavailable: true);
            }

            liveSendingDisabled = true;
            if (!FirstRunSetupStorage.TryReplaceAcsSecret(
                    target.Configuration.ProviderSecretRef!,
                    payload.Secrets.AcsConnectionString))
            {
                return await ImportFailureAsync(
                    context,
                    auditRepository,
                    adminOptions,
                    loggerFactory,
                    timeProvider,
                    RestorePath,
                    cancellationToken,
                    liveSendingDisabled: true);
            }

            if (payload.Settings.GoogleLogin.Included)
            {
                if (payload.Secrets.GoogleClientSecret is { } googleSecret)
                    AdminGoogleSecretStore.WriteSecret(target.GoogleSecretPath!, googleSecret);

                if (!await instanceConfigurationRepository.SetGoogleLoginSettingsAsync(
                        payload.Settings.GoogleLogin.Enabled,
                        payload.Settings.GoogleLogin.ClientId,
                        target.GoogleSecretPath,
                        cancellationToken))
                {
                    return await ImportFailureAsync(
                        context,
                        auditRepository,
                        adminOptions,
                        loggerFactory,
                        timeProvider,
                        RestorePath,
                        cancellationToken,
                        liveSendingDisabled: true);
                }
            }

            if (!await MergeSendersAsync(senderRepository, payload.Settings.Senders, cancellationToken))
            {
                return await ImportFailureAsync(
                    context,
                    auditRepository,
                    adminOptions,
                    loggerFactory,
                    timeProvider,
                    RestorePath,
                    cancellationToken,
                    liveSendingDisabled: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await ImportFailureAsync(
                context,
                auditRepository,
                adminOptions,
                loggerFactory,
                timeProvider,
                RestorePath,
                cancellationToken,
                liveSendingDisabled);
        }

        await WriteAuditAsync(
            context,
            auditRepository,
            adminOptions,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.SettingsBackupRestored,
            AdminAuditLog.Results.Success,
            errorCode: null,
            cancellationToken);

        return Results.Content(
            RenderRestoreResult(
                accessResult.Access!,
                deadLetterCount,
                restartRequired: preview.RestartRequired),
            "text/html; charset=utf-8");
    }

    private static async Task<RestoreTargetState?> LoadTargetStateAsync(
        SettingsBackupPayload payload,
        InstanceConfigurationRepository instanceConfigurationRepository,
        SenderRepository senderRepository,
        AdminGoogleOptions googleOptions,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var current = await instanceConfigurationRepository.GetAsync(cancellationToken);
        if (!AdminSecretsPage.IsManagedAcs(current)
            || !FirstRunSetupStorage.TryReadValidAcsSecret(current!.ProviderSecretRef!, out var acsSecret))
        {
            return null;
        }

        var googleStatus = AdminGoogleSettingsStatus.Evaluate(current, googleOptions, configuration);
        string? googleSecretPath = null;
        string? googleTargetSecret = null;
        var googleTargetConfigured = false;
        if (payload.Settings.GoogleLogin.Included)
        {
            googleSecretPath = AdminGoogleSecretStore.ResolveSecretPath(configuration);
            if (!IsSafeGoogleTargetPath(googleSecretPath)
                || PathsEqual(current.ProviderSecretRef, googleSecretPath))
            {
                return null;
            }

            if (File.Exists(googleSecretPath))
            {
                if (!AdminGoogleSecretStore.TryReadSecret(googleSecretPath, out googleTargetSecret))
                    return null;

                googleTargetConfigured = true;
            }
        }

        var senders = await senderRepository.ListAsync(cancellationToken);
        return new RestoreTargetState(
            current,
            acsSecret,
            googleStatus,
            googleSecretPath,
            googleTargetSecret,
            googleTargetConfigured,
            senders);
    }

    private static PreviewData BuildPreview(
        SettingsBackupPayload payload,
        RestoreTargetState target,
        MailerOptions mailerOptions)
    {
        var importedGoogle = payload.Settings.GoogleLogin;
        var currentGoogleEnabled = target.GoogleStatus.SavedUsesManaged
            ? target.Configuration.GoogleLoginEnabled
            : target.GoogleStatus.SavedEffectiveEnabled;
        var currentGoogleClientId = target.GoogleStatus.SavedClientId;
        var acsUnchanged = AdminSecretsPage.SecretsMatch(
            target.AcsSecret,
            payload.Secrets.AcsConnectionString);

        var googleSecretStatus = ResolveGoogleSecretStatus(payload, target);
        var googleSecretChanged = importedGoogle.Included
            && payload.Secrets.GoogleClientSecret is { } importedSecret
            && (!target.GoogleTargetConfigured
                || target.GoogleTargetSecret is null
                || !AdminSecretsPage.SecretsMatch(target.GoogleTargetSecret, importedSecret));
        var googleSettingsChanged = target.GoogleStatus.RestartRequired;
        if (importedGoogle.Included)
        {
            googleSettingsChanged |= !target.GoogleStatus.SavedUsesManaged
                || target.Configuration.GoogleLoginEnabled != importedGoogle.Enabled
                || !string.Equals(
                    NormalizeOptional(target.Configuration.GoogleClientId),
                    importedGoogle.ClientId,
                    StringComparison.Ordinal)
                || googleSecretChanged;
        }

        var acsRuntimeMatchesImported = !string.IsNullOrWhiteSpace(mailerOptions.AcsConnectionString)
            && AdminSecretsPage.SecretsMatch(
                mailerOptions.AcsConnectionString,
                payload.Secrets.AcsConnectionString);

        var currentSenders = target.Senders.ToDictionary(sender => sender.Email, StringComparer.Ordinal);
        var importedEmails = new HashSet<string>(StringComparer.Ordinal);
        var senderPreviews = new List<SenderPreview>(payload.Settings.Senders.Length);
        foreach (var imported in payload.Settings.Senders)
        {
            importedEmails.Add(imported.Email);
            currentSenders.TryGetValue(imported.Email, out var currentSender);
            var action = currentSender is null
                ? "create"
                : string.Equals(currentSender.DisplayName, imported.DisplayName, StringComparison.Ordinal)
                    && currentSender.Enabled == imported.Enabled
                    ? "unchanged"
                    : "update";
            senderPreviews.Add(new SenderPreview(
                imported.Email,
                currentSender?.DisplayName,
                imported.DisplayName,
                currentSender?.Enabled,
                imported.Enabled,
                action));
        }

        var localOnlySenderCount = target.Senders.Count(sender => !importedEmails.Contains(sender.Email));
        return new PreviewData(
            payload.Settings.LiveSending,
            acsUnchanged,
            importedGoogle.Included,
            currentGoogleEnabled,
            importedGoogle.Enabled,
            currentGoogleClientId,
            importedGoogle.ClientId,
            googleSecretStatus,
            senderPreviews,
            localOnlySenderCount,
            RestartRequired: !acsRuntimeMatchesImported
                || !acsUnchanged
                || googleSettingsChanged);
    }

    private static string ResolveGoogleSecretStatus(
        SettingsBackupPayload payload,
        RestoreTargetState target)
    {
        if (!payload.Settings.GoogleLogin.Included)
            return "preserved";

        if (payload.Secrets.GoogleClientSecret is not { } importedSecret)
            return "missing";

        if (!target.GoogleTargetConfigured || target.GoogleTargetSecret is null)
            return "configured";

        return AdminSecretsPage.SecretsMatch(target.GoogleTargetSecret, importedSecret)
            ? "unchanged"
            : "different";
    }

    private static async Task<bool> MergeSendersAsync(
        SenderRepository senderRepository,
        IReadOnlyList<SettingsBackupSender> importedSenders,
        CancellationToken cancellationToken)
    {
        foreach (var imported in importedSenders)
        {
            var current = await senderRepository.FindByEmailAsync(imported.Email, cancellationToken);
            if (current is null)
            {
                current = await senderRepository.CreateAsync(
                    imported.Email,
                    imported.DisplayName,
                    cancellationToken);
                if (!imported.Enabled)
                    await senderRepository.DisableAsync(current.SenderId, cancellationToken);

                continue;
            }

            if (!string.Equals(current.DisplayName, imported.DisplayName, StringComparison.Ordinal)
                && !await senderRepository.UpdateDisplayNameAsync(
                    current.SenderId,
                    imported.DisplayName,
                    cancellationToken))
            {
                return false;
            }

            if (current.Enabled != imported.Enabled)
            {
                if (imported.Enabled)
                    await senderRepository.EnableAsync(current.SenderId, cancellationToken);
                else
                    await senderRepository.DisableAsync(current.SenderId, cancellationToken);
            }
        }

        return true;
    }

    private static async Task<byte[]?> TryReadEncryptedUploadAsync(
        IFormCollection form,
        CancellationToken cancellationToken)
    {
        if (form.Files.Count != 1)
            return null;

        var file = form.Files[0];
        if (file.Length is <= 0 or > SettingsBackupCrypto.MaxEncryptedBytes)
            return null;

        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            if (output.Length + read > SettingsBackupCrypto.MaxEncryptedBytes)
                return null;

            output.Write(buffer, 0, read);
        }

        return output.Length == 0 ? null : output.ToArray();
    }

    private static bool IsSafeGoogleTargetPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            if (Directory.Exists(directory))
                FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);

            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or SecurityException
            or SecretOperationException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static async Task<IFormCollection?> TryReadFormAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException
            or IOException
            or InvalidOperationException
            or BadHttpRequestException)
        {
            return null;
        }
    }

    private static async Task<bool> ValidateAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidDataException
            or IOException
            or InvalidOperationException
            or BadHttpRequestException)
        {
            return false;
        }
    }

    private static bool HasConfirmation(IFormCollection form) =>
        string.Equals(form["confirmation"].ToString(), "restore", StringComparison.Ordinal);

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] decoded)
    {
        decoded = [];
        var maxEncodedLength = (SettingsBackupCrypto.MaxEncryptedBytes * 4 / 3) + 4;
        if (string.IsNullOrEmpty(value) || value.Length > maxEncodedLength)
            return false;

        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_'))
            {
                return false;
            }
        }

        var standard = value.Replace('-', '+').Replace('_', '/');
        standard = (standard.Length % 4) switch
        {
            0 => standard,
            2 => standard + "==",
            3 => standard + "=",
            _ => string.Empty,
        };
        if (standard.Length == 0)
            return false;

        try
        {
            decoded = Convert.FromBase64String(standard);
            return decoded.Length is > 0 and <= SettingsBackupCrypto.MaxEncryptedBytes;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    private static bool RequestContentLengthExceedsLimit(HttpContext context) =>
        context.Request.ContentLength is > MaxRequestBodyBytes;

    private static void ApplyRequestLimits(HttpContext context)
    {
        var requestBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (requestBodySizeFeature is { IsReadOnly: false })
            requestBodySizeFeature.MaxRequestBodySize = MaxRequestBodyBytes;

        context.Features.Set<IFormFeature>(new FormFeature(
            context.Request,
            new FormOptions
            {
                MultipartBodyLengthLimit = MaxRequestBodyBytes,
                ValueLengthLimit = MaxRequestBodyBytes,
                ValueCountLimit = 8,
                KeyLengthLimit = 128,
                MemoryBufferThreshold = 64 * 1024,
            }));
    }

    private static async Task<IResult> ExportFailureAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await WriteAuditAsync(
            context,
            auditRepository,
            adminOptions,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.SettingsBackupExported,
            AdminAuditLog.Results.Failure,
            AdminAuditLog.ErrorCodes.OperationFailed,
            cancellationToken);
        return Results.Conflict(GenericOperationFailure);
    }

    private static async Task<IResult> ImportFailureAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        string eventType,
        CancellationToken cancellationToken,
        bool targetUnavailable = false,
        bool liveSendingDisabled = false,
        string? message = null)
    {
        var auditEvent = eventType == PreviewPath
            ? AdminAuditLog.EventTypes.SettingsBackupImportPreviewed
            : AdminAuditLog.EventTypes.SettingsBackupRestored;
        await WriteAuditAsync(
            context,
            auditRepository,
            adminOptions,
            loggerFactory,
            timeProvider,
            auditEvent,
            AdminAuditLog.Results.Failure,
            AdminAuditLog.ErrorCodes.OperationFailed,
            cancellationToken);

        if (message is not null)
            return Results.BadRequest(message);
        if (targetUnavailable)
            return Results.Conflict(TargetUnavailable);
        if (liveSendingDisabled)
            return Results.Conflict("設定を最後まで復元できませんでした。Live SendingはOFFのままです。安全を確認してから再度Previewしてください。");

        return Results.BadRequest(GenericImportFailure);
    }

    private static Task WriteAuditAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        string eventType,
        string result,
        string? errorCode,
        CancellationToken cancellationToken) =>
        AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory),
            new AdminAuditEvent
            {
                EventType = eventType,
                Actor = AdminAuditLog.ResolveActor(context),
                OccurredAt = timeProvider.GetUtcNow(),
                SourceIp = adminOptions.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
                UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
                TargetType = AdminAuditLog.TargetTypes.SettingsBackup,
                Result = result,
                ErrorCode = errorCode,
            },
            cancellationToken);

    private static string RenderHome(
        AdminTenantAccess access,
        int deadLetterCount,
        string csrfToken,
        string? message)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "設定backup - Amane Admin",
            AdminNavItem.SettingsBackup,
            deadLetterCount,
            access);
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"設定backup\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">設定backup</h1>");
        html.AppendLine("                  <p class=\"ops-description\">設定とproduct-managed secretをage形式の暗号化ファイルとして持ち運びます。メール配送履歴、監査履歴、DB全体は含みません。</p>");
        if (message is not null)
        {
            html.Append("                  <p class=\"ops-meta\" role=\"status\">");
            html.Append(Html(message));
            html.AppendLine("</p>");
        }

        html.AppendLine("                  <p class=\"ops-meta\">Exportには初期化済みmanaged ACSと有効なACS secretが必要です。ImportはPreview後に明示確認して復元します。復元後のLive Sendingは常にOFFになります。</p>");
        html.AppendLine("                </section>");
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Export\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">暗号化してExport</h2>");
        html.AppendLine("                  <form method=\"post\" action=\"/admin/settings-backup/export\" class=\"stack-form\">");
        AppendCsrf(html, csrfToken);
        AppendPassphraseInput(html, "passphrase", "パスフレーズ");
        AppendPassphraseInput(html, "passphrase_confirmation", "パスフレーズ確認");
        html.AppendLine("                    <button type=\"submit\">暗号化してダウンロード</button>");
        html.AppendLine("                  </form>");
        html.AppendLine("                </section>");
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Import\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">暗号化ファイルをPreview</h2>");
        html.AppendLine("                  <form method=\"post\" action=\"/admin/settings-backup/preview\" enctype=\"multipart/form-data\" class=\"stack-form\">");
        AppendCsrf(html, csrfToken);
        html.AppendLine("                    <label><span>設定ファイル（最大1 MiB）</span><input type=\"file\" name=\"encrypted_file\" accept=\".age,application/octet-stream\" required></label>");
        AppendPassphraseInput(html, "passphrase", "パスフレーズ");
        html.AppendLine("                    <button type=\"submit\">内容を読み込んでPreview</button>");
        html.AppendLine("                  </form>");
        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static string RenderPreview(
        AdminTenantAccess access,
        int deadLetterCount,
        string csrfToken,
        string encryptedCiphertext,
        PreviewData preview)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "設定backup Preview - Amane Admin",
            AdminNavItem.SettingsBackup,
            deadLetterCount,
            access);
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"設定backup Preview\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">設定backup Preview</h1>");
        html.AppendLine("                  <p class=\"ops-description\">この画面では設定を変更しません。Restore時にファイルを再decrypt・再検証します。</p>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Provider", "ACS");
        AppendDefinition(
            html,
            "Google Login",
            preview.GoogleIncluded
                ? $"included; {OnOff(preview.CurrentGoogleEnabled)} → {OnOff(preview.ImportedGoogleEnabled)}"
                : "preserved on target");
        AppendDefinition(
            html,
            "Google Client ID",
            preview.GoogleIncluded
                ? $"{DisplayValue(preview.CurrentGoogleClientId)} → {DisplayValue(preview.ImportedGoogleClientId)}"
                : "preserved on target");
        AppendDefinition(html, "Google Client Secret", preview.GoogleSecretStatus);
        AppendDefinition(html, "ACS Secret", preview.AcsSecretUnchanged ? "unchanged" : "different");
        AppendDefinition(html, "Live Sending imported", preview.ImportedLiveSending ? "ON" : "OFF");
        AppendDefinition(html, "Live Sending applied", "OFF");
        AppendDefinition(html, "Restart", preview.RestartRequired ? "required" : "not required");
        html.AppendLine("                  </dl>");
        if (preview.GoogleIncluded && preview.GoogleSecretStatus == "missing")
        {
            html.AppendLine("                  <p class=\"ops-meta\">BackupにGoogle Client Secretがありません。復元時は既存のtarget secretを保持します。</p>");
        }

        html.AppendLine("                  <h2 class=\"ops-heading\">Senders</h2>");
        html.AppendLine("                  <p class=\"ops-meta\">Create: ");
        html.Append(preview.Senders.Count(item => item.Action == "create").ToString(CultureInfo.InvariantCulture));
        html.Append("、display-name change: ");
        html.Append(preview.Senders.Count(item => item.CurrentDisplayName != item.ImportedDisplayName && item.Action != "create").ToString(CultureInfo.InvariantCulture));
        html.Append("、enable/disable change: ");
        html.Append(preview.Senders.Count(item => item.CurrentEnabled is not null && item.CurrentEnabled != item.ImportedEnabled).ToString(CultureInfo.InvariantCulture));
        html.Append("、current-only preserved: ");
        html.Append(preview.LocalOnlySenderCount.ToString(CultureInfo.InvariantCulture));
        html.AppendLine("</p>");
        html.AppendLine("                  <table class=\"ops-table\"><thead><tr><th>Email</th><th>Display name</th><th>Enabled</th><th>Action</th></tr></thead><tbody>");
        foreach (var sender in preview.Senders)
        {
            html.Append("                    <tr><td>");
            html.Append(Html(sender.Email));
            html.Append("</td><td>");
            html.Append(Html(DisplayValue(sender.CurrentDisplayName)));
            html.Append(" → ");
            html.Append(Html(DisplayValue(sender.ImportedDisplayName)));
            html.Append("</td><td>");
            html.Append(sender.CurrentEnabled is null ? "—" : OnOff(sender.CurrentEnabled.Value));
            html.Append(" → ");
            html.Append(OnOff(sender.ImportedEnabled));
            html.Append("</td><td>");
            html.Append(Html(sender.Action));
            html.AppendLine("</td></tr>");
        }

        html.AppendLine("                  </tbody></table>");
        html.AppendLine("                </section>");
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Restore confirmation\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Restore</h2>");
        html.AppendLine("                  <p class=\"ops-meta\">パスフレーズはもう一度入力します。復元後にLive Sendingを自動でONにはしません。</p>");
        html.AppendLine("                  <form method=\"post\" action=\"/admin/settings-backup/restore\" class=\"stack-form\">");
        AppendCsrf(html, csrfToken);
        html.Append("                    <input type=\"hidden\" name=\"encrypted_ciphertext\" value=\"");
        html.Append(Html(encryptedCiphertext));
        html.AppendLine("\">");
        AppendPassphraseInput(html, "passphrase", "パスフレーズ再入力");
        html.AppendLine("                    <label><input type=\"checkbox\" name=\"confirmation\" value=\"restore\" required> 上記の差分を適用し、Live SendingをOFFにすることを確認します。</label>");
        html.AppendLine("                    <button type=\"submit\">確認してRestore</button>");
        html.AppendLine("                  </form>");
        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static string RenderRestoreResult(
        AdminTenantAccess access,
        int deadLetterCount,
        bool restartRequired)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "設定backup Restore - Amane Admin",
            AdminNavItem.SettingsBackup,
            deadLetterCount,
            access);
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Restore result\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">Restore完了</h1>");
        html.AppendLine("                  <p class=\"ops-meta\" role=\"status\">設定backupを復元しました。Live SendingはOFFです。</p>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Live Sending", "OFF");
        AppendDefinition(html, "Restart", restartRequired ? "required" : "not required");
        html.AppendLine("                  </dl>");
        html.AppendLine("                  <p class=\"ops-meta\">Restartが必要な場合はMailer container / processを手動で再起動してください。</p>");
        html.AppendLine("                  <p><a href=\"/admin/settings-backup\">設定backupへ戻る</a></p>");
        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendCsrf(StringBuilder html, string csrfToken)
    {
        html.Append("                    <input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"");
        html.Append(csrfToken);
        html.AppendLine("\">");
    }

    private static void AppendPassphraseInput(StringBuilder html, string name, string label)
    {
        html.AppendLine("                    <label>");
        html.Append("                      <span>");
        html.Append(Html(label));
        html.AppendLine("</span>");
        html.Append("                      <input name=\"");
        html.Append(Html(name));
        html.AppendLine("\" type=\"password\" autocomplete=\"new-password\" required minlength=\"12\" maxlength=\"1024\" value=\"\">");
        html.AppendLine("                    </label>");
    }

    private static void AppendDefinition(StringBuilder html, string term, string value)
    {
        html.Append("                    <dt>");
        html.Append(Html(term));
        html.Append("</dt><dd>");
        html.Append(Html(value));
        html.AppendLine("</dd>");
    }

    private static string DisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未設定" : value;

    private static string OnOff(bool enabled) => enabled ? "ON" : "OFF";

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);

    private sealed record RestoreTargetState(
        InstanceConfigurationRow Configuration,
        string AcsSecret,
        AdminGoogleSettingsStatus GoogleStatus,
        string? GoogleSecretPath,
        string? GoogleTargetSecret,
        bool GoogleTargetConfigured,
        IReadOnlyList<SenderSummary> Senders);

    private sealed record SenderPreview(
        string Email,
        string? CurrentDisplayName,
        string? ImportedDisplayName,
        bool? CurrentEnabled,
        bool ImportedEnabled,
        string Action);

    private sealed record PreviewData(
        bool ImportedLiveSending,
        bool AcsSecretUnchanged,
        bool GoogleIncluded,
        bool CurrentGoogleEnabled,
        bool ImportedGoogleEnabled,
        string CurrentGoogleClientId,
        string? ImportedGoogleClientId,
        string GoogleSecretStatus,
        IReadOnlyList<SenderPreview> Senders,
        int LocalOnlySenderCount,
        bool RestartRequired);
}
