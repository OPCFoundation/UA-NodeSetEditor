using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Controllers
{
    /// <summary>
    /// Machine-to-machine API for the external validation worker (the interactive Azure VM that
    /// runs Word/COM). Authenticated with the shared <c>X-Api-Key</c> secret (ApiKey scheme +
    /// Worker policy), NOT a user token. Implements the Pop/Push queue contract:
    /// <list type="bullet">
    /// <item><c>pop</c> atomically claims the oldest queued job → Running, returning a lock token.</item>
    /// <item><c>document</c> / <c>nodesets</c> stream the artifacts; they 410 if the job was deleted
    /// or 409 if it was cancelled / re-popped (stale lock), so the worker aborts cleanly.</item>
    /// <item><c>push</c> uploads the log + structured results and completes the job — but only if the
    /// caller still holds the lock (else 410/409, results discarded).</item>
    /// </list>
    /// </summary>
    [ApiController]
    // Internal machine-to-machine contract for the validation worker VM; hidden from the published
    // spec. Access is enforced by the ApiKey scheme and Worker policy below, not by this attribute.
    [ApiExplorerSettings(IgnoreApi = true)]
    [Route("api/opcua/v1/validation/worker")]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName, Policy = ApiKeyAuthenticationHandler.Worker)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class ValidationWorkerController : ControllerBase
    {
        private readonly IValidationService _validation;
        private readonly INodeSetStorageService _storage;
        private readonly ILogger<ValidationWorkerController> _logger;

        public ValidationWorkerController(
            IValidationService validation,
            INodeSetStorageService storage,
            ILogger<ValidationWorkerController> logger)
        {
            _validation = validation;
            _storage = storage;
            _logger = logger;
        }

        public sealed record PopRequest(string WorkerId);

        [HttpPost("jobs/pop")]
        public async Task<ActionResult<PoppedJob>> Pop([FromBody] PopRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.WorkerId))
                return BadRequest("workerId is required.");

            var job = await _validation.PopAsync(request.WorkerId);
            if (job == null) return NoContent(); // queue empty
            return Ok(job);
        }

        [HttpGet("jobs/{jobId}/document")]
        public async Task<IActionResult> GetDocument(Guid jobId, [FromQuery] string lockToken)
        {
            var (access, _) = await _validation.GetJobForWorkerAsync(jobId, lockToken);
            if (access != WorkerJobAccess.Found) return AccessError(access);

            var doc = await _validation.GetDocumentContentForJobAsync(jobId);
            if (doc == null) return StatusCode(StatusCodes.Status410Gone, "The document no longer exists.");

            return File(doc.Value.Content,
                doc.Value.ContentType ?? "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                doc.Value.FileName);
        }

        [HttpGet("jobs/{jobId}/nodesets")]
        public async Task<IActionResult> GetNodeSets(Guid jobId, [FromQuery] string lockToken)
        {
            var (access, job) = await _validation.GetJobForWorkerAsync(jobId, lockToken);
            if (access != WorkerJobAccess.Found) return AccessError(access);

            var workspaceId = job!.WorkspaceId;
            var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
            var primary = models.FirstOrDefault(m => m?.Id == job.ModelId);
            if (primary == null || string.IsNullOrEmpty(primary.ModelUri))
                return StatusCode(StatusCodes.Status410Gone, "The model no longer exists.");

            var modelIdByUri = models
                .Where(m => m != null && m!.Id.HasValue && !string.IsNullOrEmpty(m.ModelUri))
                .GroupBy(m => m!.ModelUri!)
                .ToDictionary(g => g.Key, g => g.First()!.Id!.Value);

            // Bundle EVERY NodeSet in the workspace. Dependency resolution already happened when each
            // model was added, so there's no need to re-walk the closure here — the validator gets the
            // full set (primary = _primary.xml) and resolves references locally from it.
            var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (uri, depId) in modelIdByUri)
                {
                    var (stream, info) = await _storage.GetModelFileStreamAsync(workspaceId, depId);

                    // The primary (closure root) is named "_primary.xml" so the worker can pass it
                    // to --nodeset unambiguously; dependencies get filesystem-safe URI-based names.
                    string entryName;
                    if (string.Equals(uri, primary.ModelUri, StringComparison.Ordinal))
                    {
                        entryName = "_primary.xml";
                        usedNames.Add(entryName);
                    }
                    else
                    {
                        var baseName = SanitizeFileName(info.ModelUri ?? depId.ToString());
                        entryName = $"{baseName}.xml";
                        var n = 2;
                        while (!usedNames.Add(entryName)) entryName = $"{baseName}_{n++}.xml";
                    }

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await stream.CopyToAsync(entryStream);
                    await stream.DisposeAsync();
                }
            }
            zipStream.Seek(0, SeekOrigin.Begin);
            return File(zipStream, "application/zip", $"{SanitizeFileName(primary.ModelUri!)}-nodesets.zip");
        }

        [HttpPost("jobs/{jobId}/push")]
        [RequestSizeLimit(64_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 64_000_000)]
        public async Task<IActionResult> Push(
            Guid jobId,
            [FromForm] string lockToken,
            [FromForm] int exitCode,
            [FromForm] int errorCount,
            [FromForm] int warningCount,
            [FromForm] int infoCount,
            [FromForm] string? summary = null,
            [FromForm] string? workerMessage = null,
            [FromForm] string? entries = null,
            [FromForm] IFormFile? log = null)
        {
            byte[]? logBytes = null;
            if (log != null)
            {
                using var ms = new MemoryStream();
                await log.CopyToAsync(ms);
                logBytes = ms.ToArray();
            }

            JsonArray? entriesArray = null;
            if (!string.IsNullOrWhiteSpace(entries))
            {
                try { entriesArray = JsonNode.Parse(entries) as JsonArray; }
                catch (System.Text.Json.JsonException)
                {
                    return BadRequest("entries is not valid JSON.");
                }
            }

            var input = new ValidationPushInput(
                exitCode, errorCount, warningCount, infoCount,
                summary, workerMessage, logBytes, entriesArray);

            var access = await _validation.PushAsync(jobId, lockToken, input);
            return access switch
            {
                WorkerJobAccess.Found => Ok(),
                WorkerJobAccess.Gone => StatusCode(StatusCodes.Status410Gone, "The job no longer exists."),
                _ => StatusCode(StatusCodes.Status409Conflict, "The job was cancelled or re-claimed; results discarded."),
            };
        }

        // ---------------------------------------------------------------- Helpers

        private IActionResult AccessError(WorkerJobAccess access) => access switch
        {
            WorkerJobAccess.Gone => StatusCode(StatusCodes.Status410Gone, "The job no longer exists."),
            _ => StatusCode(StatusCodes.Status409Conflict, "The job was cancelled or re-claimed."),
        };

        private static string SanitizeFileName(string raw)
        {
            var chars = raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
            var name = new string(chars).Trim('_', '.');
            return string.IsNullOrEmpty(name) ? "nodeset" : name;
        }
    }
}
