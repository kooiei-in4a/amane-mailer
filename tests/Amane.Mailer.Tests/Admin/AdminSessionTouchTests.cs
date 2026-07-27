using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSessionTouchIntervalTests
{
    [Theory]
    [InlineData(60, 15)]   // 1 minute idle → 15s interval
    [InlineData(120, 30)]  // 2 minutes → 30s
    [InlineData(240, 60)]  // 4 minutes → 1 minute cap
    [InlineData(1800, 60)] // 30 minutes → 1 minute cap
    public void ResolveInterval_uses_min_of_one_minute_and_quarter_idle(
        int idleSeconds,
        int expectedIntervalSeconds)
    {
        var interval = AdminSessionTouch.ResolveInterval(TimeSpan.FromSeconds(idleSeconds));
        Assert.Equal(TimeSpan.FromSeconds(expectedIntervalSeconds), interval);
        Assert.True(interval < TimeSpan.FromSeconds(idleSeconds));
    }
}

public sealed class SqliteTimeOrderingTests
{
    [Fact]
    public void Storage_format_lexicographic_order_matches_chronological_utc_order()
    {
        var earlier = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var later = earlier.AddSeconds(1);
        var muchLater = earlier.AddHours(2).AddMilliseconds(3);

        var earlierText = SqliteTime.ToStorageUtc(earlier);
        var laterText = SqliteTime.ToStorageUtc(later);
        var muchLaterText = SqliteTime.ToStorageUtc(muchLater);

        Assert.Equal(SqliteTime.StorageFormat, "yyyy-MM-ddTHH:mm:ss.fffffffZ");
        Assert.True(
            string.CompareOrdinal(earlierText, laterText) < 0,
            "Earlier UTC instant must sort before a later instant as TEXT.");
        Assert.True(string.CompareOrdinal(laterText, muchLaterText) < 0);
        Assert.Equal(earlier, SqliteTime.FromStorage(earlierText));
        Assert.Equal(later, SqliteTime.FromStorage(laterText));
    }
}

public sealed class AdminSessionRepositoryTouchTests
{
    [Fact]
    public async Task TryTouch_does_not_regress_last_seen_when_older_timestamp_arrives_later()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var sessionId = await CreateSessionAsync(repository, db.Now, ct);

        var t1 = db.Now.AddMinutes(1);
        var t2 = db.Now.AddMinutes(2);
        var interval = TimeSpan.Zero;

        var newer = await repository.TryTouchAsync(
            sessionId,
            t2,
            t2.AddMinutes(30),
            interval,
            ct);
        var older = await repository.TryTouchAsync(
            sessionId,
            t1,
            t1.AddMinutes(30),
            interval,
            ct);

        Assert.NotNull(newer);
        Assert.Equal(t2, newer.LastSeenAt);
        Assert.Null(older);

        var session = await repository.GetSessionAsync(sessionId, ct);
        Assert.NotNull(session);
        Assert.Equal(t2, session.LastSeenAt);
        Assert.Equal(t2.AddMinutes(30), session.IdleExpiresAt);
    }

    [Fact]
    public async Task TryTouch_does_not_regress_idle_expires_when_older_proposed_idle_arrives_later()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var sessionId = await CreateSessionAsync(repository, db.Now, ct);

        var t2 = db.Now.AddMinutes(2);
        var t2Idle = t2.AddMinutes(30);
        Assert.NotNull(await repository.TryTouchAsync(sessionId, t2, t2Idle, TimeSpan.Zero, ct));

        // Later last_seen with a lower proposed idle must not pull idle_expires_at backward.
        var t3 = t2.AddMinutes(1);
        var lowerIdle = t2Idle.AddMinutes(-5);
        var touch = await repository.TryTouchAsync(sessionId, t3, lowerIdle, TimeSpan.Zero, ct);

        Assert.NotNull(touch);
        Assert.Equal(t3, touch.LastSeenAt);
        Assert.Equal(t2Idle, touch.IdleExpiresAt);

        var session = await repository.GetSessionAsync(sessionId, ct);
        Assert.Equal(t3, session!.LastSeenAt);
        Assert.Equal(t2Idle, session.IdleExpiresAt);
    }

    [Fact]
    public async Task TryTouch_same_timestamp_preserves_existing_values()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var sessionId = await CreateSessionAsync(repository, db.Now, ct);

        var t1 = db.Now.AddMinutes(1);
        var idle = t1.AddMinutes(30);
        Assert.NotNull(await repository.TryTouchAsync(sessionId, t1, idle, TimeSpan.Zero, ct));

        await SetLastSeenAsync(db.ConnectionString, sessionId, db.Now, ct);
        var again = await repository.TryTouchAsync(sessionId, t1, idle, TimeSpan.Zero, ct);

        Assert.NotNull(again);
        Assert.Equal(t1, again.LastSeenAt);
        Assert.Equal(idle, again.IdleExpiresAt);
    }

    [Fact]
    public async Task TryTouch_caps_idle_expires_at_absolute_expires()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var absolute = db.Now.AddMinutes(40);
        var sessionId = await CreateSessionAsync(
            repository,
            db.Now,
            ct,
            absoluteExpiresAt: absolute,
            idleExpiresAt: db.Now.AddMinutes(30));

        var now = db.Now.AddMinutes(20);
        var proposedIdle = now.AddMinutes(30); // beyond absolute
        var touch = await repository.TryTouchAsync(sessionId, now, proposedIdle, TimeSpan.Zero, ct);

        Assert.NotNull(touch);
        Assert.Equal(absolute, touch.IdleExpiresAt);
        Assert.Equal(absolute, touch.AbsoluteExpiresAt);
        Assert.True(touch.IdleExpiresAt <= touch.AbsoluteExpiresAt);
    }

    [Fact]
    public async Task TryTouch_does_not_update_revoked_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var sessionId = await CreateSessionAsync(repository, db.Now, ct);

        await repository.RevokeSessionAsync(
            sessionId,
            AdminSessionRevokeReasons.Logout,
            db.Now.AddSeconds(1),
            ct);

        var touch = await repository.TryTouchAsync(
            sessionId,
            db.Now.AddMinutes(1),
            db.Now.AddMinutes(31),
            TimeSpan.Zero,
            ct);

        Assert.Null(touch);
        var session = await repository.GetSessionAsync(sessionId, ct);
        Assert.NotNull(session!.RevokedAt);
        Assert.Equal(db.Now, session.LastSeenAt);
    }

    [Fact]
    public async Task TryTouch_and_revoke_race_leaves_session_revoked_and_untouchable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var sessionId = await CreateSessionAsync(repository, db.Now, ct);
        var barrier = new Barrier(2);
        AdminSessionTouchResult? touchResult = null;

        var revokeTask = Task.Run(async () =>
        {
            barrier.SignalAndWait(ct);
            await repository.RevokeSessionAsync(
                sessionId,
                AdminSessionRevokeReasons.Logout,
                db.Now.AddSeconds(1),
                ct);
        }, ct);

        var touchTask = Task.Run(async () =>
        {
            barrier.SignalAndWait(ct);
            touchResult = await repository.TryTouchAsync(
                sessionId,
                db.Now.AddMinutes(1),
                db.Now.AddMinutes(31),
                TimeSpan.Zero,
                ct);
        }, ct);

        await Task.WhenAll(revokeTask, touchTask);

        var session = await repository.GetSessionAsync(sessionId, ct);
        Assert.NotNull(session!.RevokedAt);
        Assert.Equal(AdminSessionRevokeReasons.Logout, session.RevokeReason);

        // Regardless of which writer ran first, a later touch must not revive the row.
        Assert.Null(await repository.TryTouchAsync(
            sessionId,
            db.Now.AddMinutes(2),
            db.Now.AddMinutes(32),
            TimeSpan.Zero,
            ct));

        if (touchResult is not null)
        {
            // Touch won the race before revoke; timestamps may have advanced, but revoke stuck.
            Assert.True(session.LastSeenAt >= db.Now);
        }
        else
        {
            Assert.Equal(db.Now, session.LastSeenAt);
        }
    }

    [Fact]
    public void Cookie_renewal_is_authoritative_only_for_matching_active_touch()
    {
        var touch = new AdminSessionTouchResult(
            new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 27, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero));

        Assert.True(AdminSessionCookieRenewal.IsStillAuthoritative(
            touch,
            new AdminSessionRow(
                "s",
                "admin",
                touch.LastSeenAt,
                touch.LastSeenAt,
                touch.AbsoluteExpiresAt,
                touch.IdleExpiresAt,
                null,
                null,
                0)));

        Assert.False(AdminSessionCookieRenewal.IsStillAuthoritative(
            touch,
            new AdminSessionRow(
                "s",
                "admin",
                touch.LastSeenAt,
                touch.LastSeenAt.AddMinutes(1),
                touch.AbsoluteExpiresAt,
                touch.IdleExpiresAt.AddMinutes(1),
                null,
                null,
                0)));

        Assert.False(AdminSessionCookieRenewal.IsStillAuthoritative(
            touch,
            new AdminSessionRow(
                "s",
                "admin",
                touch.LastSeenAt,
                touch.LastSeenAt,
                touch.AbsoluteExpiresAt,
                touch.IdleExpiresAt,
                touch.LastSeenAt.AddSeconds(1),
                AdminSessionRevokeReasons.Logout,
                0)));

        Assert.False(AdminSessionCookieRenewal.IsStillAuthoritative(touch, null));
    }

    [Fact]
    public void CreateRenewalProperties_uses_repository_exact_expiry()
    {
        var touch = new AdminSessionTouchResult(
            new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 27, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero));
        var source = new AuthenticationProperties
        {
            AllowRefresh = true,
            IsPersistent = false,
            IssuedUtc = touch.LastSeenAt.AddMinutes(-5),
            ExpiresUtc = touch.LastSeenAt.AddMinutes(25),
        };
        source.Items[AdminAuthenticationConstants.SessionIdProperty] = "session-1";

        var renew = AdminSessionCookieRenewal.CreateRenewalProperties(source, touch);

        Assert.Equal(touch.LastSeenAt, renew.IssuedUtc);
        Assert.Equal(touch.IdleExpiresAt, renew.ExpiresUtc);
        Assert.Equal("session-1", renew.Items[AdminAuthenticationConstants.SessionIdProperty]);
        Assert.False(renew.IsPersistent);
    }

    [Fact]
    public async Task TryTouch_skips_within_interval_and_updates_after_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var sessionId = await CreateSessionAsync(repository, db.Now, ct);
        var interval = TimeSpan.FromMinutes(1);

        var within = db.Now.AddSeconds(30);
        Assert.Null(await repository.TryTouchAsync(
            sessionId,
            within,
            within.AddMinutes(30),
            interval,
            ct));

        var boundary = db.Now + interval;
        var atBoundary = await repository.TryTouchAsync(
            sessionId,
            boundary,
            boundary.AddMinutes(30),
            interval,
            ct);
        Assert.NotNull(atBoundary);
        Assert.Equal(boundary, atBoundary.LastSeenAt);

        var after = boundary.AddSeconds(1);
        Assert.Null(await repository.TryTouchAsync(
            sessionId,
            after,
            after.AddMinutes(30),
            interval,
            ct));

        var elapsed = boundary + interval;
        var later = await repository.TryTouchAsync(
            sessionId,
            elapsed,
            elapsed.AddMinutes(30),
            interval,
            ct);
        Assert.NotNull(later);
        Assert.Equal(elapsed, later.LastSeenAt);
    }

    [Fact]
    public async Task TryTouch_parallel_eligible_requests_update_at_most_once_per_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SessionTestDatabase.CreateAsync(ct);
        var repository = new AdminSessionRepository(db.Factory);
        var sessionId = await CreateSessionAsync(repository, db.Now, ct);
        var interval = TimeSpan.FromMinutes(1);
        var now = db.Now + interval;

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => repository.TryTouchAsync(
                sessionId,
                now,
                now.AddMinutes(30),
                interval,
                ct))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(result => result is not null));

        var session = await repository.GetSessionAsync(sessionId, ct);
        Assert.Equal(now, session!.LastSeenAt);
    }

    private static async Task<string> CreateSessionAsync(
        AdminSessionRepository repository,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        DateTimeOffset? absoluteExpiresAt = null,
        DateTimeOffset? idleExpiresAt = null)
    {
        var sessionId = AdminSessionIds.CreateNew();
        await repository.CreateSessionAsync(
            new AdminSessionRow(
                sessionId,
                "admin",
                now,
                now,
                absoluteExpiresAt ?? now.AddHours(12),
                idleExpiresAt ?? now.AddMinutes(30),
                null,
                null,
                0),
            maxConcurrentSessions: 3,
            cancellationToken);
        return sessionId;
    }

    private static async Task SetLastSeenAsync(
        string connectionString,
        string sessionId,
        DateTimeOffset lastSeenAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_sessions
            SET last_seen_at = @LastSeenAt
            WHERE session_id = @SessionId;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@LastSeenAt", SqliteTime.ToStorageUtc(lastSeenAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class SessionTestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private SessionTestDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
            Now = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public DateTimeOffset Now { get; }

        public static async Task<SessionTestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-session-touch", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";

            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());

            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new SessionTestDatabase(root, factory, connectionString);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);

            return ValueTask.CompletedTask;
        }
    }
}
