using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;
using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// EF Core-backed implementation of <see cref="IValidationService"/>. Documents and job
    /// artifacts are stored inline as bytea (no blob store), matching the file→Postgres house
    /// style. The Pop/Push worker queue is DB-backed; Pop uses <c>FOR UPDATE SKIP LOCKED</c> so
    /// concurrent workers never claim the same job.
    /// </summary>
    public class ValidationService : IValidationService
    {
        /// <summary>Maximum assembled document size (64 MB, per feature requirement).</summary>
        private const long MaxDocumentBytes = 64L * 1024 * 1024;

        private readonly NodeSetEditorDbContext _db;
        private readonly ILogger<ValidationService> _logger;

        public ValidationService(NodeSetEditorDbContext db, ILogger<ValidationService> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ---------------------------------------------------------------- Documents

        public async Task<IReadOnlyList<ValidationDocumentInfo>> ListDocumentsAsync(Guid workspaceId)
        {
            var docs = await _db.ValidationDocuments
                .Where(d => d.WorkspaceId == workspaceId)
                .OrderByDescending(d => d.UploadedUtc)
                .Select(d => new { d.Id, d.FileName, d.SizeBytes, d.UploadedUtc, d.UploadedByUserId,
                    d.ProfileGroupName, d.Verbose, d.SuppressedCodes })
                .ToListAsync();

            var docIds = docs.Select(d => d.Id).ToList();

            // Newest job per document (any state; Cancelled maps to "Ready").
            var jobs = await _db.ValidationJobs
                .Where(j => docIds.Contains(j.DocumentId))
                .ToListAsync();
            var newestByDoc = jobs
                .GroupBy(j => j.DocumentId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedUtc).First());

            return docs.Select(d =>
            {
                var info = new ValidationDocumentInfo
                {
                    Id = d.Id,
                    FileName = d.FileName,
                    SizeBytes = d.SizeBytes,
                    UploadedUtc = d.UploadedUtc,
                    UploadedByUserId = d.UploadedByUserId,
                    Status = "Ready",
                    ProfileGroupName = d.ProfileGroupName,
                    Verbose = d.Verbose,
                    SuppressedCodes = SplitCodes(d.SuppressedCodes),
                };
                if (newestByDoc.TryGetValue(d.Id, out var job) && job.State != ValidationJobState.Cancelled)
                {
                    info.Status = job.State.ToString();
                    info.JobId = job.Id;
                    info.ModelUri = job.ModelUri;   // applied model shown under the file name; cleared on reset
                    if (job.State is ValidationJobState.Completed or ValidationJobState.Failed)
                    {
                        info.ErrorCount = job.ErrorCount;
                        info.WarningCount = job.WarningCount;
                        info.InfoCount = job.InfoCount;
                    }
                }
                return info;
            }).ToList();
        }

        public async Task<DocumentUploadResult> HandleDocumentUploadChunkAsync(
            Guid workspaceId, string userId,
            Stream content, string fileName, string? contentType,
            int chunkIndex, int totalChunks, string? uploadId)
        {
            if (totalChunks < 1) throw new InvalidOperationException("totalChunks must be at least 1.");
            if (chunkIndex < 0 || chunkIndex >= totalChunks)
                throw new InvalidOperationException("chunkIndex is out of range.");

            uploadId ??= Guid.NewGuid().ToString("N");

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer);
            var data = buffer.ToArray();

            // Upsert this chunk (idempotent on retry of the same chunk index).
            var existing = await _db.ValidationUploadChunks.FindAsync(uploadId, chunkIndex);
            if (existing == null)
            {
                _db.ValidationUploadChunks.Add(new ValidationUploadChunk
                {
                    UploadId = uploadId,
                    ChunkIndex = chunkIndex,
                    WorkspaceId = workspaceId,
                    FileName = fileName,
                    TotalChunks = totalChunks,
                    UploadedByUserId = userId,
                    CreatedUtc = DateTime.UtcNow,
                    Data = data,
                });
            }
            else
            {
                existing.Data = data;
                existing.TotalChunks = totalChunks;
                existing.FileName = fileName;
            }
            await _db.SaveChangesAsync();

            var received = await _db.ValidationUploadChunks.CountAsync(c => c.UploadId == uploadId);
            if (received < totalChunks)
            {
                return new DocumentUploadResult
                {
                    UploadId = uploadId,
                    ChunksReceived = received,
                    TotalChunks = totalChunks,
                    IsComplete = false,
                };
            }

            // Final chunk arrived — assemble, verifying total size before allocating the blob.
            var chunks = await _db.ValidationUploadChunks
                .Where(c => c.UploadId == uploadId)
                .OrderBy(c => c.ChunkIndex)
                .ToListAsync();

            var totalLength = chunks.Sum(c => (long)c.Data.Length);
            if (totalLength > MaxDocumentBytes)
            {
                _db.ValidationUploadChunks.RemoveRange(chunks);
                await _db.SaveChangesAsync();
                throw new InvalidOperationException(
                    $"The document exceeds the {MaxDocumentBytes / (1024 * 1024)} MB limit.");
            }

            var assembled = new byte[totalLength];
            var offset = 0;
            foreach (var c in chunks)
            {
                Buffer.BlockCopy(c.Data, 0, assembled, offset, c.Data.Length);
                offset += c.Data.Length;
            }

            // If a document with the same name (case-insensitive) already exists in this workspace,
            // silently reset it — drop its jobs/results so it returns to Ready and its applied model
            // is cleared — and replace its content in place, keeping the same row (Id). Otherwise add
            // a new row. A Running job's row is removed too; its worker sees 410 and cleans up.
            // Lowered here, not in the expression: this half is evaluated client-side and
            // ToLower() would apply the current culture (tr-TR maps 'I' to dotless 'i'), so the
            // comparison could stop matching what the database's LOWER() produced.
            var loweredFileName = fileName.ToLowerInvariant();
            var existingDoc = await _db.ValidationDocuments
                .FirstOrDefaultAsync(d => d.WorkspaceId == workspaceId
                    && d.FileName.ToLower() == loweredFileName);

            Guid resultDocId;
            if (existingDoc != null)
            {
                var oldJobs = await _db.ValidationJobs
                    .Where(j => j.DocumentId == existingDoc.Id)
                    .ToListAsync();
                if (oldJobs.Count > 0) _db.ValidationJobs.RemoveRange(oldJobs); // results cascade via JobId FK

                existingDoc.FileName = fileName;
                existingDoc.SizeBytes = totalLength;
                existingDoc.ContentType = contentType;
                existingDoc.Content = assembled;
                existingDoc.UploadedByUserId = userId;
                existingDoc.UploadedUtc = DateTime.UtcNow;
                resultDocId = existingDoc.Id;
            }
            else
            {
                var doc = new ValidationDocument
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    FileName = fileName,
                    SizeBytes = totalLength,
                    ContentType = contentType,
                    Content = assembled,
                    UploadedByUserId = userId,
                    UploadedUtc = DateTime.UtcNow,
                };
                _db.ValidationDocuments.Add(doc);
                resultDocId = doc.Id;
            }

            _db.ValidationUploadChunks.RemoveRange(chunks);
            await _db.SaveChangesAsync();

            return new DocumentUploadResult
            {
                UploadId = uploadId,
                ChunksReceived = received,
                TotalChunks = totalChunks,
                IsComplete = true,
                DocumentId = resultDocId,
            };
        }

        public async Task<bool> DeleteDocumentAsync(Guid workspaceId, Guid documentId)
        {
            var doc = await _db.ValidationDocuments
                .FirstOrDefaultAsync(d => d.Id == documentId && d.WorkspaceId == workspaceId);
            if (doc == null) return false;

            // Jobs (and their results) cascade via the DocumentId FK.
            _db.ValidationDocuments.Remove(doc);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> UpdateDocumentSettingsAsync(
            Guid workspaceId, Guid documentId,
            string? profileGroupName, bool verbose, IEnumerable<string>? suppressedCodes)
        {
            var doc = await _db.ValidationDocuments
                .FirstOrDefaultAsync(d => d.Id == documentId && d.WorkspaceId == workspaceId);
            if (doc == null) return false;

            doc.ProfileGroupName = string.IsNullOrWhiteSpace(profileGroupName) ? null : profileGroupName.Trim();
            doc.Verbose = verbose;
            doc.SuppressedCodes = JoinCodes(suppressedCodes);
            await _db.SaveChangesAsync();
            return true;
        }

        /// <summary>Semicolon-joined codes → distinct, trimmed list (validator --ignore round-trip).</summary>
        private static List<string> SplitCodes(string? codes) =>
            string.IsNullOrWhiteSpace(codes)
                ? new List<string>()
                : codes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Distinct, trimmed codes → semicolon-joined string (null when empty).</summary>
        private static string? JoinCodes(IEnumerable<string>? codes)
        {
            if (codes == null) return null;
            var clean = codes.Select(c => c?.Trim()).Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.Ordinal).ToList();
            return clean.Count == 0 ? null : string.Join(';', clean);
        }

        // ---------------------------------------------------------------- Jobs (user)

        public async Task<ValidationJobInfo> StartJobAsync(Guid workspaceId, Guid modelId, Guid documentId, string userId)
        {
            var doc = await _db.ValidationDocuments
                .FirstOrDefaultAsync(d => d.Id == documentId && d.WorkspaceId == workspaceId)
                ?? throw new KeyNotFoundException("Document not found in this workspace.");

            // Capture the selected model's URI now so the document row can show it (and it
            // survives the model being deleted later).
            var modelUri = await _db.Models.Where(m => m.Id == modelId).Select(m => m.Uri).FirstOrDefaultAsync();

            var jobs = await _db.ValidationJobs.Where(j => j.DocumentId == documentId).ToListAsync();
            if (jobs.Any(j => j.State is ValidationJobState.Queued or ValidationJobState.Running))
                throw new InvalidOperationException("A validation job is already in progress for this document.");

            // Only terminal (Completed/Failed/Cancelled) jobs remain — clear them so the
            // document has exactly one job at a time (results cascade with the row).
            if (jobs.Count > 0) _db.ValidationJobs.RemoveRange(jobs);

            var job = new ValidationJob
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                WorkspaceId = workspaceId,
                ModelId = modelId,
                ModelUri = modelUri,
                // Snapshot the document's validator options so an edit mid-run can't change this job.
                ProfileGroupName = doc.ProfileGroupName,
                Verbose = doc.Verbose,
                IgnoreCodes = doc.SuppressedCodes,
                State = ValidationJobState.Queued,
                RequestedByUserId = userId,
                CreatedUtc = DateTime.UtcNow,
            };
            _db.ValidationJobs.Add(job);
            await _db.SaveChangesAsync();
            return ToJobInfo(job);
        }

        public async Task<bool> CancelJobAsync(Guid workspaceId, Guid jobId)
        {
            var job = await _db.ValidationJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.WorkspaceId == workspaceId);
            if (job == null) return false;
            if (job.State is not (ValidationJobState.Queued or ValidationJobState.Running)) return false;

            // Tombstone: clear the lock so any in-flight worker's Push no-ops (Conflict/409).
            job.State = ValidationJobState.Cancelled;
            job.LockToken = null;
            job.WorkerId = null;
            job.CompletedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> ResetJobAsync(Guid workspaceId, Guid jobId)
        {
            var job = await _db.ValidationJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.WorkspaceId == workspaceId);
            if (job == null) return false;
            if (job.State is not (ValidationJobState.Completed or ValidationJobState.Failed)) return false;

            job.State = ValidationJobState.Cancelled;
            job.LockToken = null;
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteJobAsync(Guid workspaceId, Guid jobId)
        {
            var job = await _db.ValidationJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.WorkspaceId == workspaceId);
            if (job == null) return false;
            _db.ValidationJobs.Remove(job);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<ValidationResultDto?> GetJobResultAsync(Guid workspaceId, Guid jobId)
        {
            var job = await _db.ValidationJobs
                .Include(j => j.Result)
                .FirstOrDefaultAsync(j => j.Id == jobId && j.WorkspaceId == workspaceId);
            if (job == null) return null;

            var dto = new ValidationResultDto
            {
                JobId = job.Id,
                State = job.State.ToString(),
                ErrorCount = job.ErrorCount,
                WarningCount = job.WarningCount,
                InfoCount = job.InfoCount,
                Summary = job.Summary,
                ExitCode = job.ExitCode,
                CompletedUtc = job.CompletedUtc,
            };

            if (job.Result?.Entries is JsonArray entries)
            {
                foreach (var node in entries)
                {
                    if (node is not JsonObject o) continue;
                    dto.Entries.Add(new ValidationEntryDto
                    {
                        Section = (string?)o["section"],
                        Table = (string?)o["table"],
                        Severity = (string?)o["severity"],
                        Code = (string?)o["code"],
                        Description = (string?)o["description"],
                    });
                }
            }
            return dto;
        }

        // ---------------------------------------------------------------- Worker (M2M)

        public async Task<PoppedJob?> PopAsync(string workerId)
        {
            // The retrying execution strategy (EnableRetryOnFailure) forbids a bare
            // user-initiated transaction, so wrap the whole atomic claim in the strategy:
            // on a transient failure it re-runs begin -> lock -> update -> commit as a unit.
            var strategy = _db.Database.CreateExecutionStrategy();
            var job = await strategy.ExecuteAsync(async () =>
            {
                // Atomic claim: lock the oldest Queued row, skipping rows other workers hold.
                await using var tx = await _db.Database.BeginTransactionAsync();

                var claimed = await _db.ValidationJobs
                    .FromSqlRaw(
                        @"SELECT * FROM ""ValidationJobs"" WHERE ""State"" = {0} " +
                        @"ORDER BY ""CreatedUtc"" LIMIT 1 FOR UPDATE SKIP LOCKED",
                        (int)ValidationJobState.Queued)
                    .FirstOrDefaultAsync();

                if (claimed == null)
                {
                    await tx.RollbackAsync();
                    return (ValidationJob?)null;
                }

                var token = Guid.NewGuid().ToString("N");
                var now = DateTime.UtcNow;
                claimed.State = ValidationJobState.Running;
                claimed.WorkerId = workerId;
                claimed.PoppedUtc = now;
                claimed.StartedUtc = now;
                claimed.LockToken = token;
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                return claimed;
            });

            if (job == null)
            {
                return null;
            }

            var doc = await _db.ValidationDocuments
                .Where(d => d.Id == job.DocumentId)
                .Select(d => new { d.FileName })
                .FirstOrDefaultAsync();

            return new PoppedJob(job.Id, job.WorkspaceId, job.ModelId, job.DocumentId,
                doc?.FileName ?? "document.docx", job.LockToken!,
                job.ProfileGroupName, job.Verbose, job.IgnoreCodes, job.ModelUri);
        }

        public async Task<(WorkerJobAccess Access, ValidationJob? Job)> GetJobForWorkerAsync(Guid jobId, string lockToken)
        {
            var job = await _db.ValidationJobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null) return (WorkerJobAccess.Gone, null);
            if (job.State != ValidationJobState.Running || job.LockToken != lockToken)
                return (WorkerJobAccess.Conflict, job);
            return (WorkerJobAccess.Found, job);
        }

        public async Task<(string FileName, string? ContentType, byte[] Content)?> GetDocumentContentForJobAsync(Guid jobId)
        {
            var row = await _db.ValidationJobs
                .Where(j => j.Id == jobId)
                .Join(_db.ValidationDocuments, j => j.DocumentId, d => d.Id,
                    (j, d) => new { d.FileName, d.ContentType, d.Content })
                .FirstOrDefaultAsync();
            return row == null ? null : (row.FileName, row.ContentType, row.Content);
        }

        public async Task<WorkerJobAccess> PushAsync(Guid jobId, string lockToken, ValidationPushInput input)
        {
            var job = await _db.ValidationJobs
                .Include(j => j.Result)
                .FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null) return WorkerJobAccess.Gone;
            if (job.State != ValidationJobState.Running || job.LockToken != lockToken)
                return WorkerJobAccess.Conflict;

            var now = DateTime.UtcNow;
            if (job.Result == null)
            {
                job.Result = new ValidationJobResult { JobId = job.Id };
                _db.ValidationJobResults.Add(job.Result);
            }
            job.Result.LogContent = input.LogContent;
            job.Result.Entries = input.Entries;
            job.Result.UploadedUtc = now;

            // Exit code 0 = the validator ran to completion (discrepancies, if any, are in the
            // report); non-zero = the tool itself failed (missing dependency, crash) → Failed.
            job.State = input.ExitCode == 0 ? ValidationJobState.Completed : ValidationJobState.Failed;
            job.ErrorCount = input.ErrorCount;
            job.WarningCount = input.WarningCount;
            job.InfoCount = input.InfoCount;
            job.Summary = input.Summary;
            job.ExitCode = input.ExitCode;
            job.WorkerMessage = input.WorkerMessage;
            job.CompletedUtc = now;
            job.LockToken = null;    // completion releases the lock (double-push → Conflict)
            await _db.SaveChangesAsync();
            return WorkerJobAccess.Found;
        }

        private static ValidationJobInfo ToJobInfo(ValidationJob job) => new()
        {
            Id = job.Id,
            DocumentId = job.DocumentId,
            State = job.State.ToString(),
            CreatedUtc = job.CreatedUtc,
            StartedUtc = job.StartedUtc,
            CompletedUtc = job.CompletedUtc,
            ErrorCount = job.ErrorCount,
            WarningCount = job.WarningCount,
            InfoCount = job.InfoCount,
        };
    }
}
