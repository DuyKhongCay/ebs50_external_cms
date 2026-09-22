namespace ebs50_backend.DTOs;

/// <summary>A local interface and its IPv4 web addresses.</summary>
public sealed record NetworkInterfaceDto(string Id, string Name, string Status,
    string Role, string[] Addresses, string[] Gateways, string[] WebUrls);

/// <summary>A single diagnostic observation; local success does not verify remote access.</summary>
public sealed record NetworkProbeResult(string Name, string Target, string Status,
    string Code, string Message, long DurationMs, string? SourceAddress = null);

/// <summary>Timestamped network diagnostic results.</summary>
public sealed record NetworkDiagnosticResult(DateTimeOffset CheckedAt,
    IReadOnlyList<NetworkProbeResult> Checks);
