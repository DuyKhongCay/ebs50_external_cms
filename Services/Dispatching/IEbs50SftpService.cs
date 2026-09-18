namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Result of an EBS-50 connection diagnostic test.
/// </summary>
public sealed record SftpTestResult(
    bool Success,
    string Message,
    string Host,
    int Port,
    string RemotePath);

/// <summary>
/// Service contract for SFTP file transport to the Opticon EBS-50 router.
/// </summary>
public interface IEbs50SftpService
{
    /// <summary>
    /// Tests connection and write accessibility to the EBS-50 input folder.
    /// </summary>
    Task<SftpTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads paired dynamic image and XML metadata files atomically into the EBS-50 Input folder.
    /// Cleans up any previous image files for the specified MAC, uploads image first, then uploads XML descriptor.
    /// </summary>
    Task UploadTagPayloadAsync(
        string macAddress,
        string imageFileName,
        byte[] imageBytes,
        string xmlContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads the links.csv binding file into the EBS-50 Input folder to establish MAC-to-Item bindings.
    /// </summary>
    Task UploadLinksCsvAsync(string csvContent, CancellationToken cancellationToken = default);
}

