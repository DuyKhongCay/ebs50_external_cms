using ebs50_backend.Data;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Queries tag status directly from the Opticon EBS-50 station via SSH/SQLite.
/// </summary>
public sealed class Ebs50StatusService(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<Ebs50StatusService> logger) : IEbs50StatusService
{
    public async Task<IReadOnlyDictionary<string, Ebs50LabelStatus>> GetLabelStatusesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetConnectionSettingsAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10-second timeout for status check

        try
        {
            var authMethod = string.IsNullOrEmpty(settings.Password)
                ? new PasswordAuthenticationMethod(settings.Username, "")
                : new PasswordAuthenticationMethod(settings.Username, settings.Password);

            var connInfo = new Renci.SshNet.ConnectionInfo(settings.Host, settings.Port, settings.Username, authMethod)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            using var sshClient = new SshClient(connInfo);
            await sshClient.ConnectAsync(cts.Token);

            // NOTE: Query Opticon station internal SQLite DB via SSH to inspect actual RF transmission
            // status and display acknowledgement per MAC (STATUS, IMAGE_ID vs IMAGE_ID_LOCAL).
            var cmdText = "sqlite3 /home/root/ebs_50_run/Output/esl.sqlite3 \"SELECT MAC, IMAGE_FILE, IMAGE_ID, IMAGE_ID_LOCAL, STATUS FROM labelstatus;\"";
            var cmd = sshClient.CreateCommand(cmdText);

            // NOTE: Adapt SSH.NET APM pattern (BeginExecute/EndExecute) to Task to avoid blocking thread pool threads
            var output = await Task.Factory.FromAsync(
                (callback, state) => cmd.BeginExecute(callback, state),
                iar => cmd.EndExecute(iar),
                null);

            sshClient.Disconnect();

            var result = new Dictionary<string, Ebs50LabelStatus>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(output))
            {
                return result;
            }

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length < 5) continue;

                var mac = parts[0].Trim().ToUpperInvariant();
                var imageFile = parts[1].Trim();
                _ = int.TryParse(parts[2].Trim(), out var imageId);
                _ = int.TryParse(parts[3].Trim(), out var imageIdLocal);
                _ = int.TryParse(parts[4].Trim(), out var status);

                result[mac] = new Ebs50LabelStatus(mac, imageFile, imageId, imageIdLocal, status);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("GetLabelStatusesAsync cancelled by caller.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query tag statuses from EBS-50 at {Host}", settings.Host);
            return new Dictionary<string, Ebs50LabelStatus>();
        }
    }

    private async Task<StatusConnectionSettings> GetConnectionSettingsAsync(CancellationToken cancellationToken)
    {
        var host = configuration["Ebs50Settings:Host"] ?? "192.168.11.36";
        var port = int.TryParse(configuration["Ebs50Settings:Port"], out var p) ? p : 22;
        var user = configuration["Ebs50Settings:Username"] ?? "root";
        var pass = configuration["Ebs50Settings:Password"] ?? "root";

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbSettings = await db.SystemSettings
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var s in dbSettings)
        {
            switch (s.Key)
            {
                case "Ebs50Host" when !string.IsNullOrWhiteSpace(s.Value):
                    host = s.Value.Trim();
                    break;
                case "Ebs50Port" when int.TryParse(s.Value, out var dp):
                    port = dp;
                    break;
                case "Ebs50Username" when !string.IsNullOrWhiteSpace(s.Value):
                    user = s.Value.Trim();
                    break;
                case "Ebs50Password" when !string.IsNullOrWhiteSpace(s.Value):
                    pass = s.Value;
                    break;
            }
        }

        return new StatusConnectionSettings(host, port, user, pass);
    }

    private sealed record StatusConnectionSettings(string Host, int Port, string Username, string Password);
}
