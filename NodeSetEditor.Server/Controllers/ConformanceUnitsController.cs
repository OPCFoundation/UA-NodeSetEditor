using Microsoft.AspNetCore.Mvc;
using NodeSetEditor.Server.Model;
using NodeSetEditor.Server.Services;
using Opc.Ua.RestfulApi;

namespace NodeSetEditor.Server.Controllers
{
    /// <summary>
    /// User-facing API for viewing a NodeSet by conformance unit. The workspace is passed via
    /// the <c>OpcUa-Server</c> header (same convention as the rest of the app) and every action
    /// re-validates the (workspace, model) linkage — model rows are shared, so a model id from
    /// the caller is never trusted.
    /// </summary>
    [ApiController]
    [Route("api/opcua/v1/conformance-units")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class ConformanceUnitsController : ControllerBase
    {
        private readonly IConformanceUnitService _conformanceUnits;
        private readonly INodeSetStorageService _storage;

        public ConformanceUnitsController(
            IConformanceUnitService conformanceUnits,
            INodeSetStorageService storage)
        {
            _conformanceUnits = conformanceUnits;
            _storage = storage;
        }

        /// <summary>Every conformance unit the model declares, name-sorted, with node counts.</summary>
        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<ConformanceUnitInfo>>> List(
            [FromQuery] Guid modelId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            var (_, workspace, error) = await ResolveServer(opcUaServer);
            if (error != null) return error;

            // Read is allowed for any model linked to the workspace, private or shared.
            var modelError = ResolveModel(workspace!, modelId);
            if (modelError != null) return modelError;

            return Ok(await _conformanceUnits.ListAsync(modelId));
        }

        // ---------------------------------------------------------------- Helpers

        /// <summary>
        /// Resolves the <c>OpcUa-Server</c> header to a workspace the caller may access.
        /// Mirrors <see cref="ValidationController"/>: the workspace is always supplied by the
        /// client, and an inaccessible one reads as 404 so workspace existence doesn't leak.
        /// </summary>
        private async Task<(AuthenticatedUser? user, Workspace? workspace, ObjectResult? error)> ResolveServer(
            string? opcUaServer, bool requireWrite = false)
        {
            var user = AuthenticatedUser.FromClaimsPrincipal(HttpContext.User);
            if (!user.IsAuthenticated || user.UserId == null)
                return (null, null, Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied),
                    "Authentication required.")));

            var id = UrnUtils.ParseUrn(opcUaServer);
            if (id == null)
                return (null, null, BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                    "A valid OpcUa-Server header (urn:uuid:<guid>) is required.")));

            var workspace = await _storage.GetWorkspaceAsync(id.Value);
            if (workspace == null || !WorkspaceAccess.HasAccess(workspace, user))
                return (null, null, NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                    "Server not found.")));

            if (requireWrite && !WorkspaceAccess.CanWrite(workspace, user))
                return (null, null, Forbidden(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied),
                    "This workspace is read-only. Only the owner can make changes.")));

            return (user, workspace, null);
        }

        /// <summary>
        /// Verifies the model is linked to the resolved workspace. Returns an error result, or
        /// null when the model is valid.
        /// </summary>
        private ObjectResult? ResolveModel(Workspace workspace, Guid modelId)
        {
            var modelRef = workspace.Models?.FirstOrDefault(m => m.Id == modelId);
            if (modelRef == null)
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                    $"Model '{modelId}' not found in this workspace."));
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
