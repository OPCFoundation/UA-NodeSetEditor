using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NodeSetEditor.Server.Model;
using NodeSetEditor.Server.Services;
using Opc.Ua.RestfulApi;

namespace NodeSetEditor.Server.Controllers
{
    /// <summary>
    /// User-facing API for specification validation: managing the Word documents uploaded per
    /// (workspace, model) and driving validation jobs. The workspace is passed via the
    /// <c>OpcUa-Server</c> header (same convention as the rest of the app). Every action resolves
    /// the workspace with owner/ACL access checks and re-validates the (workspace, model,
    /// document, job) linkage — model rows are shared, so ids from the caller are never trusted.
    /// Machine-to-machine worker endpoints live in <see cref="ValidationWorkerController"/>.
    /// </summary>
    [ApiController]
    [Route("api/opcua/v1/validation")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class ValidationController : ControllerBase
    {
        // A single chunk is ~4 MB; cap each request small (the 64 MB total is enforced on assembly).
        private const int ChunkRequestLimit = 16_000_000;

        // Reference data proxied from profiles.opcfoundation.org for the options dropdown.
        private const string ProfileGroupsUrl = "https://profiles.opcfoundation.org/api/profilegroup/";
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
        // Small in-memory cache — the profile-group list changes rarely.
        private static (DateTime FetchedUtc, List<ProfileGroupDto> Groups)? _profileGroupCache;
        private static readonly TimeSpan ProfileGroupTtl = TimeSpan.FromHours(6);

        private readonly IValidationService _validation;
        private readonly INodeSetStorageService _storage;
        private readonly ILogger<ValidationController> _logger;

        public ValidationController(
            IValidationService validation,
            INodeSetStorageService storage,
            ILogger<ValidationController> logger)
        {
            _validation = validation;
            _storage = storage;
            _logger = logger;
        }

        // ---------------------------------------------------------------- Documents

        [HttpGet("documents")]
        public async Task<ActionResult<IReadOnlyList<ValidationDocumentInfo>>> ListDocuments(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer);
            if (error != null) return error;

            var docs = await _validation.ListDocumentsAsync(workspace!.Id!.Value);
            return Ok(docs);
        }

        [HttpPost("documents/upload")]
        [RequestSizeLimit(ChunkRequestLimit)]
        [RequestFormLimits(MultipartBodyLengthLimit = ChunkRequestLimit)]
        public async Task<ActionResult<DocumentUploadResult>> UploadDocument(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromForm] IFormFile file,
            [FromForm] string fileName,
            [FromForm] int chunkIndex = 0,
            [FromForm] int totalChunks = 1,
            [FromForm] string? uploadId = null)
        {
            var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
            if (error != null) return error;

            if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                    "Only Word (.docx) documents are supported."));

            try
            {
                using var stream = file.OpenReadStream();
                var result = await _validation.HandleDocumentUploadChunkAsync(
                    workspace!.Id!.Value, user!.UserId!,
                    stream, fileName, file.ContentType, chunkIndex, totalChunks, uploadId);
                return Ok(result);
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
        }

        [HttpDelete("documents/{documentId}")]
        public async Task<IActionResult> DeleteDocument(
            Guid documentId, [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
            if (error != null) return error;

            var deleted = await _validation.DeleteDocumentAsync(workspace!.Id!.Value, documentId);
            if (!deleted)
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                    "Document not found in this workspace."));
            return NoContent();
        }

        /// <summary>Update a document's validator options (Profile Group / Verbose / suppressed codes).</summary>
        [HttpPut("documents/{documentId}/settings")]
        public async Task<IActionResult> UpdateDocumentSettings(
            Guid documentId,
            [FromBody] ValidationDocumentSettingsDto settings,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
            if (error != null) return error;

            var ok = await _validation.UpdateDocumentSettingsAsync(
                workspace!.Id!.Value, documentId,
                settings?.ProfileGroupName, settings?.Verbose ?? false, settings?.SuppressedCodes);
            if (!ok)
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                    "Document not found in this workspace."));
            return NoContent();
        }

        /// <summary>
        /// Profile groups (full names) from profiles.opcfoundation.org, sorted by their <c>sort</c>
        /// field, for the document options dropdown. Proxied server-side (avoids browser CORS) and
        /// cached briefly. Requires authentication (global policy) but no workspace.
        /// </summary>
        [HttpGet("profile-groups")]
        public async Task<ActionResult<IReadOnlyList<ProfileGroupDto>>> GetProfileGroups()
        {
            if (_profileGroupCache is { } c && DateTime.UtcNow - c.FetchedUtc < ProfileGroupTtl)
                return Ok(c.Groups);

            try
            {
                using var stream = await _http.GetStreamAsync(ProfileGroupsUrl);
                using var doc = await JsonDocument.ParseAsync(stream);
                var groups = new List<ProfileGroupDto>();
                if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in result.EnumerateArray())
                    {
                        var fullName = g.TryGetProperty("fullName", out var fn) ? fn.GetString() : null;
                        if (string.IsNullOrWhiteSpace(fullName)) continue;
                        var sort = g.TryGetProperty("sort", out var s) && s.TryGetInt32(out var si) ? si : 0;
                        groups.Add(new ProfileGroupDto { FullName = fullName!, Sort = sort });
                    }
                }
                groups = groups.OrderBy(x => x.Sort).ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase).ToList();
                _profileGroupCache = (DateTime.UtcNow, groups);
                return Ok(groups);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Serve stale cache if we have it; otherwise surface a 502.
                if (_profileGroupCache is { } stale) return Ok(stale.Groups);
                _logger.LogWarning(e, "Failed to fetch profile groups from {Url}", ProfileGroupsUrl);
                return StatusCode(502, MakeError(Opc.Ua.StatusCodes.BadNotConnected, nameof(Opc.Ua.StatusCodes.BadNotConnected),
                    "Could not reach the profile group service."));
            }
        }

        // ---------------------------------------------------------------- Jobs

        [HttpPost("documents/{documentId}/jobs")]
        public async Task<ActionResult<ValidationJobInfo>> StartJob(
            Guid documentId,
            [FromQuery] Guid modelId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
            if (error != null) return error;

            // The selected model (from the model's Validate icon) must be a private model in this workspace.
            var modelError = ResolveModel(workspace!, modelId);
            if (modelError != null) return modelError;

            try
            {
                var job = await _validation.StartJobAsync(workspace!.Id!.Value, modelId, documentId, user!.UserId!);
                return Ok(job);
            }
            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (InvalidOperationException e)
            {
                return Conflict(MakeError(Opc.Ua.StatusCodes.BadInvalidState, nameof(Opc.Ua.StatusCodes.BadInvalidState), e.Message));
            }
        }

        [HttpPost("jobs/{jobId}/cancel")]
        public async Task<IActionResult> CancelJob(
            Guid jobId, [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
            if (error != null) return error;

            var ok = await _validation.CancelJobAsync(workspace!.Id!.Value, jobId);
            if (!ok) return JobNotFoundOrWrongState();
            return NoContent();
        }

        [HttpPost("jobs/{jobId}/reset")]
        public async Task<IActionResult> ResetJob(
            Guid jobId, [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
            if (error != null) return error;

            var ok = await _validation.ResetJobAsync(workspace!.Id!.Value, jobId);
            if (!ok) return JobNotFoundOrWrongState();
            return NoContent();
        }

        [HttpDelete("jobs/{jobId}")]
        public async Task<IActionResult> DeleteJob(
            Guid jobId, [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
            if (error != null) return error;

            var ok = await _validation.DeleteJobAsync(workspace!.Id!.Value, jobId);
            if (!ok)
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                    "Job not found in this workspace."));
            return NoContent();
        }

        [HttpGet("jobs/{jobId}/result")]
        public async Task<ActionResult<ValidationResultDto>> GetJobResult(
            Guid jobId, [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer);
            if (error != null) return error;

            var result = await _validation.GetJobResultAsync(workspace!.Id!.Value, jobId);
            if (result == null)
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                    "Job not found in this workspace."));
            return Ok(result);
        }

        // ---------------------------------------------------------------- Helpers

        private AuthenticatedUser GetCurrentUser() => AuthenticatedUser.FromClaimsPrincipal(HttpContext.User);

        private IActionResult JobNotFoundOrWrongState()
            => NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                "Job not found in this workspace, or not in a state that allows this action."));

        /// <summary>
        /// Resolves the <c>OpcUa-Server</c> header to a workspace the caller may access.
        /// The workspace is always supplied by the client (no default resolution here).
        /// </summary>
        private async Task<(AuthenticatedUser? user, Workspace? workspace, ObjectResult? error)> ResolveServer(
            string? opcUaServer, bool requireWrite = false)
        {
            var user = GetCurrentUser();
            if (!user.IsAuthenticated || user.UserId == null)
                return (null, null, Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required.")));

            var id = UrnUtils.ParseUrn(opcUaServer);
            if (id == null)
                return (null, null, BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                    "A valid OpcUa-Server header (urn:uuid:<guid>) is required.")));

            var workspace = await _storage.GetWorkspaceAsync(id.Value);
            // 404 (not 403) when inaccessible — don't leak workspace existence.
            if (workspace == null || !WorkspaceAccess.HasAccess(workspace, user))
                return (null, null, NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), "Server not found.")));

            if (requireWrite && !WorkspaceAccess.CanWrite(workspace, user))
                return (null, null, Forbidden(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied),
                    "This workspace is read-only. Only the owner can make changes.")));

            return (user, workspace, null);
        }

        /// <summary>
        /// Verifies the model belongs to the resolved workspace and is a private (owned) model —
        /// validation only applies to a workspace's own private models. Returns an error result,
        /// or null when the model is valid.
        /// </summary>
        private ObjectResult? ResolveModel(Workspace workspace, Guid modelId)
        {
            var modelRef = workspace.Models?.FirstOrDefault(m => m.Id == modelId);
            if (modelRef == null)
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                    $"Model '{modelId}' not found in this workspace."));
            if (!modelRef.IsPrivate)
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable),
                    "Validation is only available for private models."));
            return null;
        }

        private static ErrorResponse MakeError(long code, string symbol, string message) => new()
        {
            StatusCode = new Opc.Ua.RestfulApi.StatusCode { Code = code, Symbol = symbol },
            Message = message,
            Timestamp = DateTime.UtcNow,
        };

        private ObjectResult Forbidden(ErrorResponse error) => StatusCode(403, error);
    }
}
