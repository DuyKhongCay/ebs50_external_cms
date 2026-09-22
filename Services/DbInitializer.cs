using ebs50_backend.Data;
using ebs50_backend.Models;
using Microsoft.EntityFrameworkCore;
using ebs50_backend.Services.Database;

namespace ebs50_backend.Services;

/// <summary>
/// Handles database bootstrapping, default data seeding, and migration of legacy CSV data.
/// Adheres to csharp-async best practices with non-blocking I/O and cancellation support.
/// </summary>
public class DbInitializer(
    AppDbContext context,
    ILogger<DbInitializer> logger,
    IWebHostEnvironment env,
    DatabaseMaintenanceService maintenance) : IDbInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // NOTE: Create SQLite database file and tables if not already present
            await context.Database.EnsureCreatedAsync(cancellationToken);

            await MigrateSchemaAsync(cancellationToken);
            // An intentionally empty restored database must stay empty, including across later restarts.
            if (maintenance.HasRestoredDatabase) return;
            await SeedMachineStatesAsync(cancellationToken);
            await SeedSystemSettingsAsync(cancellationToken);
            await SeedLegacyDataFromCsvAsync(cancellationToken);

            logger.LogInformation("Database initialization and data seeding completed successfully.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Database initialization was canceled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unhandled exception occurred during database initialization.");
            throw;
        }
    }

    private async Task SeedMachineStatesAsync(CancellationToken cancellationToken)
    {
        // NOTE: Skip if machine states are already populated
        if (await context.MachineStates.AnyAsync(cancellationToken))
        {
            return;
        }

        var defaultStates = new List<MachineState>
        {
            new()
            {
                StateCode = 0,
                StateNameVi = "Máy đang hoạt động",
                StateNameKo = "*가동 중",
                IconFileName = "running.png",
                ThemeColor = "Black"
            },
            new()
            {
                StateCode = 1,
                StateNameVi = "Không có kế hoạch SX",
                StateNameKo = "*생산 계획 없음",
                IconFileName = "no_plan.png",
                ThemeColor = "Black"
            },
            new()
            {
                StateCode = 2,
                StateNameVi = "Thiếu linh kiện",
                StateNameKo = "*투입 자재 없음",
                IconFileName = "no_input.png",
                ThemeColor = "Black"
            },
            new()
            {
                StateCode = 3,
                StateNameVi = "Sự cố máy, chờ sửa",
                StateNameKo = "*설비 고장, 수리 대기",
                IconFileName = "broken.png",
                ThemeColor = "Red"
            },
            new()
            {
                StateCode = 4,
                StateNameVi = "Đang đổi model",
                StateNameKo = "*설비 모델 변경",
                IconFileName = "change_model.png",
                ThemeColor = "Black"
            }
        };

        await context.MachineStates.AddRangeAsync(defaultStates, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} machine states.", defaultStates.Count);
    }

    private async Task SeedSystemSettingsAsync(CancellationToken cancellationToken)
    {
        if (await context.SystemSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        var defaultSettings = new List<SystemSetting>
        {
            new() { Key = "Ebs50Host", Value = "192.168.1.50", Description = "IP address or hostname of the EBS-50 base station" },
            new() { Key = "Ebs50Port", Value = "22", Description = "SSH/SFTP port on EBS-50" },
            new() { Key = "Ebs50Username", Value = "root", Description = "SFTP username" },
            new() { Key = "Ebs50Password", Value = "root", Description = "SFTP password" },
            new() { Key = "RemoteInputPath", Value = "/home/root/ebs_50_run/Input", Description = "Input folder monitored by EBS-50 CMS" },
            new() { Key = "AutoSyncOnStateChange", Value = "true", Description = "Automatically dispatch new XML/image when state changes" }
        };

        await context.SystemSettings.AddRangeAsync(defaultSettings, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded default system settings.");
    }

    private async Task SeedLegacyDataFromCsvAsync(CancellationToken cancellationToken)
    {
        if (await context.EslTags.AnyAsync(cancellationToken))
        {
            return;
        }

        // Search for backup_20260915_100710 folder upwards
        var currentDir = new DirectoryInfo(env.ContentRootPath);
        string? legacyLinksPath = null;
        while (currentDir != null)
        {
            var candidate = Path.Combine(currentDir.FullName, "backup_20260915_100710", "Input", "links.csv");
            if (File.Exists(candidate))
            {
                legacyLinksPath = candidate;
                break;
            }
            currentDir = currentDir.Parent;
        }

        if (string.IsNullOrEmpty(legacyLinksPath) || !File.Exists(legacyLinksPath))
        {
            logger.LogWarning("Legacy links.csv file not found from root: {Path}", env.ContentRootPath);
            return;
        }

        using var reader = new StreamReader(legacyLinksPath);
        string? line;
        var tagsToInsert = new List<EslTag>();
        var modelsToInsert = new Dictionary<string, ModelItem>(StringComparer.OrdinalIgnoreCase);

        await reader.ReadLineAsync(cancellationToken);

        // PERF: Stream CSV line by line to minimize memory allocations on large legacy datasets
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',');
            if (parts.Length < 3) continue;

            var modelCode = parts[0].Trim();
            var macAddress = parts[2].Trim();

            if (string.IsNullOrWhiteSpace(modelCode) || string.IsNullOrWhiteSpace(macAddress)) continue;

            if (!modelsToInsert.ContainsKey(modelCode))
            {
                modelsToInsert[modelCode] = new ModelItem
                {
                    ModelCode = modelCode,
                    Description = "Migrated from backup links.csv",
                    UpdatedAt = DateTime.UtcNow
                };
            }

            // NOTE: Default machine location to ModelCode during initial legacy migration
            tagsToInsert.Add(new EslTag
            {
                MacAddress = macAddress,
                MachineNo = modelCode,
                ModelCode = modelCode,
                CurrentStateCode = 0,
                BatteryLevel = 100,
                SyncStatus = "Pending"
            });
        }

        // NOTE: Save ModelItems first to satisfy foreign key constraints before inserting EslTags
        await context.ModelItems.AddRangeAsync(modelsToInsert.Values, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        await context.EslTags.AddRangeAsync(tagsToInsert, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Migrated {ModelCount} models and {TagCount} tags from legacy links.csv.",
            modelsToInsert.Count, tagsToInsert.Count);
    }

    private async Task MigrateSchemaAsync(CancellationToken cancellationToken)
    {
        var createDispatchJobsTableSql = """
            CREATE TABLE IF NOT EXISTS "DispatchJobs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_DispatchJobs" PRIMARY KEY,
                "MacAddress" TEXT NOT NULL,
                "ModelCode" TEXT NOT NULL,
                "StateCode" INTEGER NOT NULL,
                "MachineNo" TEXT NOT NULL,
                "OverrideThemeColor" TEXT NULL,
                "DesiredRevision" INTEGER NOT NULL,
                "BindingVersion" INTEGER NOT NULL,
                "Status" TEXT NOT NULL DEFAULT 'Pending',
                "AttemptCount" INTEGER NOT NULL DEFAULT 0,
                "LastError" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "NextAttemptAt" TEXT NOT NULL,
                "UploadedAt" TEXT NULL,
                "ConfirmedAt" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DispatchJobs_Status_NextAttemptAt" ON "DispatchJobs" ("Status", "NextAttemptAt");
            CREATE INDEX IF NOT EXISTS "IX_DispatchJobs_MacAddress_DesiredRevision" ON "DispatchJobs" ("MacAddress", "DesiredRevision");
            """;
        await context.Database.ExecuteSqlRawAsync(createDispatchJobsTableSql, cancellationToken);

        // 2. Ensure new revision columns exist in EslTags table without throwing DbCommand exceptions
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = context.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"EslTags\");";
            await context.Database.OpenConnectionAsync(cancellationToken);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingCols.Add(reader.GetString(1)); // Column 1 is name
            }
        }

        var columns = new[]
        {
            ("DesiredRevision", "INTEGER NOT NULL DEFAULT 1"),
            ("ConfirmedRevision", "INTEGER NOT NULL DEFAULT 0"),
            ("BindingVersion", "INTEGER NOT NULL DEFAULT 1"),
            ("LastUploadedAt", "TEXT NULL"),
            ("LastConfirmedAt", "TEXT NULL"),
            ("Variant", "TEXT NOT NULL DEFAULT 'SE420RY'")
        };

        foreach (var (colName, colDef) in columns)
        {
            if (!existingCols.Contains(colName))
            {
                try
                {
#pragma warning disable EF1002
                    await context.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE \"EslTags\" ADD COLUMN \"{colName}\" {colDef};",
                        cancellationToken);
#pragma warning restore EF1002
                    logger.LogInformation("Added column {Column} to EslTags table.", colName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not add column {Column} to EslTags.", colName);
                }
            }
        }
    }
}
