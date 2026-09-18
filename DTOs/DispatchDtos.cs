namespace ebs50_backend.DTOs;

/// <summary>A persisted dispatch request; acceptance does not confirm physical display.</summary>
/// <param name="Message">Human-readable acceptance message.</param>
/// <param name="JobId">Job identifier for log/database correlation; no job lookup endpoint exists.</param>
/// <param name="Mac">Target tag MAC.</param>
/// <param name="Model">Bound product model.</param>
/// <param name="State">Requested machine state.</param>
/// <param name="QueuePending">Snapshot of jobs in Pending state.</param>
public sealed record DispatchAcceptedResponse(string Message, Guid JobId, string Mac, string Model, int State, int QueuePending);

/// <summary>Acceptance of a batch of dispatch jobs, without delivery confirmation.</summary>
public sealed record DispatchBatchResponse(string Message, int TotalTags, int QueuePending);

/// <summary>Aggregate Pending plus Dispatching jobs; excludes Uploaded jobs awaiting confirmation.</summary>
public sealed record DispatchQueueStatusResponse(int PendingJobs, DateTime Timestamp);

/// <summary>Stored station settings with controller defaults; Password is returned unmasked.</summary>
public sealed record StationConfigResponse(string Host, int Port, string Username, string Password, string RemotePath);

/// <summary>Result of saving connection settings; does not test connectivity.</summary>
public sealed record ConfigSavedResponse(bool Success, string Message);
