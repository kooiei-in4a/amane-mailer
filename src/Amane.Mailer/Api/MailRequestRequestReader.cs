using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Amane.Mailer.Json;

namespace Amane.Mailer.Api;

/// <summary>
/// Reads mail-request HTTP bodies: size limit, strict UTF-8, JSON deserialize, duplicate properties.
/// </summary>
public static class MailRequestRequestReader
{
    public const int MaxRequestBodyBytes = 256_000;

    // Reject invalid UTF-8 instead of substituting U+FFFD (#343).
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool IsContentLengthTooLarge(HttpRequest request) =>
        request.ContentLength > MaxRequestBodyBytes;

    public static async Task<MailRequestBodyReadResult> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var totalBytes = 0;

        while (true)
        {
            var bytesRead = await request.Body.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaxRequestBodyBytes)
            {
                return MailRequestBodyReadResult.TooLarge();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        try
        {
            return MailRequestBodyReadResult.Success(StrictUtf8.GetString(buffer.ToArray()));
        }
        catch (DecoderFallbackException)
        {
            return MailRequestBodyReadResult.InvalidUtf8();
        }
    }

    public static MailRequestJsonReadResult<T> DeserializeStrict<T>(
        string requestBody,
        JsonTypeInfo<T> typeInfo)
        where T : class
    {
        T? request;
        try
        {
            request = JsonSerializer.Deserialize(requestBody, typeInfo);
        }
        catch (JsonException)
        {
            return MailRequestJsonReadResult<T>.InvalidJson();
        }

        if (request is null)
        {
            return MailRequestJsonReadResult<T>.BodyRequired();
        }

        if (JsonDuplicatePropertyDetector.HasDuplicateProperty(requestBody))
        {
            return MailRequestJsonReadResult<T>.DuplicateProperty();
        }

        return MailRequestJsonReadResult<T>.Success(request);
    }
}

public readonly struct MailRequestBodyReadResult
{
    private MailRequestBodyReadResult(string? body, MailRequestBodyReadFailure? failure)
    {
        Body = body;
        Failure = failure;
    }

    public string? Body { get; }

    public MailRequestBodyReadFailure? Failure { get; }

    public bool Succeeded => Failure is null;

    public static MailRequestBodyReadResult Success(string body) => new(body, null);

    public static MailRequestBodyReadResult TooLarge() =>
        new(null, MailRequestBodyReadFailure.TooLarge);

    public static MailRequestBodyReadResult InvalidUtf8() =>
        new(null, MailRequestBodyReadFailure.InvalidUtf8);
}

public enum MailRequestBodyReadFailure
{
    TooLarge,
    InvalidUtf8,
}

public readonly struct MailRequestJsonReadResult<T>
    where T : class
{
    private MailRequestJsonReadResult(T? value, MailRequestJsonReadFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public MailRequestJsonReadFailure? Failure { get; }

    public bool Succeeded => Failure is null;

    public static MailRequestJsonReadResult<T> Success(T value) => new(value, null);

    public static MailRequestJsonReadResult<T> InvalidJson() =>
        new(null, MailRequestJsonReadFailure.InvalidJson);

    public static MailRequestJsonReadResult<T> BodyRequired() =>
        new(null, MailRequestJsonReadFailure.BodyRequired);

    public static MailRequestJsonReadResult<T> DuplicateProperty() =>
        new(null, MailRequestJsonReadFailure.DuplicateProperty);
}

public enum MailRequestJsonReadFailure
{
    InvalidJson,
    BodyRequired,
    DuplicateProperty,
}
