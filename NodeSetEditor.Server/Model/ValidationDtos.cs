namespace NodeSetEditor.Server.Model
{
    /// <summary>
    /// A Word specification document in the validation document list, with its derived
    /// job status. Status is one of: "Ready" (no active job), "Queued", "Running",
    /// "Completed", "Failed" — see <see cref="NodeSetEditor.Model.ValidationJobState"/>.
    /// </summary>
    public class ValidationDocumentInfo
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = null!;
        public long SizeBytes { get; set; }
        public DateTime UploadedUtc { get; set; }
        public string? UploadedByUserId { get; set; }

        /// <summary>Derived job status: Ready | Queued | Running | Completed | Failed.</summary>
        public string Status { get; set; } = "Ready";

        /// <summary>The newest non-cleared job id (for cancel/reset/view), if any.</summary>
        public Guid? JobId { get; set; }

        /// <summary>URI of the model this document's job was run against; shown under the file name. Cleared on reset.</summary>
        public string? ModelUri { get; set; }

        // Result counts, present when Status is Completed/Failed.
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public int? InfoCount { get; set; }

        // Editable validator options for this document (see ValidationDocumentSettingsDto).
        public string? ProfileGroupName { get; set; }
        public bool Verbose { get; set; }
        public List<string> SuppressedCodes { get; set; } = new();
    }

    /// <summary>Editable validator options for a document (Profile Group / Verbose / suppressed codes).</summary>
    public class ValidationDocumentSettingsDto
    {
        /// <summary>Validator profile-group full name (e.g. "UACore 1.05"); null/empty = none.</summary>
        public string? ProfileGroupName { get; set; }
        public bool Verbose { get; set; }
        /// <summary>Error codes to suppress (validator --ignore). Empty = none.</summary>
        public List<string>? SuppressedCodes { get; set; }
    }

    /// <summary>A profile group from profiles.opcfoundation.org, for the options dropdown.</summary>
    public class ProfileGroupDto
    {
        public string FullName { get; set; } = null!;
        public int Sort { get; set; }
    }

    /// <summary>Result of a chunked document upload request (mirrors NodeSet UploadResult shape).</summary>
    public class DocumentUploadResult
    {
        public string UploadId { get; set; } = null!;
        public int ChunksReceived { get; set; }
        public int TotalChunks { get; set; }
        public bool IsComplete { get; set; }

        /// <summary>Set once the final chunk assembles the document.</summary>
        public Guid? DocumentId { get; set; }
    }

    /// <summary>Metadata for a single validation job.</summary>
    public class ValidationJobInfo
    {
        public Guid Id { get; set; }
        public Guid DocumentId { get; set; }
        public string State { get; set; } = null!;
        public DateTime CreatedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int InfoCount { get; set; }
    }

    /// <summary>One parsed validator finding rendered in the results dialog.</summary>
    public class ValidationEntryDto
    {
        public string? Section { get; set; }
        public string? Table { get; set; }
        public string? Severity { get; set; }
        public string? Code { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>The full result payload for the results dialog.</summary>
    public class ValidationResultDto
    {
        public Guid JobId { get; set; }
        public string State { get; set; } = null!;
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int InfoCount { get; set; }
        public string? Summary { get; set; }
        public int? ExitCode { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public List<ValidationEntryDto> Entries { get; set; } = new();
    }
}
