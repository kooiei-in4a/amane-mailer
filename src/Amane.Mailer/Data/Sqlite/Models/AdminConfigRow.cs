namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminConfigRow(string AppliedPasswordHash, int CredentialEpoch);
