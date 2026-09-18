using System.Text;
using ebs50_backend.Data;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Manages automatic generation and synchronization of the links.csv mapping file with the EBS-50 station.
/// Follows csharp-async principles and safeguards the EBS-50 SQLite schema constraints.
/// </summary>
public sealed class Ebs50LinkSyncService(
    IServiceScopeFactory scopeFactory,
    IWebHostEnvironment env,
    ILogger<Ebs50LinkSyncService> logger) : IEbs50LinkSyncService
{
    public async Task SyncAllLinksAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var tags = await dbContext.EslTags
                .AsNoTracking()
                .OrderBy(t => t.ModelCode)
                .ThenBy(t => t.MacAddress)
                .ToListAsync(cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine("#ID,Variant,MAC,Layer");

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag.MacAddress) || string.IsNullOrWhiteSpace(tag.ModelCode))
                {
                    continue;
                }

                var cleanMac = tag.MacAddress.Trim().ToUpperInvariant();
                var cleanModel = tag.ModelCode.Trim();
                var variant = string.IsNullOrWhiteSpace(tag.Variant) ? "SE420RY" : tag.Variant.Trim();

                // NOTE: Trailing comma is crucial! It tells the Opticon CSV parser that Layer is an empty
                // string ("") instead of null, satisfying the SQLite PRIMARY KEY(MAC, Layer) non-null requirement.
                sb.AppendLine($"{cleanModel},{variant},{cleanMac},");
            }

            var csvContent = sb.ToString();

            // Save local audit copy
            await SaveLocalCopyAsync(csvContent, cancellationToken);

            // NOTE: Do NOT upload links.csv to EBS-50 in External CMS mode.
            // Uploading links.csv without corresponding dbase.csv causes Opticon SQLite 'Column Layer does not allow nulls'
            // and wipes existing station links, triggering tag fallback to default.
            logger.LogInformation("Saved local audit copy of links.csv ({Count} tags). SFTP upload to EBS-50 is disabled in External CMS mode.", tags.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("SyncAllLinksAsync cancelled by caller.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to synchronize links.csv with EBS-50 station.");
            throw;
        }
    }

    private async Task SaveLocalCopyAsync(string csvContent, CancellationToken cancellationToken)
    {
        try
        {
            var localDir = Path.Combine(env.ContentRootPath, "Local_Ebs50_Input");
            Directory.CreateDirectory(localDir);
            var localPath = Path.Combine(localDir, "links.csv");
            await File.WriteAllTextAsync(localPath, csvContent, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save local copy of links.csv");
        }
    }
}

