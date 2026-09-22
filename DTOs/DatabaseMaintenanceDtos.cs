namespace ebs50_backend.DTOs;

public sealed record DatabaseBackupManifest(int FormatVersion, int SchemaVersion, string ApplicationVersion,
    DateTime CreatedAtUtc, string DatabaseSha256, Dictionary<string, long> TableCounts, long UnfinishedJobCount = 0);

public sealed record DatabaseOperation(Guid Id, string Kind, string Status, string Stage,
    DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DatabaseBackupManifest? Preview = null,
    string? Error = null, string? RecoveryBackup = null);

public sealed record DatabaseMaintenanceStatus(bool Maintenance, bool SyncPaused);
