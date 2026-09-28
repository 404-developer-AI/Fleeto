namespace Fleeto.Core.Entities;

/// <summary>
/// One storage scan of one volume of an endpoint (0.6.0), as the agent reported it: the largest folders and files, not the whole
/// tree. Stored by the gateway once per <see cref="AgentScanId"/>; kept daily for 30 days and one a week for 13 months after that.
/// </summary>
public class StorageScan
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }

    /// <summary>The scan id the agent chose, unique per endpoint: a report sent twice is stored once.</summary>
    public Guid AgentScanId { get; set; }

    /// <summary>"C:" on Windows, the mount point on Linux.</summary>
    public string Volume { get; set; } = string.Empty;

    public string Filesystem { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }

    /// <summary>Stamped by the gateway on ingest; used for ordering and growth, the agent's time is informational.</summary>
    public DateTime ReceivedAt { get; set; }

    public DateTime? AgentStartedAt { get; set; }
    public int DurationMs { get; set; }
    public StorageScanMethod Method { get; set; }

    /// <summary>False when the scan stopped early or could not read everything; its sizes are then lower bounds.</summary>
    public bool Complete { get; set; }

    public string? Error { get; set; }
    public long FileCount { get; set; }
    public long FolderCount { get; set; }

    /// <summary>The request that started the scan, null for a scheduled one.</summary>
    public Guid? RequestId { get; set; }

    /// <summary>JSON array of <see cref="Domain.StorageFolderEntry"/>, largest first, the volume root first of all.</summary>
    public string FoldersJson { get; set; } = "[]";

    /// <summary>JSON array of <see cref="Domain.StorageFileEntry"/>, largest first.</summary>
    public string FilesJson { get; set; } = "[]";
}

/// <summary>
/// A request to scan the volumes of an endpoint now (0.6.0): a technician clicked "Scan now", or a Disk free check of the endpoint
/// turned warning or critical. Written by web or the workers, delivered once by the gateway to a live agent before it expires.
/// </summary>
public class StorageScanRequest
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public StorageScanReason Reason { get; set; }

    /// <summary>The technician who asked; null when Fleeto asked because of a Disk free check.</summary>
    public Guid? RequestedByUserId { get; set; }

    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    /// <summary>An agent that connects after this time does not receive the request.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? DeliveredAt { get; set; }
}
