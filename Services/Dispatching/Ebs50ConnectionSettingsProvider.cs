using ebs50_backend.Data;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Services.Dispatching;

/// <summary>Effective station configuration, with database overrides applied.</summary>
public sealed record ConnectionSettings(string Host, int Port, string Username, string Password, string RemoteInputPath);

/// <summary>Shared source of configuration for SFTP and network diagnostics.</summary>
public interface IEbs50ConnectionSettingsProvider
{
    /// <summary>Reads configuration and database overrides without exposing credentials to the API.</summary>
    Task<ConnectionSettings> GetAsync(CancellationToken cancellationToken);
}

internal sealed class Ebs50ConnectionSettingsProvider(
    IConfiguration configuration, IServiceScopeFactory scopeFactory) : IEbs50ConnectionSettingsProvider
{
    public async Task<ConnectionSettings> GetAsync(CancellationToken cancellationToken)
    {
        // 1. Defaults from appsettings.json
        var host = configuration["Ebs50Settings:Host"] ?? "192.168.1.50";
        var port = int.TryParse(configuration["Ebs50Settings:Port"], out var p) ? p : 22;
        var user = configuration["Ebs50Settings:Username"] ?? "root";
        var pass = configuration["Ebs50Settings:Password"] ?? "";
        var remotePath = configuration["Ebs50Settings:RemoteInputPath"] ?? "/home/root/ebs_50_run/Input";

        // 2. Override from SQLite SystemSettings table if available
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
                case "Ebs50Password":
                    pass = s.Value;
                    break;
                case "RemoteInputPath" when !string.IsNullOrWhiteSpace(s.Value):
                    remotePath = s.Value.Trim();
                    break;
            }
        }

        return new ConnectionSettings(host, port, user, pass, remotePath);
    }

}
