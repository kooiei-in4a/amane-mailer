using System.Text.Json;
using Amane.Mailer.Api;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests.Api;

public sealed class MailRequestHttpErrorMapperTests
{
    [Fact]
    public void FromBodyReadFailure_maps_too_large_to_413()
    {
        var result = MailRequestHttpErrorMapper.FromBodyReadFailure(MailRequestBodyReadFailure.TooLarge);

        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, statusCode);
        Assert.Contains(MailerErrorCodes.RequestTooLarge, body, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJsonReadFailure_maps_duplicate_property_to_400()
    {
        var result = MailRequestHttpErrorMapper.FromJsonReadFailure(MailRequestJsonReadFailure.DuplicateProperty);

        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Contains("duplicate JSON property", body, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageFull_returns_503_not_retryable()
    {
        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(MailRequestHttpErrorMapper.StorageFull());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.Contains(MailerErrorCodes.StorageFull, body, StringComparison.Ordinal);
        Assert.Contains("\"retryable\":false", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceUnavailable_returns_503_retryable()
    {
        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(
            MailRequestHttpErrorMapper.ServiceUnavailable());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.Contains(MailerErrorCodes.MailerTemporarilyUnavailable, body, StringComparison.Ordinal);
        Assert.Contains("\"retryable\":true", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsStorageFullDatabaseException_detects_sqlite_full()
    {
        var ex = new SqliteException("database or disk is full", SqliteDatabaseExceptionClassifier.SqliteFull);
        Assert.True(MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex));
        Assert.False(MailRequestHttpErrorMapper.IsTransientDatabaseException(ex));
    }

    [Fact]
    public void IsTransientDatabaseException_detects_busy()
    {
        var ex = new SqliteException("database is locked", SqliteDatabaseExceptionClassifier.SqliteBusy);
        Assert.True(MailRequestHttpErrorMapper.IsTransientDatabaseException(ex));
        Assert.False(MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex));
    }

    [Fact]
    public void StatusOk_maps_delivery_status()
    {
        var row = new MailRequestStatusRow(
            MailRequestId: Guid.NewGuid(),
            Status: MailRequestState.Queued,
            AttemptCount: 0,
            MaxAttempts: 3,
            NextAttemptAt: null,
            ScheduledAt: null,
            AcceptedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            LastErrorCode: null);

        var result = MailRequestHttpErrorMapper.StatusOk(row);
        var (statusCode, _) = MailRequestHttpResultAssertions.Inspect(result);
        var response = MailRequestHttpResultAssertions.Value<MailRequestStatusResponse>(result);

        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal(MailRequestStatus.Queued, response.Status);
        Assert.Equal(row.MailRequestId, response.MailRequestId);
    }
}
