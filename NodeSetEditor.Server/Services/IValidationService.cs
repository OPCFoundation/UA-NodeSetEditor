using NodeSetEditor.Model;
using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// How a worker's attempt to access a job resolved. <see cref="Found"/> = the caller holds the
    /// current lock and the job is Running; <see cref="Gone"/> = the job row no longer exists
    /// (deleted / document or model removed) → the worker should abort (HTTP 410);
    /// <see cref="Conflict"/> = the job exists but the caller's lock token is stale (cancelled or
    /// re-popped) → the worker should abort and its results are discarded (HTTP 409).
    /// </summary>
    public enum WorkerJobAccess
    {
        Found,
        Gone,
        Conflict,
    }

    /// <summary>A job handed to a worker by <c>Pop</c>.</summary>
    public sealed record PoppedJob(
        Guid JobId, Guid WorkspaceId, Guid ModelId, Guid DocumentId,
        string FileName, string LockToken,
        string? ProfileGroupName, bool Verbose, string? IgnoreCodes,
        string? ModelUri);

    /// <summary>The results a worker uploads on <c>Push</c>.</summary>
    public sealed record ValidationPushInput(
        int ExitCode, int ErrorCount, int WarningCount, int InfoCount,
        string? Summary, string? WorkerMessage,
        byte[]? LogContent, System.Text.Json.Nodes.JsonArray? Entries);

    /// <summary>
    /// Persistence and lifecycle for specification-validation documents and jobs. User-facing
    /// methods take the already-resolved workspace id and re-validate the (workspace, model,
    /// document, job) linkage — model rows are shared across workspaces, so ids from the caller
    /// are never trusted (IDOR discipline, same as UaRestApiController).
    /// </summary>
    public interface IValidationService
    {
        // ---- Documents (user, per-workspace) ----
        Task<IReadOnlyList<ValidationDocumentInfo>> ListDocumentsAsync(Guid workspaceId);

        Task<DocumentUploadResult> HandleDocumentUploadChunkAsync(
            Guid workspaceId, string userId,
            Stream content, string fileName, string? contentType,
            int chunkIndex, int totalChunks, string? uploadId);

        /// <summary>Returns false if the document does not exist in this workspace.</summary>
        Task<bool> DeleteDocumentAsync(Guid workspaceId, Guid documentId);

        /// <summary>Update a document's validator options. False if the document is not in this workspace.</summary>
        Task<bool> UpdateDocumentSettingsAsync(
            Guid workspaceId, Guid documentId,
            string? profileGroupName, bool verbose, IEnumerable<string>? suppressedCodes);

        // ---- Jobs (user) ----
        /// <summary>Start a job for a document against the selected model (saved on the job).</summary>
        Task<ValidationJobInfo> StartJobAsync(Guid workspaceId, Guid modelId, Guid documentId, string userId);

        /// <summary>Cancel a Queued/Running job → Cancelled tombstone (clears lock). False if not found.</summary>
        Task<bool> CancelJobAsync(Guid workspaceId, Guid jobId);

        /// <summary>Reset a Completed job → Cancelled (Ready). False if not found / not completed.</summary>
        Task<bool> ResetJobAsync(Guid workspaceId, Guid jobId);

        /// <summary>Hard-delete a job row (worker will see 410 gone). False if not found.</summary>
        Task<bool> DeleteJobAsync(Guid workspaceId, Guid jobId);

        Task<ValidationResultDto?> GetJobResultAsync(Guid workspaceId, Guid jobId);

        // ---- Worker (M2M) ----
        /// <summary>Atomically claim the oldest Queued job → Running, stamping a fresh lock token.</summary>
        Task<PoppedJob?> PopAsync(string workerId);

        /// <summary>Resolve a job for a worker holding <paramref name="lockToken"/>.</summary>
        Task<(WorkerJobAccess Access, ValidationJob? Job)> GetJobForWorkerAsync(Guid jobId, string lockToken);

        /// <summary>The uploaded document bytes for a job (worker artifact download). Null if gone.</summary>
        Task<(string FileName, string? ContentType, byte[] Content)?> GetDocumentContentForJobAsync(Guid jobId);

        /// <summary>Complete a job (Push). Writes results + sets Completed/Failed only if lock matches.</summary>
        Task<WorkerJobAccess> PushAsync(Guid jobId, string lockToken, ValidationPushInput input);
    }
}
