namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record WorkerHeartbeat(string Name, DateTimeOffset LastHeartbeatAt);
