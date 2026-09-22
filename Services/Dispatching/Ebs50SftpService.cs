using System.Net.Sockets;
using System.Text;
using ebs50_backend.Data;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Handles SFTP connectivity, authentication, and atomic file dispatching to the Opticon EBS-50 server.
/// </summary>
internal sealed class Ebs50SftpService(
    IEbs50ConnectionSettingsProvider settingsProvider,
    IWebHostEnvironment env,
    ILogger<Ebs50SftpService> logger) : IEbs50SftpService
{
    public async Task<SftpTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15)); // Hard timeout

        try
        {
            using var client = CreateSftpClient(settings);
            await client.ConnectAsync(cts.Token);

            bool pathExists = await client.ExistsAsync(settings.RemoteInputPath, cts.Token);

            client.Disconnect();

            return new SftpTestResult(
                Success: true,
                Message: pathExists
                    ? $"Kết nối thành công tới EBS-50 tại {settings.Host}:{settings.Port}. Thư mục Input tồn tại."
                    : $"Kết nối thành công tới {settings.Host}:{settings.Port}, nhưng thư mục '{settings.RemoteInputPath}' chưa tồn tại.",
                Host: settings.Host,
                Port: settings.Port,
                RemotePath: settings.RemoteInputPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("TestConnectionAsync cancelled by caller.");
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Connection to EBS-50 timed out after 15s at {Host}:{Port}", settings.Host, settings.Port);
            return new SftpTestResult(false, $"Quá thời gian chờ kết nối tới {settings.Host}:{settings.Port} (Timeout 15s).", settings.Host, settings.Port, settings.RemoteInputPath);
        }
        catch (SocketException ex)
        {
            logger.LogWarning("Network socket error connecting to EBS-50: {Message}", ex.Message);
            return new SftpTestResult(false, $"Không thể kết nối mạng tới {settings.Host}:{settings.Port}. Kiểm tra cáp mạng LAN hoặc IP router.", settings.Host, settings.Port, settings.RemoteInputPath);
        }
        catch (SshAuthenticationException ex)
        {
            logger.LogWarning("SSH Authentication error on EBS-50: {Message}", ex.Message);
            return new SftpTestResult(false, $"Xác thực SSH thất bại trên {settings.Host} (User: {settings.Username}). Kiểm tra lại mật khẩu.", settings.Host, settings.Port, settings.RemoteInputPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to EBS-50 router.");
            return new SftpTestResult(false, $"Lỗi kết nối: {ex.Message}", settings.Host, settings.Port, settings.RemoteInputPath);
        }
    }

    public async Task UploadTagPayloadAsync(
        string macAddress,
        string imageFileName,
        byte[] imageBytes,
        string xmlContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFileName);
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlContent);

        var settings = await settingsProvider.GetAsync(cancellationToken);

        // NOTE: Always save locally first to guarantee audit copy and local fallback inspection
        await SaveLocalCopyAsync(macAddress, imageFileName, imageBytes, xmlContent, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20)); // Hard timeout per upload operation

        try
        {
            using var client = CreateSftpClient(settings);
            await client.ConnectAsync(cts.Token);

            // Ensure remote input directory exists
            bool inputDirExists = await client.ExistsAsync(settings.RemoteInputPath, cts.Token);
            if (!inputDirExists)
            {
                await CreateRemoteDirectoryRecursivelyAsync(client, settings.RemoteInputPath, cts.Token);
            }

            var cleanMac = macAddress.Trim().ToUpperInvariant();
            var remoteImagePath = $"{settings.RemoteInputPath.TrimEnd('/')}/{imageFileName}";

            // Standard Opticon External CMS XML filename format is <MAC>.xml.
            // The XML content references the dynamic image filename (e.g. <MAC>_<timestamp>.png).
            var xmlFileName = $"{cleanMac}.xml";
            var remoteXmlPath = $"{settings.RemoteInputPath.TrimEnd('/')}/{xmlFileName}";

            // NOTE: Strict dispatch ordering: upload PNG image first so it is completely flushed on disk.
            // The EBS-50 inotify daemon triggers upon XML file arrival; uploading XML first would cause a partial read race.
            using (var imgStream = new MemoryStream(imageBytes))
            {
                await client.UploadFileAsync(imgStream, remoteImagePath, canOverride: true, cancellationToken: cts.Token);
            }

            var xmlBytes = Encoding.UTF8.GetBytes(xmlContent);
            using (var xmlStream = new MemoryStream(xmlBytes))
            {
                await client.UploadFileAsync(xmlStream, remoteXmlPath, canOverride: true, cancellationToken: cts.Token);
            }

            client.Disconnect();
            logger.LogInformation("Successfully dispatched {Mac} payload (Image: {Image}, XML: {Xml}) to EBS-50 at {Host}:{Path}",
                cleanMac, imageFileName, xmlFileName, settings.Host, settings.RemoteInputPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("UploadTagPayloadAsync for {Mac} cancelled by caller.", macAddress);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("UploadTagPayloadAsync for {Mac} timed out after 20s.", macAddress);
            throw new TimeoutException($"Quá thời gian chờ tải lên SFTP cho thẻ {macAddress} (Timeout 20s).", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload tag {Mac} payload via SFTP to {Host}. Local copy preserved.",
                macAddress, settings.Host);
            throw;
        }
    }

    public async Task UploadLinksCsvAsync(string csvContent, CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using var client = CreateSftpClient(settings);
            await client.ConnectAsync(cts.Token);

            bool inputDirExists = await client.ExistsAsync(settings.RemoteInputPath, cts.Token);
            if (!inputDirExists)
            {
                await CreateRemoteDirectoryRecursivelyAsync(client, settings.RemoteInputPath, cts.Token);
            }

            var remoteCsvPath = $"{settings.RemoteInputPath.TrimEnd('/')}/links.csv";
            var csvBytes = Encoding.UTF8.GetBytes(csvContent);
            using (var stream = new MemoryStream(csvBytes))
            {
                await client.UploadFileAsync(stream, remoteCsvPath, canOverride: true, cancellationToken: cts.Token);
            }

            client.Disconnect();
            logger.LogInformation("Successfully uploaded links.csv to EBS-50 at {Host}:{Path}", settings.Host, remoteCsvPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("UploadLinksCsvAsync cancelled by caller.");
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("UploadLinksCsvAsync timed out after 20s.");
            throw new TimeoutException("Quá thời gian chờ tải lên links.csv (Timeout 20s).", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload links.csv to EBS-50 at {Host}", settings.Host);
            throw;
        }
    }

    private async Task SaveLocalCopyAsync(
        string macAddress,
        string imageFileName,
        byte[] imageBytes,
        string xmlContent,
        CancellationToken cancellationToken)
    {
        try
        {
            var localDir = Path.Combine(env.ContentRootPath, "Local_Ebs50_Input");
            Directory.CreateDirectory(localDir);

            // Clean up previous local images for this tag to avoid disk clutter
            var oldFiles = Directory.GetFiles(localDir, $"{macAddress}*.png");
            foreach (var oldFile in oldFiles)
            {
                if (!oldFile.EndsWith(imageFileName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(oldFile); } catch { /* Ignore file lock */ }
                }
            }

            var imgPath = Path.Combine(localDir, imageFileName);
            var xmlPath = Path.Combine(localDir, $"{macAddress}.xml");

            await File.WriteAllBytesAsync(imgPath, imageBytes, cancellationToken);
            await File.WriteAllTextAsync(xmlPath, xmlContent, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write local dispatch backup for {Mac}", macAddress);
        }
    }

    private static async Task CreateRemoteDirectoryRecursivelyAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            bool exists = await client.ExistsAsync(current, cancellationToken);
            if (!exists)
            {
                await client.CreateDirectoryAsync(current, cancellationToken);
            }
        }
    }

    private SftpClient CreateSftpClient(ConnectionSettings settings)
    {
        var authMethod = string.IsNullOrEmpty(settings.Password)
            ? new PasswordAuthenticationMethod(settings.Username, "")
            : new PasswordAuthenticationMethod(settings.Username, settings.Password);

        var connectionInfo = new Renci.SshNet.ConnectionInfo(
            settings.Host,
            settings.Port,
            settings.Username,
            authMethod)
        {
            Timeout = TimeSpan.FromSeconds(5) // Fast fail if host is unreachable
        };

        return new SftpClient(connectionInfo);
    }

}
