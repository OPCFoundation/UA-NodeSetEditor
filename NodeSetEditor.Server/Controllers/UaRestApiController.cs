extern alias JsonNodeSet;

using System.IO.Compression;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NodeSetEditor.Server.Model;
using NodeSetEditor.Server.Services;
using Opc.Ua.RestfulApi;
using JsonNodeSet::NodeSetTool;

namespace NodeSetEditor.Server.Controllers
{
    /// <summary>
    /// Decodes base64url-encoded NodeId path parameters before the controller
    /// action runs. NodeIds are passed in URL paths as base64url slugs (see the
    /// frontend's slugifyNodeId helper) so they don't trigger IIS / proxy URL
    /// filtering on characters like %2F or `;`. This filter restores them to
    /// their plain form so action methods can use them directly.
    ///
    /// Only parameters whose name appears in {@link NodeIdParamNames} are
    /// transformed; other path parameters (workspace IDs, category names,
    /// model GUIDs, etc.) are left untouched.
    /// </summary>
    public class NodeIdSlugFilter : IActionFilter
    {
        private static readonly HashSet<string> NodeIdParamNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "nodeId",
            "parentNodeId",
            "targetNodeId",
            "referenceTypeId",
        };

        public void OnActionExecuting(ActionExecutingContext context)
        {
            foreach (var key in context.ActionArguments.Keys.ToList())
            {
                if (!NodeIdParamNames.Contains(key)) continue;
                if (context.ActionArguments[key] is not string slug) continue;
                if (string.IsNullOrEmpty(slug)) continue;

                context.ActionArguments[key] = DecodeBase64Url(slug);
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }

        /// <summary>
        /// Encodes an arbitrary UTF-8 string as a base64url slug
        /// (RFC 4648 §5: A-Z a-z 0-9 - _, no padding). Mirror of the frontend's
        /// slugifyNodeId helper. Use this from tests / dev tools to build URLs
        /// the same way the SPA does.
        /// </summary>
        public static string EncodeBase64Url(string raw)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        /// <summary>
        /// Decodes a base64url string (RFC 4648 §5: A-Z a-z 0-9 - _, no padding)
        /// into the original UTF-8 string. If the input doesn't parse as valid
        /// base64url, returns it unchanged so direct API consumers (curl, tests)
        /// can still pass plain NodeIds — but the frontend always slugs.
        /// </summary>
        public static string DecodeBase64Url(string slug)
        {
            try
            {
                var s = slug.Replace('-', '+').Replace('_', '/');
                switch (s.Length % 4)
                {
                    case 2: s += "=="; break;
                    case 3: s += "="; break;
                    case 1: return slug; // invalid base64url length — pass through
                }
                var bytes = Convert.FromBase64String(s);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Not valid base64 — assume the caller passed a plain NodeId.
                return slug;
            }
        }
    }

    [ApiController]
    [Route("api/opcua/v1")]
    [ServiceFilter(typeof(NodeIdSlugFilter))]
    // Grouped as "Editor" in the API documentation; the class name is an implementation
    // detail and means nothing to someone reading the published spec.
    [Tags("Editor")]
    // API responses are user-scoped (bearer-token authenticated) and must
    // never be cached by the browser or any intermediate proxy. A stale
    // 304 from an earlier deploy was masking real server behavior.
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class UaRestApiController : ControllerBase
    {
        private readonly INodeSetStorageService _storage;
        private readonly IWorkspaceAddressSpaceService _addressSpace;
        private readonly BetaTesterPolicy _betaTesters;
        private readonly ILogger<UaRestApiController> _logger;

        public UaRestApiController(
            INodeSetStorageService storage,
            IWorkspaceAddressSpaceService addressSpace,
            BetaTesterPolicy betaTesters,
            ILogger<UaRestApiController> logger)
        {
            _storage = storage;
            _addressSpace = addressSpace;
            _betaTesters = betaTesters;
            _logger = logger;
        }

        /// <summary>
        /// The only download format open to everyone. The rest are beta features, gated on
        /// <see cref="BetaTesterPolicy"/>.
        /// </summary>
        private const string OpenExportFormat = "xml";

        #region Discovery & Server CRUD

        /// <summary>
        /// Discover available OPC UA servers (workspaces) accessible to the current user.
        /// </summary>
        [HttpGet("discovery")]
        public async Task<ActionResult<PaginatedResponse<WorkspaceDescription>>> Discover(
            [FromQuery] int start = 0,
            [FromQuery] int count = 100)
        {
            try
            {
                var user = GetCurrentUser();

                if (!user.IsAuthenticated || user.UserId == null)
                {
                    // Returning 200 with empty results here would mask auth failures
                    // as "no workspaces" on the client. Return 401 so the frontend can
                    // distinguish auth problems from a legitimately empty user state.
                    // GetWorkspacesAsync below auto-creates a Default for new users,
                    // so an authenticated user always sees at least one workspace.
                    return Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required."));
                }

                // Provision the user's preference row (display Name / DefaultDomain) so
                // their own workspaces resolve to a Name on the very first paint, even
                // before the client's GET /user/preferences lands.
                await _storage.EnsureUserPreferenceAsync(user.UserId, user.Email, user.DisplayName, user.TenantId);

                var workspaces = await _storage.GetWorkspacesAsync(user.UserId, user.Email);
                var selectedId = await _storage.GetUserSelectedWorkspaceAsync(user.UserId);

                // Determine the default: user's selection, or first workspace
                var defaultId = selectedId;
                if ((!defaultId.HasValue || !workspaces.Any(w => w.Id == defaultId)) && workspaces.Count > 0)
                {
                    defaultId = workspaces[0].Id;
                }

                if (start < 0) start = 0;
                if (count <= 0) count = 100;

                var total = workspaces.Count;
                var paged = workspaces.Skip(start).Take(count).ToList();

                // Batch-resolve owner display Names once for the whole page.
                var ownerNames = await _storage.GetUserDisplayNamesAsync(
                    paged.Where(w => w.Owner != null).Select(w => w.Owner!));
                var results = new List<WorkspaceDescription>(paged.Count);
                foreach (var ws in paged)
                    results.Add(await ToWorkspaceDescriptionAsync(ws, defaultId, ownerNames));

                return Ok(new PaginatedResponse<WorkspaceDescription>
                {
                    Results = results,
                    TotalCount = total
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error discovering servers");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Get server details by applicationUri.
        /// </summary>
        [HttpGet("servers/{applicationUri}")]
        public async Task<ActionResult<WorkspaceDescription>> GetServer(string applicationUri)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(applicationUri);
                if (error != null) return error;

                return Ok(await ToWorkspaceDescriptionAsync(workspace!));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting server {ApplicationUri}", applicationUri);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Create a new server (workspace).
        /// </summary>
        [HttpPost("servers")]
        public async Task<ActionResult<WorkspaceDescription>> CreateServer([FromBody] CreateServerRequest request)
        {
            try
            {
                var user = GetCurrentUser();

                if (!user.IsAuthenticated || user.UserId == null)
                {
                    return Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required."));
                }

                if (string.IsNullOrWhiteSpace(request.ApplicationName))
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "applicationName is required."));
                }

                // Provision the owner's preference row so a display Name exists for
                // this workspace to resolve to in the dropdown.
                await _storage.EnsureUserPreferenceAsync(user.UserId, user.Email, user.DisplayName, user.TenantId);

                var workspace = await _storage.CreateWorkspaceAsync(
                    user.UserId,
                    user.Email,
                    request.ApplicationName,
                    request.Description,
                    request.Acl);

                return CreatedAtAction(
                    nameof(GetServer),
                    new { applicationUri = UrnUtils.ToUrn(workspace.Id!.Value) },
                    await ToWorkspaceDescriptionAsync(workspace));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error creating server");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Update server details.
        /// </summary>
        [HttpPut("servers/{applicationUri}")]
        public async Task<ActionResult<WorkspaceDescription>> UpdateServer(
            string applicationUri,
            [FromBody] UpdateServerRequest request)
        {
            try
            {
                // Updating name/description/ACL is an owner-only operation.
                var (user, workspace, error) = await ResolveServer(applicationUri, requireWrite: true);
                if (error != null) return error;

                var updated = await _storage.UpdateWorkspaceAsync(
                    workspace!.Id!.Value,
                    request.ApplicationName,
                    request.Description,
                    request.Acl);

                return Ok(await ToWorkspaceDescriptionAsync(updated));
            }
            catch (KeyNotFoundException)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Server '{applicationUri}' not found."));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error updating server {ApplicationUri}", applicationUri);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Delete a server.
        /// </summary>
        [HttpDelete("servers/{applicationUri}")]
        public async Task<IActionResult> DeleteServer(string applicationUri)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(applicationUri, requireWrite: true);
                if (error != null) return error;

                await _storage.DeleteWorkspaceAsync(user!.UserId!, workspace!.Id!.Value);

                return NoContent();
            }
            catch (UnauthorizedAccessException e)
            {
                return StatusCode(403, MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), e.Message));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error deleting server {ApplicationUri}", applicationUri);
                return InternalError(e);
            }
        }

        #endregion

        #region Namespaces

        /// <summary>
        /// List namespace info (models) for a workspace identified by the OpcUa-Server header.
        /// </summary>
        [HttpGet("namespaces/info")]
        public async Task<ActionResult<PaginatedResponse<WorkspaceNamespaceInfo>>> ListNamespaceInfo(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] int start = 0,
            [FromQuery] int count = 100)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
                var modelRefs = workspace.Models ?? new List<ModelReference>();

                // Flag models that failed to load
                Dictionary<string, string> badModels;
                try
                {
                    badModels = await _addressSpace.GetBadModelsAsync(workspaceId);
                }
                catch
                {
                    badModels = new Dictionary<string, string>();
                }

                Dictionary<string, List<string>> modelDeps;
                try
                {
                    modelDeps = await _addressSpace.GetModelDependenciesAsync(workspaceId);
                }
                catch
                {
                    modelDeps = new Dictionary<string, List<string>>();
                }

                if (start < 0) start = 0;
                if (count <= 0) count = 100;

                var nonNullModels = models.Where(m => m != null).ToList()!;
                var total = nonNullModels.Count;
                var paged = nonNullModels.Skip(start).Take(count);

                var results = paged.Select((m, idx) =>
                {
                    var modelRef = modelRefs.FirstOrDefault(r => r.Id == m!.Id);
                    return new WorkspaceNamespaceInfo
                    {
                        Index = start + idx,
                        Uri = m?.ModelUri ?? string.Empty,
                        Id = m?.Id,
                        Name = m?.Name,
                        Version = m?.ModelVersion,
                        PublicationDate = m?.PublicationDate != null && DateTime.TryParse(m?.PublicationDate, out var dt) ? dt : null,
                        Description = m?.Description != null
                            ? new Opc.Ua.RestfulApi.LocalizedText { Text = m?.Description }
                            : null,
                        IsPrivate = modelRef?.IsPrivate,
                        IsEditable = modelRef?.IsEditable,
                        // Editable only when the model is private AND checked out.
                        IsReadOnly = !(modelRef?.IsPrivate == true && modelRef?.IsEditable == true),
                        RequiredNamespaceUris = m?.ModelUri != null && modelDeps.TryGetValue(m.ModelUri, out var deps) && deps.Count > 0
                            ? deps
                            : null,
                        HasErrors = m?.HasErrors == true || (m?.ModelUri != null && badModels.ContainsKey(m.ModelUri)),
                        ErrorMessage = m?.ModelUri != null && badModels.TryGetValue(m.ModelUri, out var errMsg) ? errMsg : null,
                        License = m?.License,
                        LicenseUrl = m?.LicenseUrl,
                        CopyrightHolder = m?.CopyrightHolder,
                    };
                }).ToList();

                return Ok(new PaginatedResponse<WorkspaceNamespaceInfo>
                {
                    Results = results,
                    TotalCount = total
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error listing namespace info");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Create a new model in the workspace.
        /// </summary>
        [HttpPost("namespaces/info")]
        public async Task<ActionResult<WorkspaceNamespaceInfo>> CreateNamespace(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] CreateNamespaceRequest request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                if (string.IsNullOrWhiteSpace(request.Uri))
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "uri is required."));
                }

                // Reject reserved/published OPC Foundation namespace URIs
                if (request.Uri.StartsWith("http://opcfoundation.org/UA/", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"The namespace URI '{request.Uri}' is reserved and cannot be used for a new model."));
                }

                var workspaceId = workspace!.Id!.Value;

                // Name is mandatory and must be at least 2 characters.
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length < 2)
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "name is required and must be at least 2 characters."));
                }

                // Check for duplicate name within this workspace
                if (!string.IsNullOrWhiteSpace(request.Name))
                {
                    var existingModels = await _storage.GetWorkspaceModelsAsync(workspaceId);
                    if (existingModels.Any(m => string.Equals(m?.Name, request.Name?.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                            $"A model named '{request.Name}' already exists in this workspace."));
                    }
                }

                // Every model must carry a copyright holder and a license, set once here at genesis.
                if (string.IsNullOrWhiteSpace(request.CopyrightHolder))
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "copyrightHolder is required."));
                }
                var (licOk, resolvedLicenseUrl, licError) = await ResolveLicenseAsync(request.License, request.LicenseUrl);
                if (!licOk)
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), licError!));
                }

                // A brand-new model starts as an editable working copy, so it defaults to the
                // -alpha pre-release version unless the caller specifies one.
                var initialVersion = string.IsNullOrWhiteSpace(request.Version)
                    ? "1.0.0-alpha"
                    : request.Version;

                var nodeSet = new Opc.Ua.Export.UANodeSet
                {
                    NamespaceUris = new[] { request.Uri },
                    Models = new[]
                    {
                        new Opc.Ua.Export.ModelTableEntry
                        {
                            ModelUri = request.Uri,
                            Version = initialVersion,
                            ModelVersion = initialVersion,
                            PublicationDate = DateTime.UtcNow,
                            PublicationDateSpecified = true,
                            RequiredModel = new[]
                            {
                                new Opc.Ua.Export.ModelTableEntry
                                {
                                    ModelUri = "http://opcfoundation.org/UA/",
                                    PublicationDate = DateTime.MinValue,
                                    PublicationDateSpecified = false
                                }
                            }
                        }
                    }
                };

                var modelInfo = await _storage.UploadNodeSetAsync(workspaceId, nodeSet, isPrivate: true, isEditable: true,
                    license: request.License!.Trim(), licenseUrl: resolvedLicenseUrl, copyrightHolder: request.CopyrightHolder!.Trim(),
                    origin: NodeSetEditor.Model.ModelOrigin.Authored);

                if (!string.IsNullOrWhiteSpace(request.Name) || !string.IsNullOrWhiteSpace(request.Description))
                {
                    await _storage.UpdateModelInfoAsync(
                        workspaceId,
                        modelInfo.Id ?? Guid.Empty,
                        name: request.Name?.Trim(),
                        version: null,
                        description: request.Description?.Trim());

                    if (!string.IsNullOrWhiteSpace(request.Name))
                        modelInfo.Name = request.Name.Trim();
                    if (!string.IsNullOrWhiteSpace(request.Description))
                        modelInfo.Description = request.Description.Trim();
                }

                // Rebuild so the new model is in the address space, then create its NamespaceMetadata
                // object through the instantiation engine (all mandatory children) and invalidate again.
                _addressSpace.Invalidate(workspaceId);
                await EnsureNamespaceMetadataObjectAsync(workspaceId, modelInfo.ModelUri);

                return CreatedAtAction(
                    nameof(ListNamespaceInfo),
                    ToNamespaceInfo(modelInfo, isPrivate: true, isEditable: true));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error creating namespace");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Update model metadata.
        /// </summary>
        [HttpPut("namespaces/info/{id}")]
        public async Task<ActionResult<WorkspaceNamespaceInfo>> UpdateNamespace(
            Guid id,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] UpdateNamespaceRequest request)
        {
            try
            {
                if (!string.IsNullOrEmpty(request.Uri))
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "The model URI cannot be changed after creation."));
                }

                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;

                var modelRef = workspace.Models?.FirstOrDefault(r => r.Id == id);

                // The target model must belong to the resolved workspace. Without this,
                // a writable-workspace owner could edit any model by id (model rows are
                // shared across workspaces). The storage layer enforces the same check;
                // this returns a clean 404 instead of surfacing it as a 500.
                if (modelRef == null)
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound),
                        $"Model '{id}' not found in this workspace."));
                }

                // While a model is checked out, the checkout/check-in lifecycle owns its version
                // (the -alpha/-beta progression). Reject manual version edits to avoid colliding
                // with the unique (Uri, VersionNorm) index or desyncing the lifecycle.
                if (!string.IsNullOrEmpty(request.Version) && modelRef?.IsEditable == true)
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "The version cannot be changed while the model is checked out for editing."));
                }

                // License/copyright are editable, but only on the user's own private models
                // (shared/published models stay read-only). Validate when a change is requested.
                var editingLicense = request.License != null || request.CopyrightHolder != null || request.LicenseUrl != null;
                string? resolvedLicenseUrl = null;
                if (editingLicense)
                {
                    if (modelRef?.IsPrivate != true)
                    {
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                            "License and copyright can only be changed on your own private models."));
                    }
                    if (string.IsNullOrWhiteSpace(request.CopyrightHolder))
                    {
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                            "copyrightHolder is required."));
                    }
                    var (licOk, licUrl, licErr) = await ResolveLicenseAsync(request.License, request.LicenseUrl);
                    if (!licOk)
                    {
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), licErr!));
                    }
                    resolvedLicenseUrl = licUrl;
                }

                var modelInfo = await _storage.UpdateModelInfoAsync(
                    workspaceId,
                    id,
                    name: request.Name,
                    version: request.Version,
                    description: request.Description,
                    license: editingLicense ? request.License!.Trim() : null,
                    licenseUrl: editingLicense ? resolvedLicenseUrl : null,
                    copyrightHolder: editingLicense ? request.CopyrightHolder!.Trim() : null,
                    enforceReadOnlyReserved: true);

                return Ok(ToNamespaceInfo(modelInfo, modelRef?.IsPrivate, modelRef?.IsEditable));
            }
            catch (KeyNotFoundException)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Model '{id}' not found."));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error updating namespace {Id}", id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Remove a model from the workspace.
        /// </summary>
        [HttpDelete("namespaces/info/{id}")]
        public async Task<IActionResult> DeleteNamespace(
            Guid id,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;

                // Find the model URI from the ID
                var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
                var model = models.FirstOrDefault(m => m?.Id == id);
                if (model == null || string.IsNullOrEmpty(model.ModelUri))
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Model '{id}' not found."));
                }

                await _addressSpace.RemoveModelAsync(workspaceId, model.ModelUri);
                // Rebuild the cached address space from the DB. RemoveModelAsync surgically drops
                // the model from the in-memory address space by URI; when a private copy of a shared
                // model is deleted, the shared link still remains in the workspace and must be
                // reloaded so the original model reappears in the tree.
                _addressSpace.Invalidate(workspaceId);

                return NoContent();
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error removing namespace {Id}", id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// List every stored version of this model's namespace URI that the workspace can see,
        /// with provenance and whether each may be deleted from here.
        /// </summary>
        [HttpGet("namespaces/info/{id}/versions")]
        public async Task<ActionResult<List<ModelVersionInfo>>> ListModelVersions(
            Guid id,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                return Ok(await _storage.GetModelVersionsAsync(workspace!.Id!.Value, id));
            }
            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error listing versions for namespace {Id}", id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Delete one stored version of this model's namespace URI. Only a private version the
        /// workspace is not using and no other workspace references can go.
        /// </summary>
        [HttpDelete("namespaces/info/{id}/versions/{versionId}")]
        public async Task<ActionResult<List<ModelVersionInfo>>> DeleteModelVersion(
            Guid id,
            Guid versionId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                await _storage.DeleteModelVersionAsync(workspaceId, id, versionId);
                // A deleted version can be the backup behind the model currently loaded, so the
                // cached address space has to be rebuilt from what is left.
                _addressSpace.Invalidate(workspaceId);

                // Return the remaining versions so the dialog can render the result directly.
                return Ok(await _storage.GetModelVersionsAsync(workspaceId, id));
            }
            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error deleting version {VersionId} of namespace {Id}", versionId, id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// List published models that can be linked into a workspace. These are models
        /// explicitly published by their authors (and therefore available to other users),
        /// as opposed to the external Cloud Library.
        /// </summary>
        [HttpGet("namespaces/shared")]
        public async Task<ActionResult<List<ModelInfo>>> ListSharedModels(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                // Collapse to the single latest version per namespace URI. The version
                // picker (a separate workspace-model feature) is where older versions
                // are chosen; this list shows one row per model.
                var latest = (await _storage.GetPublishedModelsAsync())
                    .Where(m => !string.IsNullOrEmpty(m.ModelUri))
                    .GroupBy(m => m.ModelUri!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g
                        .OrderByDescending(m => ModelInfo.ParseVersion(m.ModelVersion))
                        .ThenByDescending(m => ModelInfo.ParsePublicationDate(m.PublicationDate))
                        .First())
                    .ToList();

                return Ok(latest);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error listing shared models");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Link an existing model from the shared index into the workspace.
        /// </summary>
        [HttpPost("namespaces/info/{id}/link")]
        public async Task<ActionResult<PaginatedResponse<WorkspaceNamespaceInfo>>> LinkNamespace(
            Guid id,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] LinkNamespaceRequest request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;

                // Linking always creates a SHARED, read-only reference. A private working copy is
                // only ever produced by import/create/checkout (which mint a model the caller owns),
                // never by linking an existing model id — honoring a client-supplied isPrivate=true
                // here would let the private code path pull an arbitrary model into the workspace.
                // The request's IsPrivate is therefore intentionally ignored. AddModelAsync also
                // gates the id to the linkable (shared/published/cloud) set.
                await _addressSpace.AddModelAsync(workspaceId, id, isPrivate: false);
                // Rebuild the cached address space from the DB. AddModelAsync patches the model
                // into the in-memory address space incrementally, which can leave the browse tree
                // inconsistent (notably after a delete→re-add cycle: nodes load but the tree comes
                // up empty). A clean rebuild — as every other mutating endpoint does — avoids that.
                _addressSpace.Invalidate(workspaceId);

                // Return updated list
                return await ListNamespaceInfo(opcUaServer);
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error linking namespace {Id}", id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Check a model out for editing. Mints a new <c>-alpha</c> working copy (or re-enables a
        /// previously kept one) and returns the refreshed model list.
        /// </summary>
        [HttpPost("namespaces/info/{id}/checkout")]
        public async Task<ActionResult<PaginatedResponse<WorkspaceNamespaceInfo>>> CheckoutNamespace(
            Guid id,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;

                await _storage.CheckoutModelAsync(workspaceId, id);
                _addressSpace.Invalidate(workspaceId);

                return await ListNamespaceInfo(opcUaServer);
            }
            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error checking out namespace {Id}", id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Check a model in. The request action is "keep" (lock it private, dropping the
        /// <c>-alpha</c> working-copy suffix), "publish" (<c>-alpha</c>→<c>-beta</c>, make
        /// public) or "discard" (delete the working copy and restore the prior good version).
        /// "keep" and "publish" both accept a caller-chosen version. Returns the refreshed
        /// model list.
        /// </summary>
        [HttpPost("namespaces/info/{id}/checkin")]
        public async Task<ActionResult<PaginatedResponse<WorkspaceNamespaceInfo>>> CheckinNamespace(
            Guid id,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] CheckinNamespaceRequest request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;

                // Creator is the publisher's email with the domain stripped (e.g. "randy"),
                // kept as a fallback snapshot. CreatorUserId lets the shared-model picker
                // resolve the publisher's current display Name. Provision the row so a Name
                // exists to resolve.
                var creator = StripEmailDomain(user?.Email);
                if (user?.UserId != null)
                    await _storage.EnsureUserPreferenceAsync(user.UserId, user.Email, user.DisplayName, user.TenantId);

                await _storage.CheckinModelAsync(workspaceId, id, request.Action ?? string.Empty,
                    request.Version, request.Description, creator, user?.UserId);
                _addressSpace.Invalidate(workspaceId);

                return await ListNamespaceInfo(opcUaServer);
            }
            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (ModelVersionConflictException e)
            {
                return Conflict(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error checking in namespace {Id}", id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Import a NodeSet file into the workspace (chunked upload).
        /// </summary>
        /// <summary>
        /// Detect (without importing) the license/copyright embedded in an uploaded NodeSet file so
        /// the import dialog can pre-fill values for the user to confirm or override. Returns nulls
        /// for anything not detected.
        /// </summary>
        [HttpPost("namespaces/info/detect-license")]
        [RequestSizeLimit(200_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
        public async Task<ActionResult<LicenseDetectionResult>> DetectLicense([FromForm] IFormFile file)
        {
            try
            {
                var user = GetCurrentUser();
                if (!user.IsAuthenticated)
                {
                    return Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required."));
                }

                using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                var bytes = buffer.ToArray();

                string? modelUri = null;
                try
                {
                    buffer.Position = 0;
                    var ns = Opc.Ua.Export.UANodeSet.Read(buffer);
                    modelUri = ns.Models?.FirstOrDefault()?.ModelUri;
                }
                catch { /* unparseable / not XML — still try header parsing on the raw bytes */ }

                var (license, url, copyright) = NodeSetEditor.Model.SpdxHeaders.ResolveForImport(bytes, modelUri);

                // For a known catalog license, surface the catalog's canonical URL.
                if (!string.IsNullOrWhiteSpace(license))
                {
                    var opt = (await _storage.GetLicenseOptionsAsync())
                        .FirstOrDefault(o => !o.IsCustom && string.Equals(o.SpdxId, license, StringComparison.OrdinalIgnoreCase));
                    if (opt != null) url = opt.ReferenceUrl;
                }

                return Ok(new LicenseDetectionResult
                {
                    License = license,
                    LicenseUrl = url,
                    CopyrightHolder = copyright,
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error detecting license for uploaded NodeSet");
                return InternalError(e);
            }
        }

        [HttpPost("namespaces/info/import")]
        [RequestSizeLimit(200_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
        public async Task<ActionResult<UploadResult>> ImportNamespace(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromForm] IFormFile file,
            [FromForm] string fileName,
            [FromForm] int chunkIndex = 0,
            [FromForm] int totalChunks = 1,
            [FromForm] string? uploadId = null,
            [FromForm] string? license = null,
            [FromForm] string? licenseUrl = null,
            [FromForm] string? copyrightHolder = null)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;

                using var stream = file.OpenReadStream();
                var result = await _storage.HandleUploadChunkAsync(
                    workspaceId, stream, fileName, chunkIndex, totalChunks, uploadId,
                    license, licenseUrl, copyrightHolder);
                _addressSpace.Invalidate(workspaceId);

                // If the imported NodeSet didn't carry its own NamespaceMetadata object, create one
                // via the instantiation engine (same code path as creating an instance of a type).
                if (result.IsComplete && !string.IsNullOrEmpty(result.Model?.ModelUri))
                    await EnsureNamespaceMetadataObjectAsync(workspaceId, result.Model!.ModelUri);

                return Ok(result);
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (FormatException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error importing namespace file");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Export a model in the specified format.
        /// </summary>
        [HttpGet("namespaces/info/{id}/export")]
        public async Task<IActionResult> ExportNamespace(
            Guid id,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] string format = "xml",
            [FromQuery] bool includeDependencies = false)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;

                var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
                var modelInfo = models.FirstOrDefault(m => m?.Id == id);

                if (modelInfo == null || string.IsNullOrEmpty(modelInfo.ModelUri))
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Model '{id}' not found."));
                }

                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);
                var fmt = format.ToLowerInvariant();

                // Everything but XML is still in beta. Checked here so it covers the bundle path
                // too, which serializes every dependency in the same format.
                if (fmt != OpenExportFormat && !_betaTesters.IsBetaTester(user?.Email))
                {
                    return StatusCode(403, MakeError(
                        Opc.Ua.StatusCodes.BadUserAccessDenied,
                        nameof(Opc.Ua.StatusCodes.BadUserAccessDenied),
                        $"The '{fmt}' download format is not available for this account."));
                }

                if (includeDependencies)
                {
                    return await ExportNamespaceBundle(workspaceId, modelInfo, models, addressSpace, fmt);
                }

                var ms = SerializeModel(addressSpace, modelInfo.ModelUri!, fmt, out var contentType, out var extension,
                    modelInfo.CopyrightHolder, modelInfo.License, modelInfo.LicenseUrl);
                var fileName = GenerateExportFileName(modelInfo, extension);
                return File(ms, contentType, fileName);
            }
            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error exporting namespace {Id}", id);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Export a model together with every dependency that resolves its
        /// references and is also present in the workspace, bundled into a single
        /// ZIP. Required models that are not in the workspace are ignored, so the
        /// bundle is always a subset of the workspace's models.
        /// </summary>
        private async Task<IActionResult> ExportNamespaceBundle(
            Guid workspaceId,
            ModelInfo modelInfo,
            IReadOnlyList<ModelInfo?> models,
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string fmt)
        {
            var modelByUri = models
                .Where(m => m != null && !string.IsNullOrEmpty(m!.ModelUri))
                .GroupBy(m => m!.ModelUri!)
                .ToDictionary(g => g.Key, g => g.First()!);

            Dictionary<string, List<string>> modelDeps;
            try
            {
                modelDeps = await _addressSpace.GetModelDependenciesAsync(workspaceId);
            }
            catch
            {
                modelDeps = new Dictionary<string, List<string>>();
            }

            var bundleUris = ResolveDependencyClosure(modelInfo.ModelUri!, modelByUri.Keys, modelDeps);

            var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var uri in bundleUris)
                {
                    if (!modelByUri.TryGetValue(uri, out var depInfo)) continue;

                    using var modelStream = SerializeModel(addressSpace, uri, fmt, out _, out var ext,
                        depInfo.CopyrightHolder, depInfo.License, depInfo.LicenseUrl);

                    var entryName = GenerateExportFileName(depInfo, ext);
                    if (!usedNames.Add(entryName))
                    {
                        var bareName = entryName.Substring(0, entryName.Length - ext.Length);
                        var n = 2;
                        while (!usedNames.Add($"{bareName}_{n}{ext}")) n++;
                        entryName = $"{bareName}_{n}{ext}";
                    }

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    modelStream.CopyTo(entryStream);
                }
            }

            zipStream.Seek(0, SeekOrigin.Begin);
            var zipName = GenerateExportFileName(modelInfo, ".zip");
            return File(zipStream, "application/zip", zipName);
        }

        /// <summary>
        /// Returns the target model URI plus the transitive set of its required
        /// models, restricted to URIs that exist in the workspace. Dependencies
        /// outside the workspace are skipped (and so are their sub-dependencies).
        /// </summary>
        private static List<string> ResolveDependencyClosure(
            string rootUri,
            IEnumerable<string> workspaceUris,
            Dictionary<string, List<string>> modelDeps)
        {
            var workspaceSet = new HashSet<string>(workspaceUris);
            var result = new List<string>();
            var visited = new HashSet<string> { rootUri };
            var queue = new Queue<string>();
            queue.Enqueue(rootUri);

            while (queue.Count > 0)
            {
                var uri = queue.Dequeue();
                result.Add(uri);

                if (!modelDeps.TryGetValue(uri, out var deps)) continue;
                foreach (var dep in deps)
                {
                    if (!workspaceSet.Contains(dep)) continue;
                    if (visited.Add(dep)) queue.Enqueue(dep);
                }
            }

            return result;
        }

        /// <summary>
        /// Serializes a single model from the address space into a seekable stream
        /// using the requested format, returning the matching content type and
        /// file extension.
        /// </summary>
        private static MemoryStream SerializeModel(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string modelUri,
            string fmt,
            out string contentType,
            out string extension,
            string? copyrightHolder = null,
            string? license = null,
            string? licenseUrl = null)
        {
            var serializer = NodeSetSerializer.FromAddressSpace(addressSpace, modelUri);

            // JSON-family NodeSets carry SPDX license/copyright as a structured header object (JSON has
            // no comment syntax); the XML path injects comment headers post-serialization instead. The
            // header is written once — on the first file of a multi-file archive. (JSON-LD conveys
            // provenance through its own RDF/OWL vocabulary and is left untouched here.)
            if (fmt is "json" or "compressed")
            {
                var copyrightText = NodeSetEditor.Model.SpdxHeaders.FormatCopyrightText(copyrightHolder);
                if (copyrightText != null
                    || !string.IsNullOrWhiteSpace(license)
                    || !string.IsNullOrWhiteSpace(licenseUrl))
                {
                    serializer.Spdx = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.SpdxDeclaration
                    {
                        CopyrightText = copyrightText,
                        LicenceId = string.IsNullOrWhiteSpace(license) ? null : license,
                        // The URL is only meaningful for a custom (LicenseRef-…) license; a standard
                        // SPDX id is self-describing, so its URL is omitted (matches the XML headers).
                        LicenceRef = !string.IsNullOrWhiteSpace(licenseUrl)
                            && NodeSetEditor.Model.SpdxHeaders.IsCustomLicenseId(license)
                            ? licenseUrl : null,
                    };
                }
            }

            var ms = new MemoryStream();

            switch (fmt)
            {
                case "json":
                    serializer.SaveJson(ms);
                    contentType = "application/json";
                    extension = ".json";
                    break;
                case "compressed":
                    serializer.SaveArchive(ms, 10000);
                    contentType = "application/gzip";
                    extension = ".uanodeset";
                    break;
                // "jsonld" is deliberately absent. RDF/JSON-LD is a prototype of a draft encoding
                // and ships in its own assembly, which this build does not reference — so there is
                // no route to serve it and an asking client falls through to XML below.
                default:
                    serializer.SaveXml(ms);
                    contentType = "text/xml";
                    extension = ".xml";
                    // Emit the SPDX copyright/license/URL headers so they round-trip on export.
                    ms.Seek(0, SeekOrigin.Begin);
                    ms = NodeSetEditor.Model.SpdxHeaders.InjectIntoXml(ms, copyrightHolder, license, licenseUrl);
                    break;
            }

            ms.Seek(0, SeekOrigin.Begin);
            return ms;
        }

        #endregion

        #region Nodes

        /// <summary>
        /// Get a node with all its attributes.
        /// </summary>
        [HttpGet("nodes/{nodeId}")]
        public async Task<ActionResult<Opc.Ua.RestfulApi.Node>> GetNode(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var uaNode = addressSpace.Read(nodeId);
                if (uaNode == null)
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));
                }

                var restNode = UaNodeToRestNode(uaNode, addressSpace);

                // The structural parent, which the list endpoints each set for themselves
                // after mapping. A null one is meaningful here: it marks a top-level node
                // (types are always top-level; an Object or Variable created outside any
                // parent is too), which is where conformance units belong.
                restNode.ParentNodeId = uaNode.ParentId;

                // Add supertype chain
                restNode.SuperTypeIds = GetSuperTypeIds(addressSpace, nodeId);
                if (restNode.SuperTypeIds.Count > 0)
                {
                    restNode.SuperTypeId = restNode.SuperTypeIds[^1];
                }

                return Ok(restNode);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting node {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Returns the structural ancestor path of a node, ordered from the
        /// top-level type/object/variable down to (and including) the node
        /// itself, each entry carrying <c>ParentNodeId</c> (null on the root).
        ///
        /// The path follows <c>ParentId</c> alone — the authoritative
        /// structural parent recorded on each node. It is acyclic and stops
        /// naturally at the owning top-level node: instance declarations point
        /// up to their parent and ultimately to the owning type, whose own
        /// <c>ParentId</c> is null (a type's place in the address space is its
        /// supertype chain, not a parent). Used by the tree's "sync" action to
        /// re-root the focused view at that top-level node and expand down to
        /// the displayed node (e.g. AmberType → Pink → EngineeringUnits).
        /// </summary>
        [HttpGet("nodes/{nodeId}/path")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> GetNodePath(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var chain = new List<Opc.Ua.RestfulApi.Node>();
                var current = nodeId;

                // ParentId SHOULD be acyclic (see summary), but it comes from
                // uploaded nodeset data — guard against a malicious/corrupt cycle
                // so the walk can't loop forever.
                var visited = new HashSet<string>(StringComparer.Ordinal);

                while (!string.IsNullOrEmpty(current) && visited.Add(current))
                {
                    var ua = addressSpace.Read(current);
                    if (ua == null) break;

                    var rest = UaNodeToRestNode(ua, addressSpace);
                    chain.Add(rest);

                    var parent = ua.ParentId;
                    if (string.IsNullOrEmpty(parent)) break;

                    rest.ParentNodeId = parent;
                    current = parent;
                }

                chain.Reverse();

                // Surface the root's supertype chain so the client can also
                // navigate the complete type tree to the owning type (the
                // type categories expand along SuperTypeIds, not ParentNodeId).
                if (chain.Count > 0)
                {
                    var root = chain[0];
                    root.SuperTypeIds = GetSuperTypeIds(addressSpace, root.NodeId);
                    if (root.SuperTypeIds.Count > 0)
                        root.SuperTypeId = root.SuperTypeIds[^1];
                }

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = chain,
                    TotalCount = chain.Count
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting path for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Get references for a node.
        /// </summary>
        [HttpGet("nodes/{nodeId}/references")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.ReferenceDescription>>> GetReferences(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] int start = 0,
            [FromQuery] int count = 1000)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var refs = addressSpace.Browse(nodeId, null, includeForward: true, includeInverse: true);

                // Structural parent of the node we're viewing — used when the
                // ref shows up as inverse (X←Y) so we can flag the row as the
                // canonical parent link (X is the child, Y is its parent).
                var sourceUaNode = addressSpace.Read(nodeId);
                var sourceParent = sourceUaNode != null
                    ? GetStructuralParent(addressSpace, sourceUaNode)
                    : null;

                var results = refs.Select(r =>
                {
                    var targetNode = addressSpace.Read(r.TargetNodeId);
                    var refNode = addressSpace.Read(r.ReferenceTypeId);

                    bool isCanonicalParent;
                    if (r.IsForward)
                    {
                        // X → Y. Canonical iff Y's structural parent is X via
                        // this reference type.
                        var targetParent = targetNode != null
                            ? GetStructuralParent(addressSpace, targetNode)
                            : null;
                        isCanonicalParent = targetParent.HasValue
                            && targetParent.Value.parentNodeId == nodeId
                            && targetParent.Value.referenceTypeId == r.ReferenceTypeId;
                    }
                    else
                    {
                        // X ← Y (inverse). Canonical iff X's own structural
                        // parent is Y via this reference type.
                        isCanonicalParent = sourceParent.HasValue
                            && sourceParent.Value.parentNodeId == r.TargetNodeId
                            && sourceParent.Value.referenceTypeId == r.ReferenceTypeId;
                    }

                    return new Opc.Ua.RestfulApi.ReferenceDescription
                    {
                        ReferenceTypeId = r.ReferenceTypeId,
                        ReferenceTypeName = refNode != null
                            ? (GetLocalizedText(refNode.DisplayName) ?? refNode.BrowseName)
                            : null,
                        IsForward = r.IsForward,
                        TargetNodeId = r.TargetNodeId,
                        TargetBrowseName = targetNode?.BrowseName,
                        TargetDisplayName = targetNode != null
                            ? new Opc.Ua.RestfulApi.LocalizedText
                            {
                                Text = GetLocalizedText(targetNode.DisplayName)
                                    ?? targetNode.BrowseName
                            }
                            : null,
                        TargetNodeClass = targetNode?.NodeClass?.ToString(),
                        IsCanonicalParent = isCanonicalParent,
                    };
                }).ToList();

                if (start < 0) start = 0;
                if (count <= 0) count = 1000;

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.ReferenceDescription>
                {
                    Results = results.Skip(start).Take(count).ToList(),
                    TotalCount = results.Count
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting references for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Create a top-level instance node (Object or Variable) with no parent.
        /// The node has no ParentId and no hierarchical back-reference, making it
        /// a standalone entry point visible under "Object (top-level)" in queries.
        /// </summary>
        [HttpPost("nodes")]
        public async Task<ActionResult<Opc.Ua.RestfulApi.Node>> CreateTopLevelNode(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] CreateNodeRequest request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                if (string.IsNullOrWhiteSpace(request.ModelUri))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "modelUri is required."));
                if (string.IsNullOrWhiteSpace(request.BrowseName))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "browseName is required."));

                var ncEnum = request.NodeClass switch
                {
                    "Object" => (JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass?)JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObject,
                    "Variable" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable,
                    _ => null
                };
                if (ncEnum == null)
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"nodeClass must be 'Object' or 'Variable'; got '{request.NodeClass}'."));

                var numericId = await _addressSpace.GetNextNodeIdAsync(workspaceId, request.ModelUri);
                var nodeId = $"nsu={request.ModelUri};i={numericId}";
                var browseNameNs = request.BrowseNameModelUri ?? request.ModelUri;
                var qualifiedBrowseName = $"nsu={browseNameNs};{request.BrowseName}";
                // A blank DisplayName means "unset" — an empty LocalizedText would survive
                // into the node and leave it nameless in every list, so fall back to the
                // BrowseName exactly as a missing DisplayName does.
                var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? request.BrowseName : request.DisplayName;

                var lt = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                {
                    T = new List<List<string>> { new() { "", displayName } }
                };

                // DesignToolOnly: top-level Variables are always design-tool-only;
                // top-level Objects may opt in via the request. A design-tool-only
                // node is a standalone marker — no references (not even
                // HasTypeDefinition), no children, instantiation rules skipped.
                bool designToolOnly =
                    ncEnum.Value == JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable
                    || request.DesignToolOnly == true;

                // A design-tool-only node keeps its defining HasTypeDefinition as
                // read-only metadata, but takes no children and no other
                // references; instantiation of mandatory children is skipped.
                // Only HasTypeDefinition — no hierarchical parent reference.
                var references = new List<JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Reference>();
                if (!string.IsNullOrEmpty(request.TypeDefinitionId))
                    references.Add(new() { ReferenceTypeId = "i=40", TargetId = request.TypeDefinitionId, IsForward = true });

                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UANode uaNode = ncEnum.Value == JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable
                    ? new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = null, TypeId = request.TypeDefinitionId,
                        References = references, DesignToolOnly = designToolOnly ? true : null,
                        DataType = request.DataType, ValueRank = request.ValueRank,
                        ArrayDimensions = SanitizeArrayDimensions(request.ValueRank, request.ArrayDimensions),
                    }
                    : new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObject
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = null, TypeId = request.TypeDefinitionId,
                        References = references, DesignToolOnly = designToolOnly ? true : null,
                    };

                if (!string.IsNullOrEmpty(request.Description))
                {
                    uaNode.Description = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                    {
                        T = new List<List<string>> { new() { "", request.Description } }
                    };
                }

                uaNode.ConformanceUnits = NormalizeConformanceUnits(request.Category);

                addressSpace.AddNode(uaNode, null);

                var changeset = new ModelChangeset();
                changeset.Nodes.Add(BuildNodeChange(uaNode, ChangeKind.Upsert));
                foreach (var r in references)
                {
                    changeset.References.Add(new ReferenceChange
                    {
                        Kind = ChangeKind.Upsert,
                        SourceNodeId = nodeId,
                        ReferenceTypeId = r.ReferenceTypeId!,
                        TargetNodeId = r.TargetId!,
                        IsForward = r.IsForward ?? true
                    });
                }

                await PersistModelAsync(addressSpace, workspaceId, nodeId, changeset);
                _addressSpace.Invalidate(workspaceId);

                return CreatedAtAction(nameof(GetNode), new { nodeId }, UaNodeToRestNode(uaNode, addressSpace));
            }

            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error creating top-level node");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Create a child node under a parent. Server allocates the NodeId.
        /// </summary>
        [HttpPost("nodes/{parentNodeId}/children")]
        public async Task<ActionResult<Opc.Ua.RestfulApi.Node>> CreateChildNode(
            string parentNodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] CreateNodeRequest request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                if (string.IsNullOrWhiteSpace(request.ModelUri))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "modelUri is required."));
                if (string.IsNullOrWhiteSpace(request.BrowseName))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "browseName is required."));
                if (string.IsNullOrWhiteSpace(request.NodeClass))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "nodeClass is required."));

                var parentNode = addressSpace.Read(parentNodeId);
                if (parentNode == null)
                {
                    // Diagnostic: include a sample of nodes from the same model so the
                    // caller can see whether the AddressSpace got wiped (empty list) or
                    // the parent's NodeId mutated (different ids present).
                    var sameModelPrefix = parentNodeId.StartsWith("nsu=", StringComparison.Ordinal)
                        ? parentNodeId[..(parentNodeId.IndexOf(';') + 1)]
                        : "";
                    var siblings = addressSpace.Nodes
                        .Where(n => n.NodeId != null
                            && (sameModelPrefix.Length == 0 || n.NodeId.StartsWith(sameModelPrefix, StringComparison.Ordinal)))
                        .Take(20)
                        .Select(n => n.NodeId)
                        .ToList();
                    var msg = $"Parent node '{parentNodeId}' not found. Same-model NodeIds present: [{string.Join(", ", siblings)}]";
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), msg));
                }

                // A design-tool-only node is a standalone marker: it takes no children.
                if (parentNode.DesignToolOnly == true)
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"Node '{parentNodeId}' is design-tool-only; children are not allowed."));

                // A Property may not be the source of hierarchical references, so it
                // can have no children.
                if (IsPropertyNode(addressSpace, parentNode))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "Adding children to Properties is not permitted."));

                // ModellingRule only belongs on instance declarations inside a
                // type tree. Strip it when the parent is a regular instance so
                // callers (e.g. InstantiateDialog copying the type's own rule)
                // can't accidentally tag plain instance children as Mandatory.
                var effectiveModellingRuleId =
                    addressSpace.IsInsideTypeTree(parentNode) ? request.ModellingRuleId : null;

                // Allocate NodeId
                var numericId = await _addressSpace.GetNextNodeIdAsync(workspaceId, request.ModelUri);
                var nodeId = $"nsu={request.ModelUri};i={numericId}";
                var browseNameNs = request.BrowseNameModelUri ?? request.ModelUri;
                var qualifiedBrowseName = $"nsu={browseNameNs};{request.BrowseName}";
                // Blank DisplayName == unset; see CreateNode.
                var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? request.BrowseName : request.DisplayName;

                var ncEnum = request.NodeClass switch
                {
                    "Object" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObject,
                    "Variable" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable,
                    "Method" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAMethod,
                    "ObjectType" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObjectType,
                    "VariableType" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariableType,
                    "DataType" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UADataType,
                    "ReferenceType" => JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAReferenceType,
                    _ => (JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass?)null
                };

                if (ncEnum == null)
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), $"Invalid nodeClass: '{request.NodeClass}'."));

                if (ncEnum == JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAReferenceType)
                {
                    // A new ReferenceType is created as a subtype, so the parent it hangs
                    // off decides whether it is hierarchical.
                    var attrError = ValidateReferenceTypeAttributes(
                        request.Symmetric == true, request.InverseName,
                        addressSpace.IsTypeOf(parentNodeId, HIERARCHICAL_REFERENCES));
                    if (attrError != null)
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), attrError));
                }

                if (ncEnum == JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UADataType
                    && request.IsOptionSet == true
                    && !addressSpace.IsTypeOf(parentNodeId, UINTEGER))
                {
                    // The fields of an OptionSet are bit positions in the underlying
                    // unsigned integer, so there has to be one to index into.
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "isOptionSet requires a DataType derived from UInteger."));
                }

                var refTypeId = request.ReferenceTypeId ?? "i=47";
                var references = new List<JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Reference>
                {
                    new() { ReferenceTypeId = refTypeId, TargetId = parentNodeId, IsForward = false },
                };

                if (!string.IsNullOrEmpty(request.TypeDefinitionId))
                    references.Add(new() { ReferenceTypeId = "i=40", TargetId = request.TypeDefinitionId, IsForward = true });

                if (!string.IsNullOrEmpty(effectiveModellingRuleId))
                    references.Add(new() { ReferenceTypeId = "i=37", TargetId = effectiveModellingRuleId, IsForward = true });

                // For HasSubtype references, the reference direction is forward from parent
                if (refTypeId == HAS_SUBTYPE)
                {
                    references.Clear();
                    references.Add(new() { ReferenceTypeId = HAS_SUBTYPE, TargetId = parentNodeId, IsForward = false });
                }

                var lt = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                {
                    T = new List<List<string>> { new() { "", displayName } }
                };

                // Type nodes (HasSubtype) are top-level; instance nodes have a ParentId
                var isTypeNode = refTypeId == HAS_SUBTYPE;
                var nodeParentId = isTypeNode ? null : parentNodeId;

                // A new Variable child starts with the default value its declaration carries.
                // When the caller names the declaration (the Instantiate Children dialog does),
                // that node is the value source. Otherwise — and when the declaration itself
                // carries no value — the default can still live several types up: a subtype
                // that overrides an inherited child re-declares only the child, so the value
                // stays on the supertype's copy at the same BrowseName path. ResolveDefaultValue
                // does that walk and falls back to the child's own TypeDefinition.
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant? defaultValue = null;
                if (ncEnum == JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable && !isTypeNode)
                {
                    if (!string.IsNullOrEmpty(request.SourceNodeId))
                        defaultValue = (addressSpace.Read(request.SourceNodeId!) as JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable)?.Value;

                    defaultValue ??= addressSpace.ResolveDefaultValue(
                        parentNodeId, qualifiedBrowseName, request.TypeDefinitionId);
                }

                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UANode uaNode = ncEnum.Value switch
                {
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = nodeParentId, TypeId = request.TypeDefinitionId,
                        ModellingRuleId = effectiveModellingRuleId, References = references,
                        DataType = request.DataType, ValueRank = request.ValueRank,
                        ArrayDimensions = SanitizeArrayDimensions(request.ValueRank, request.ArrayDimensions),
                        Value = defaultValue,
                    },
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAMethod => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAMethod
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = nodeParentId,
                        ModellingRuleId = effectiveModellingRuleId, References = references,
                    },
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObjectType => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObjectType
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = nodeParentId,
                        References = references, IsAbstract = request.IsAbstract,
                    },
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariableType => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = nodeParentId,
                        References = references, IsAbstract = request.IsAbstract,
                        DataType = request.DataType, ValueRank = request.ValueRank,
                        ArrayDimensions = SanitizeArrayDimensions(request.ValueRank, request.ArrayDimensions),
                    },
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UADataType => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = nodeParentId,
                        References = references, IsAbstract = request.IsAbstract,
                        // Being an OptionSet lives on the DataTypeDefinition, not on the node,
                        // so the (still empty) definition has to exist from the start. Without
                        // it the new type reads back as a plain UInteger subtype and the editor
                        // never offers the Fields tab needed to add the first bit.
                        Definition = request.IsOptionSet == true
                            ? new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.DataTypeDefinition
                            {
                                IsOptionSet = true,
                                Fields = new List<JsonNodeSet::Opc.Ua.JsonNodeSet.Model.DataTypeField>(),
                            }
                            : null,
                    },
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAReferenceType => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAReferenceType
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = nodeParentId,
                        References = references, IsAbstract = request.IsAbstract,
                        Symmetric = request.Symmetric == true ? true : null,
                        InverseName = MakeLocalizedText(request.InverseName),
                    },
                    _ => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObject
                    {
                        NodeId = nodeId, NodeClass = ncEnum, BrowseName = qualifiedBrowseName,
                        DisplayName = lt, ParentId = nodeParentId, TypeId = request.TypeDefinitionId,
                        ModellingRuleId = effectiveModellingRuleId, References = references,
                    },
                };

                if (!string.IsNullOrEmpty(request.Description))
                {
                    uaNode.Description = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                    {
                        T = new List<List<string>> { new() { "", request.Description } }
                    };
                }

                uaNode.ConformanceUnits = NormalizeConformanceUnits(request.Category);

                addressSpace.AddNode(uaNode, isTypeNode ? null : parentNodeId);

                // For instance children, attach to parent's Children collection for serialization
                if (!isTypeNode)
                {
                    var parent = addressSpace.Read(parentNodeId);
                    if (parent != null)
                    {
                        parent.Children ??= new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ChildList();
                        switch (uaNode)
                        {
                            case JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable v:
                                (parent.Children.Variables ??= new()).Add(v);
                                break;
                            case JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAMethod m:
                                (parent.Children.Methods ??= new()).Add(m);
                                break;
                            case JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObject o:
                                (parent.Children.Objects ??= new()).Add(o);
                                break;
                        }
                    }
                }

                // Build changeset for targeted persistence
                var changeset = new ModelChangeset();
                changeset.Nodes.Add(BuildNodeChange(uaNode, ChangeKind.Upsert));
                foreach (var r in references)
                {
                    changeset.References.Add(new ReferenceChange
                    {
                        Kind = ChangeKind.Upsert,
                        SourceNodeId = nodeId,
                        ReferenceTypeId = r.ReferenceTypeId!,
                        TargetNodeId = r.TargetId!,
                        IsForward = r.IsForward ?? true
                    });
                }

                // Auto-create Default Binary and Default XML encoding nodes for Structure subtypes
                if (uaNode is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType)
                {
                    if (IsSubtypeOf(addressSpace, parentNodeId, "i=22")) // Structure
                    {
                        foreach (var encName in new[] { "Default Binary", "Default XML" })
                        {
                            var encNumericId = await _addressSpace.GetNextNodeIdAsync(workspaceId, request.ModelUri);
                            var encNodeId = $"nsu={request.ModelUri};i={encNumericId}";
                            var encNode = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObject
                            {
                                NodeId = encNodeId,
                                NodeClass = JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObject,
                                BrowseName = encName,
                                DisplayName = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                                {
                                    T = new List<List<string>> { new() { "", encName } }
                                },
                                TypeId = "i=76", // DataTypeEncodingType
                                References = new List<JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Reference>
                                {
                                    new() { ReferenceTypeId = "i=38", TargetId = nodeId, IsForward = false }, // HasEncoding (inverse)
                                    new() { ReferenceTypeId = "i=40", TargetId = "i=76", IsForward = true },  // HasTypeDefinition
                                },
                            };
                            addressSpace.AddNode(encNode);

                            changeset.Nodes.Add(BuildNodeChange(encNode, ChangeKind.Upsert));
                            changeset.References.Add(new ReferenceChange
                            {
                                Kind = ChangeKind.Upsert,
                                SourceNodeId = encNodeId,
                                ReferenceTypeId = "i=38",
                                TargetNodeId = nodeId,
                                IsForward = false
                            });
                            changeset.References.Add(new ReferenceChange
                            {
                                Kind = ChangeKind.Upsert,
                                SourceNodeId = encNodeId,
                                ReferenceTypeId = "i=40",
                                TargetNodeId = "i=76",
                                IsForward = true
                            });
                        }
                    }
                }

                await PersistModelAsync(addressSpace, workspaceId, nodeId, changeset);
                _addressSpace.Invalidate(workspaceId);

                var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
                var restNode = UaNodeToRestNode(uaNode, addressSpace);
                restNode.ParentNodeId = parentNodeId;

                return CreatedAtAction(nameof(GetNode), new { nodeId }, restNode);
            }

            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error creating child node under {ParentNodeId}", parentNodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Update a node's attributes.
        /// </summary>
        [HttpPut("nodes/{nodeId}")]
        public async Task<ActionResult<Opc.Ua.RestfulApi.Node>> UpdateNode(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] UpdateNodeRequest request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var uaNode = addressSpace.Read(nodeId);
                if (uaNode == null)
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));

                if (request.BrowseName != null)
                {
                    // Every node needs a name. Blanking it produced a "nsu=<uri>;" BrowseName
                    // with an empty local part — a node that shows up nameless in every picker.
                    if (string.IsNullOrWhiteSpace(request.BrowseName))
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "browseName may not be empty."));

                    if (request.BrowseNameModelUri != null)
                    {
                        // Explicit namespace provided
                        uaNode.BrowseName = $"nsu={request.BrowseNameModelUri};{request.BrowseName}";
                    }
                    else
                    {
                        // Preserve existing namespace prefix
                        var existing = uaNode.BrowseName ?? "";
                        var semi = existing.IndexOf(';');
                        uaNode.BrowseName = semi >= 0
                            ? existing[..(semi + 1)] + request.BrowseName
                            : request.BrowseName;
                    }
                }

                if (request.DisplayName != null)
                {
                    // Clearing the field unsets DisplayName rather than storing an empty
                    // LocalizedText — that empty text used to survive the rebuild (it beats
                    // NodeSetConverter's "no DisplayName → fall back to BrowseName" guard)
                    // and left the node nameless in every picker. Unset, the node keeps the
                    // name already persisted, falling back to its BrowseName.
                    uaNode.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null
                        : new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                        {
                            T = new List<List<string>> { new() { "", request.DisplayName } }
                        };
                }

                if (request.Description != null)
                {
                    uaNode.Description = string.IsNullOrEmpty(request.Description) ? null
                        : new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                        {
                            T = new List<List<string>> { new() { "", request.Description } }
                        };
                }

                if (request.IsAbstract.HasValue)
                    uaNode.IsAbstract = request.IsAbstract.Value ? true : null;

                // Omitted means "leave alone"; an empty list clears the units.
                if (request.Category != null)
                    uaNode.ConformanceUnits = NormalizeConformanceUnits(request.Category);

                if (uaNode is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAReferenceType refType
                    && (request.Symmetric.HasValue || request.InverseName != null))
                {
                    // Validate against the state the node would end up in, so one call can
                    // turn Symmetric off and supply an InverseName (or the reverse).
                    var symmetric = request.Symmetric ?? refType.Symmetric == true;
                    var newInverseName = request.InverseName != null
                        ? MakeLocalizedText(request.InverseName)
                        : refType.InverseName;
                    var attrError = ValidateReferenceTypeAttributes(
                        symmetric, GetLocalizedText(newInverseName),
                        addressSpace.IsTypeOf(nodeId, HIERARCHICAL_REFERENCES));
                    if (attrError != null)
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), attrError));

                    refType.Symmetric = symmetric ? true : null;
                    refType.InverseName = newInverseName;
                }

                if (uaNode is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType dtNode
                    && request.IsOptionSet.HasValue
                    && request.IsOptionSet.Value != (dtNode.Definition?.IsOptionSet == true))
                {
                    // Flipping this on an existing DataType would reinterpret every field:
                    // enumeration values become bit positions or the other way round, and
                    // any instance value already written against it silently changes meaning.
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "isOptionSet can only be set when the DataType is created."));
                }

                // A design-tool-only node keeps its defining HasTypeDefinition as
                // read-only metadata. It is preserved across updates (the client
                // sends it read-only); DesignToolOnly itself is immutable and is not
                // part of UpdateNodeRequest. Other references/children stay blocked
                // by the AddReference / CreateChildNode guards.

                // Update TypeDefinition — serializer writes TypeId as HasTypeDefinition reference,
                // so only set the property; do NOT add to References (avoids duplicates).
                if (request.TypeDefinitionId != null)
                {
                    uaNode.TypeId = string.IsNullOrEmpty(request.TypeDefinitionId) ? null : request.TypeDefinitionId;
                    // Remove any existing HasTypeDefinition from References to avoid duplicates on save
                    uaNode.References?.RemoveAll(r => r.ReferenceTypeId == "i=40" && (r.IsForward ?? true));
                }

                // Update ModellingRule — serializer writes ModellingRuleId as HasModellingRule reference.
                if (request.ModellingRuleId != null)
                {
                    uaNode.ModellingRuleId = string.IsNullOrEmpty(request.ModellingRuleId) ? null : request.ModellingRuleId;
                    uaNode.References?.RemoveAll(r => r.ReferenceTypeId == "i=37" && (r.IsForward ?? true));
                }

                // Update the structural (parent→child) reference type. A user-created
                // child authors this as an inverse hierarchical reference to its parent
                // (see CreateChildNode); retype it in place and emit paired reference
                // changes so the DB reference rows are rewritten. Only concrete
                // hierarchical types are allowed, and the type hierarchy (HasSubtype)
                // is never touched.
                (string parentId, string oldType, string newType)? structuralRetype = null;
                if (!string.IsNullOrEmpty(request.ReferenceTypeId))
                {
                    var structural = GetStructuralParent(addressSpace, uaNode);
                    if (structural is { } sp
                        && sp.referenceTypeId != request.ReferenceTypeId
                        && sp.referenceTypeId != HAS_SUBTYPE
                        && addressSpace.IsTypeOf(request.ReferenceTypeId, HIERARCHICAL_REFERENCES))
                    {
                        var childRef = uaNode.References?.FirstOrDefault(r =>
                            r.TargetId == sp.parentNodeId
                            && (r.IsForward ?? true) == false
                            && r.ReferenceTypeId == sp.referenceTypeId);
                        if (childRef != null)
                        {
                            childRef.ReferenceTypeId = request.ReferenceTypeId;
                            structuralRetype = (sp.parentNodeId, sp.referenceTypeId, request.ReferenceTypeId);
                        }
                    }
                }

                // Update Variable-specific attributes
                if (uaNode is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable variable)
                {
                    if (request.DataType != null)
                        variable.DataType = string.IsNullOrEmpty(request.DataType) ? null : request.DataType;
                    if (request.ValueRank.HasValue)
                        variable.ValueRank = request.ValueRank.Value;
                    if (request.ArrayDimensions != null || request.ValueRank.HasValue)
                        variable.ArrayDimensions = SanitizeArrayDimensions(
                            variable.ValueRank, request.ArrayDimensions ?? variable.ArrayDimensions);
                    if (request.Value.HasValue)
                        variable.Value = JsonElementToVariant(request.Value.Value, variable.DataType, variable.ArrayDimensions, addressSpace);
                }
                else if (uaNode is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType variableType)
                {
                    if (request.DataType != null)
                        variableType.DataType = string.IsNullOrEmpty(request.DataType) ? null : request.DataType;
                    if (request.ValueRank.HasValue)
                        variableType.ValueRank = request.ValueRank.Value;
                    if (request.ArrayDimensions != null || request.ValueRank.HasValue)
                        variableType.ArrayDimensions = SanitizeArrayDimensions(
                            variableType.ValueRank, request.ArrayDimensions ?? variableType.ArrayDimensions);
                    if (request.Value.HasValue)
                        variableType.Value = JsonElementToVariant(request.Value.Value, variableType.DataType, variableType.ArrayDimensions, addressSpace);
                }

                var changeset = new ModelChangeset();
                changeset.Nodes.Add(BuildNodeChange(uaNode, ChangeKind.Upsert));
                if (structuralRetype is { } rt)
                {
                    changeset.References.Add(new ReferenceChange
                    {
                        Kind = ChangeKind.Delete,
                        SourceNodeId = nodeId,
                        ReferenceTypeId = rt.oldType,
                        TargetNodeId = rt.parentId,
                        IsForward = false,
                    });
                    changeset.References.Add(new ReferenceChange
                    {
                        Kind = ChangeKind.Upsert,
                        SourceNodeId = nodeId,
                        ReferenceTypeId = rt.newType,
                        TargetNodeId = rt.parentId,
                        IsForward = false,
                    });
                }
                await PersistModelAsync(addressSpace, workspaceId, nodeId, changeset);
                _addressSpace.Invalidate(workspaceId);

                var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
                return Ok(UaNodeToRestNode(uaNode, addressSpace));
            }

            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error updating node {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Delete a node.
        /// </summary>
        [HttpDelete("nodes/{nodeId}")]
        public async Task<IActionResult> DeleteNode(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var nodeToDelete = addressSpace.Read(nodeId);
                if (nodeToDelete == null)
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));

                // Remove from parent's Children collection (needed for serialization)
                if (nodeToDelete.ParentId != null)
                {
                    var parent = addressSpace.Read(nodeToDelete.ParentId);
                    if (parent?.Children != null)
                    {
                        parent.Children.Objects?.RemoveAll(n => n.NodeId == nodeId);
                        parent.Children.Variables?.RemoveAll(n => n.NodeId == nodeId);
                        parent.Children.Methods?.RemoveAll(n => n.NodeId == nodeId);
                    }
                }

                // Determine which model to persist: parent's model for child nodes, own
                // model for top-level nodes or nodes parented under a core folder (e.g.
                // Objects, i=85) — the core namespace itself is never persisted.
                var persistNodeId = nodeToDelete.ParentId != null
                    && ExtractNamespaceUri(nodeToDelete.ParentId) != OPC_UA_CORE_NS
                    ? nodeToDelete.ParentId
                    : nodeId;

                if (!addressSpace.RemoveNode(nodeId))
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));

                var changeset = new ModelChangeset();
                changeset.Nodes.Add(new NodeChange { Kind = ChangeKind.Delete, NodeId = nodeId });
                await PersistModelAsync(addressSpace, workspaceId, persistNodeId, changeset);
                _addressSpace.Invalidate(workspaceId);
                return NoContent();
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error deleting node {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Add a reference from a node.
        /// </summary>
        [HttpPost("nodes/{nodeId}/references")]
        public async Task<IActionResult> AddReference(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] CreateReferenceRequest request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                if (string.IsNullOrEmpty(request.ReferenceTypeId) || string.IsNullOrEmpty(request.TargetNodeId))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "referenceTypeId and targetNodeId are required."));

                var sourceNode = addressSpace.Read(nodeId);

                // A design-tool-only node is a standalone marker: no references may originate from it.
                if (sourceNode?.DesignToolOnly == true)
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"Node '{nodeId}' is design-tool-only; references are not allowed."));

                // A Property may not be the source of a forward hierarchical reference
                // (a subtype of HierarchicalReferences, i=33) — that would give it a child.
                if ((request.IsForward ?? true)
                    && IsPropertyNode(addressSpace, sourceNode)
                    && addressSpace.IsTypeOf(request.ReferenceTypeId, HIERARCHICAL_REFERENCES))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "Properties may not be the source of hierarchical references."));

                addressSpace.AddReference(nodeId, request.ReferenceTypeId, request.TargetNodeId, request.IsForward ?? true);

                var changeset = new ModelChangeset();
                changeset.References.Add(new ReferenceChange
                {
                    Kind = ChangeKind.Upsert,
                    SourceNodeId = nodeId,
                    ReferenceTypeId = request.ReferenceTypeId,
                    TargetNodeId = request.TargetNodeId,
                    IsForward = request.IsForward ?? true
                });
                await PersistModelAsync(addressSpace, workspaceId, nodeId, changeset);
                _addressSpace.Invalidate(workspaceId);

                return NoContent();
            }

            catch (KeyNotFoundException e)
            {
                return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), e.Message));
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error adding reference for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Delete a reference from a node.
        /// </summary>
        [HttpDelete("nodes/{nodeId}/references/{referenceTypeId}/{targetNodeId}")]
        public async Task<IActionResult> DeleteReference(
            string nodeId,
            string referenceTypeId,
            string targetNodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] bool isForward = true)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                if (!addressSpace.RemoveReference(nodeId, referenceTypeId, targetNodeId, isForward))
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), "Reference not found."));

                var changeset = new ModelChangeset();
                changeset.References.Add(new ReferenceChange
                {
                    Kind = ChangeKind.Delete,
                    SourceNodeId = nodeId,
                    ReferenceTypeId = referenceTypeId,
                    TargetNodeId = targetNodeId,
                    IsForward = isForward
                });
                await PersistModelAsync(addressSpace, workspaceId, nodeId, changeset);
                _addressSpace.Invalidate(workspaceId);
                return NoContent();
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error deleting reference for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        #endregion

        #region Types

        /// <summary>
        /// List subtypes of a given type node as a flat list with superTypeId.
        /// Traverses the HasSubtype hierarchy up to the specified depth.
        /// Category must be one of: object-types, variable-types, data-types, reference-types.
        /// </summary>
        [HttpGet("types/{category}/{nodeId}/subtypes")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> GetSubtypes(
            string category,
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] int depth = 3,
            [FromQuery] int start = 0,
            [FromQuery] int count = 1000,
            [FromQuery] bool includeSelf = false,
            [FromQuery] string? modelUri = null)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                if (CategoryToNodeClass(category) < 0)
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"Invalid category: '{category}'. Expected: object-types, variable-types, data-types, reference-types"));
                }

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var results = new List<Opc.Ua.RestfulApi.Node>();
                if (!string.IsNullOrEmpty(modelUri))
                {
                    // Namespace filter: return the full HasSubtype hierarchy pruned
                    // to branches that contain a type in `modelUri` (the type itself
                    // plus its supertype-chain ancestors as scaffolding). `depth`
                    // and `includeSelf` are ignored — the pruned tree is complete.
                    CollectNamespaceTypeTree(addressSpace, nodeId, modelUri, results);
                }
                else
                {
                    // Recursively collect subtypes as a flat list
                    if (includeSelf)
                    {
                        var rootNode = addressSpace.Read(nodeId);
                        if (rootNode == null)
                            return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));
                        var rootRest = UaNodeToRestNode(rootNode, addressSpace);
                        rootRest.HasNoChildren = ComputeHasNoChildren(addressSpace, nodeId);
                        results.Add(rootRest);
                    }
                    CollectSubtypes(addressSpace, nodeId, depth, results);
                }

                if (start < 0) start = 0;
                if (count <= 0) count = 1000;

                var total = results.Count;
                var paged = results.Skip(start).Take(count).ToList();

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = paged,
                    TotalCount = total
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting subtypes for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Instantiate a type under a parent node. Creates the instance with all mandatory
        /// children from the type hierarchy. Returns the created nodes.
        /// Category must be object-types or variable-types.
        /// </summary>
        [HttpPost("types/{category}/{nodeId}/instantiate")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> InstantiateType(
            string category,
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] InstantiateRequest request)
        {
            try
            {
                if (category != "object-types" && category != "variable-types")
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "Instantiate only applies to object-types and variable-types."));
                }

                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var typeNode = addressSpace.Read(nodeId);
                if (typeNode == null)
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Type '{nodeId}' not found."));
                }

                if (typeNode.NodeClass != JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObjectType
                    && typeNode.NodeClass != JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariableType)
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "Node must be an ObjectType or VariableType."));
                }

                if (string.IsNullOrWhiteSpace(request.ParentNodeId))
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "parentNodeId is required."));
                }
                if (string.IsNullOrWhiteSpace(request.ModelUri))
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "modelUri is required."));
                }

                var browseName = request.BrowseName ?? GetLocalizedText(typeNode.DisplayName) ?? "Instance";

                var createdUaNodes = addressSpace.Instantiate(
                    nodeId,
                    request.ParentNodeId!,
                    request.ModelUri!,
                    browseName,
                    request.DisplayName ?? browseName,
                    modelUri => _addressSpace.GetNextNodeIdAsync(workspaceId, modelUri).Result,
                    request.ReferenceTypeId ?? "i=47",
                    request.ModellingRuleId);

                var changeset = new ModelChangeset();
                foreach (var n in createdUaNodes)
                {
                    changeset.Nodes.Add(BuildNodeChange(n, ChangeKind.Upsert));
                    if (n.References != null)
                    {
                        foreach (var r in n.References)
                        {
                            changeset.References.Add(new ReferenceChange
                            {
                                Kind = ChangeKind.Upsert,
                                SourceNodeId = n.NodeId!,
                                ReferenceTypeId = r.ReferenceTypeId!,
                                TargetNodeId = r.TargetId!,
                                IsForward = r.IsForward ?? true
                            });
                        }
                    }
                }
                await PersistModelAsync(addressSpace, workspaceId, $"nsu={request.ModelUri};i=0", changeset);
                _addressSpace.Invalidate(workspaceId);

                var results = createdUaNodes.Select(n => UaNodeToRestNode(n, addressSpace)).ToList();

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = results,
                    TotalCount = results.Count
                });
            }

            catch (InvalidOperationException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error instantiating type {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Adds the mandatory descendants of a type or an instance declaration directly under
        /// the existing instance <paramref name="nodeId"/>, recursively. Used after
        /// CreateChildNode to populate the new instance, without creating a wrapper node.
        ///
        /// Pass <c>sourceNodeId</c> when the node was created from an instance declaration:
        /// expanding the TypeDefinition alone drops the children the owning type authored
        /// under that declaration, which is exactly what a subtype inherits when it overrides
        /// the declaration.
        /// </summary>
        [HttpPost("nodes/{nodeId}/instantiate-children")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> InstantiateChildren(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] InstantiateChildrenRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.TypeNodeId) && string.IsNullOrWhiteSpace(request.SourceNodeId))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "typeNodeId or sourceNodeId is required."));
                if (string.IsNullOrWhiteSpace(request.ModelUri))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "modelUri is required."));

                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                if (addressSpace.Read(nodeId) == null)
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));

                var createdUaNodes = !string.IsNullOrWhiteSpace(request.SourceNodeId)
                    ? addressSpace.ExpandMandatoryChildrenFromDeclaration(
                        nodeId,
                        request.SourceNodeId!,
                        request.ModelUri!,
                        modelUri => _addressSpace.GetNextNodeIdAsync(workspaceId, modelUri).Result)
                    : addressSpace.ExpandMandatoryChildren(
                        nodeId,
                        request.TypeNodeId!,
                        request.ModelUri!,
                        modelUri => _addressSpace.GetNextNodeIdAsync(workspaceId, modelUri).Result);

                var changeset = new ModelChangeset();
                foreach (var n in createdUaNodes)
                {
                    changeset.Nodes.Add(BuildNodeChange(n, ChangeKind.Upsert));
                    if (n.References != null)
                    {
                        foreach (var r in n.References)
                        {
                            changeset.References.Add(new ReferenceChange
                            {
                                Kind = ChangeKind.Upsert,
                                SourceNodeId = n.NodeId!,
                                ReferenceTypeId = r.ReferenceTypeId!,
                                TargetNodeId = r.TargetId!,
                                IsForward = r.IsForward ?? true
                            });
                        }
                    }
                }
                await PersistModelAsync(addressSpace, workspaceId, $"nsu={request.ModelUri};i=0", changeset);
                _addressSpace.Invalidate(workspaceId);

                var results = createdUaNodes.Select(n => UaNodeToRestNode(n, addressSpace)).ToList();

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = results,
                    TotalCount = results.Count
                });
            }

            catch (ArgumentException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error instantiating children of {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Add a HasInterface reference from an Object/ObjectType to the given
        /// InterfaceType and instantiate all of the interface's members (Mandatory +
        /// Optional) directly under the node, in the node's model. The created
        /// children are ordinary children; the HasInterface reference is purely a
        /// model-compatibility marker.
        /// </summary>
        [HttpPost("nodes/{nodeId}/add-interface")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> AddInterface(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] AddInterfaceRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.InterfaceTypeNodeId))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "interfaceTypeNodeId is required."));
                if (string.IsNullOrWhiteSpace(request.ModelUri))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), "modelUri is required."));

                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var node = addressSpace.Read(nodeId);
                if (node == null)
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));
                if (node.NodeClass != JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObject
                    && node.NodeClass != JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObjectType)
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        "Interfaces can only be added to an Object or ObjectType."));
                if (node.DesignToolOnly == true)
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"Node '{nodeId}' is design-tool-only; interfaces (references and children) are not allowed."));

                if (addressSpace.Read(request.InterfaceTypeNodeId!) == null)
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"InterfaceType '{request.InterfaceTypeNodeId}' not found."));
                // BaseInterfaceType = i=17602.
                if (!addressSpace.IsTypeOf(request.InterfaceTypeNodeId!, "i=17602"))
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"Node '{request.InterfaceTypeNodeId}' is not an InterfaceType."));

                // 1) HasInterface reference (i=17603), node -> interface type.
                addressSpace.AddReference(nodeId, "i=17603", request.InterfaceTypeNodeId!, true);

                // 2) Instantiate all interface members (Mandatory + Optional) under the node.
                var createdUaNodes = addressSpace.ExpandMandatoryChildren(
                    nodeId,
                    request.InterfaceTypeNodeId!,
                    request.ModelUri!,
                    modelUri => _addressSpace.GetNextNodeIdAsync(workspaceId, modelUri).Result,
                    includeOptionalTopLevel: true);

                var changeset = new ModelChangeset();
                changeset.References.Add(new ReferenceChange
                {
                    Kind = ChangeKind.Upsert,
                    SourceNodeId = nodeId,
                    ReferenceTypeId = "i=17603",
                    TargetNodeId = request.InterfaceTypeNodeId!,
                    IsForward = true
                });
                foreach (var n in createdUaNodes)
                {
                    changeset.Nodes.Add(BuildNodeChange(n, ChangeKind.Upsert));
                    if (n.References != null)
                    {
                        foreach (var r in n.References)
                        {
                            changeset.References.Add(new ReferenceChange
                            {
                                Kind = ChangeKind.Upsert,
                                SourceNodeId = n.NodeId!,
                                ReferenceTypeId = r.ReferenceTypeId!,
                                TargetNodeId = r.TargetId!,
                                IsForward = r.IsForward ?? true
                            });
                        }
                    }
                }
                await PersistModelAsync(addressSpace, workspaceId, $"nsu={request.ModelUri};i=0", changeset);
                _addressSpace.Invalidate(workspaceId);

                var results = createdUaNodes.Select(n => UaNodeToRestNode(n, addressSpace)).ToList();

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = results,
                    TotalCount = results.Count
                });
            }
            catch (ArgumentException e)
            {
                return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), e.Message));
            }
            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error adding interface to {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Get the DataType definition with fields.
        /// </summary>
        [HttpGet("types/data-types/{nodeId}/definition")]
        public async Task<ActionResult<DataTypeDefinitionResponse>> GetDataTypeDefinition(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] bool full = false)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var node = addressSpace.Read(nodeId);
                if (node is not JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType dt)
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"DataType '{nodeId}' not found."));
                }

                var response = BuildDefinitionResponse(addressSpace, dt, nodeId, full);
                return Ok(response);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting DataType definition for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Update the DataType definition (own fields only).
        /// </summary>
        [HttpPut("types/data-types/{nodeId}/definition")]
        public async Task<ActionResult<DataTypeDefinitionResponse>> UpdateDataTypeDefinition(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromBody] DataTypeDefinitionResponse request)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer, requireWrite: true);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var node = addressSpace.Read(nodeId);
                if (node is not JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType dt)
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"DataType '{nodeId}' not found."));
                }

                // Collect inherited field names for duplicate validation
                var inheritedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var superTypeIds = GetSuperTypeIds(addressSpace, nodeId);
                foreach (var ancestorId in superTypeIds)
                {
                    var ancestor = addressSpace.Read(ancestorId);
                    if (ancestor is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType ancestorDt
                        && ancestorDt.Definition?.Fields != null)
                    {
                        foreach (var f in ancestorDt.Definition.Fields)
                        {
                            if (f.Name != null) inheritedNames.Add(f.Name);
                        }
                    }
                }

                // Validate: reject fields with names that collide with inherited
                var ownFields = request.Fields?
                    .Where(f => f.IsInherited != true)
                    .ToList() ?? new List<DataTypeFieldDescription>();

                var duplicates = ownFields
                    .Where(f => inheritedNames.Contains(f.Name))
                    .Select(f => f.Name)
                    .ToList();

                if (duplicates.Count > 0)
                {
                    return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                        $"Field names conflict with inherited fields: {string.Join(", ", duplicates)}"));
                }

                if (dt.Definition?.IsOptionSet == true || dt.DataTypeForm == "OptionSet")
                {
                    // An OptionSet field's Value is a bit position in the underlying
                    // unsigned integer, not an arbitrary number: negatives are meaningless
                    // and 64 is past the widest one (UInt64).
                    var badBits = ownFields
                        .Where(f => f.Value is < 0 or > 63)
                        .Select(f => f.Name)
                        .ToList();

                    if (badBits.Count > 0)
                    {
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                            $"OptionSet bit positions must be between 0 and 63: {string.Join(", ", badBits)}"));
                    }
                }

                // Update the definition. `??=` matters for an OptionSet: the flag lives on
                // the definition created with the node, and replacing it would lose it.
                dt.Definition ??= new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.DataTypeDefinition();
                dt.Definition.Fields = ownFields.Select(f => new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.DataTypeField
                {
                    Name = f.Name,
                    Value = f.Value,
                    DataType = f.DataType,
                    ValueRank = f.ValueRank,
                    ArrayDimensions = f.ArrayDimensions,
                    MaxStringLength = f.MaxStringLength,
                    IsOptional = f.IsOptional,
                    AllowSubTypes = f.AllowSubTypes,
                    Description = f.Description?.Text != null
                        ? new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
                        {
                            T = new List<List<string>> { new() { "", f.Description.Text } }
                        }
                        : null,
                }).ToList();

                var changeset = new ModelChangeset();
                changeset.Nodes.Add(BuildNodeChange(dt, ChangeKind.Upsert));
                await PersistModelAsync(addressSpace, workspaceId, nodeId, changeset);
                _addressSpace.Invalidate(workspaceId);

                // Return full definition
                var response = BuildDefinitionResponse(addressSpace, dt, nodeId, full: true);
                return Ok(response);
            }

            catch (ModelReadOnlyException e)
            {
                return Forbidden(MakeError(Opc.Ua.StatusCodes.BadNotWritable, nameof(Opc.Ua.StatusCodes.BadNotWritable), e.Message));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error updating DataType definition for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Returns a JSON Schema (draft 2020-12) describing one element of the given
        /// DataType, suitable for driving a schema-aware JSON editor on the Value tab.
        /// The Variable's ValueRank is intentionally NOT applied here — the schema
        /// describes a single value; the editor wraps for arrays and lets the user
        /// navigate elements one at a time.
        /// </summary>
        [HttpGet("types/data-types/{nodeId}/json-schema")]
        public async Task<ActionResult<System.Text.Json.Nodes.JsonObject>> GetDataTypeJsonSchema(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                if (addressSpace.Read(nodeId) is not JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType)
                {
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"DataType '{nodeId}' not found."));
                }

                var resolver = new AddressSpaceDataTypeResolver(addressSpace);
                var schema = NodeSetEditor.Model.DataTypeJsonSchemaGenerator.Generate(nodeId, resolver);
                return Ok(schema);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error generating JSON schema for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        private sealed class AddressSpaceDataTypeResolver : NodeSetEditor.Model.IDataTypeResolver
        {
            private readonly JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace _addressSpace;

            public AddressSpaceDataTypeResolver(JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace)
            {
                _addressSpace = addressSpace;
            }

            public NodeSetEditor.Model.DataTypeSchemaInfo? Resolve(string nodeId)
            {
                var node = _addressSpace.Read(nodeId);
                if (node is not JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType dt) return null;

                string? superTypeId = null;
                var inverseRefs = _addressSpace.Browse(nodeId, HAS_SUBTYPE,
                    includeForward: false, includeInverse: true);
                if (inverseRefs.Count > 0) superTypeId = inverseRefs[0].TargetNodeId;

                return new NodeSetEditor.Model.DataTypeSchemaInfo
                {
                    NodeId = nodeId,
                    BrowseName = node.BrowseName,
                    SuperTypeId = superTypeId,
                    Description = GetLocalizedText(node.Description),
                    Definition = ConvertDefinition(dt.Definition, node.BrowseName),
                };
            }

            // The JsonNodeSet definition carries no Name; the DataType's BrowseName is the
            // normative source.
            private static NodeSetEditor.Model.DataTypeDefinitionEntry? ConvertDefinition(
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.DataTypeDefinition? def, string? browseName)
            {
                if (def == null) return null;
                return new NodeSetEditor.Model.DataTypeDefinitionEntry
                {
                    Name = browseName,
                    SymbolicName = def.SymbolicName,
                    IsUnion = def.IsUnion,
                    IsOptionSet = def.IsOptionSet,
                    Fields = def.Fields?.Select(f => new NodeSetEditor.Model.FieldDefinitionEntry
                    {
                        Name = f.Name,
                        SymbolicName = f.SymbolicName,
                        Value = f.Value,
                        DataType = f.DataType,
                        ValueRank = f.ValueRank,
                        ArrayDimensions = f.ArrayDimensions,
                        IsOptional = f.IsOptional,
                        AllowSubTypes = f.AllowSubTypes,
                        Description = ConvertDescription(f.Description),
                    }).ToList(),
                };
            }

            private static List<NodeSetEditor.Model.LocalizedTextEntry>? ConvertDescription(
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText? lt)
            {
                if (lt?.T == null) return null;
                var list = new List<NodeSetEditor.Model.LocalizedTextEntry>();
                foreach (var t in lt.T)
                {
                    if (t == null) continue;
                    list.Add(new NodeSetEditor.Model.LocalizedTextEntry
                    {
                        Locale = t.Count > 0 ? t[0] : null,
                        Value = t.Count > 1 ? t[1] : null,
                    });
                }
                return list;
            }
        }

        private DataTypeDefinitionResponse BuildDefinitionResponse(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType dt,
            string nodeId,
            bool full)
        {
            var fields = new List<DataTypeFieldDescription>();

            // Own field names for dedup
            var ownFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (dt.Definition?.Fields != null)
            {
                foreach (var f in dt.Definition.Fields)
                    if (f.Name != null) ownFieldNames.Add(f.Name);
            }

            // Inherited fields (if full=true)
            if (full)
            {
                var superTypeIds = GetSuperTypeIds(addressSpace, nodeId);
                foreach (var ancestorId in superTypeIds)
                {
                    var ancestor = addressSpace.Read(ancestorId);
                    if (ancestor is not JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType ancestorDt) continue;
                    if (ancestorDt.Definition?.Fields == null) continue;

                    int nextValue = 0;
                    foreach (var f in ancestorDt.Definition.Fields)
                    {
                        if (f.Name != null && ownFieldNames.Contains(f.Name)) continue;
                        int effectiveValue = f.Value ?? nextValue;
                        nextValue = effectiveValue + 1;

                        fields.Add(new DataTypeFieldDescription
                        {
                            Name = f.Name ?? "",
                            Value = effectiveValue,
                            DataType = f.DataType,
                            DataTypeName = ResolveBrowseName(addressSpace, f.DataType),
                            ValueRank = f.ValueRank,
                            ArrayDimensions = f.ArrayDimensions,
                            MaxStringLength = f.MaxStringLength,
                            IsOptional = f.IsOptional,
                            AllowSubTypes = f.AllowSubTypes,
                            Description = GetLocalizedText(f.Description) is string desc
                                ? new Opc.Ua.RestfulApi.LocalizedText { Text = desc } : null,
                            IsInherited = true,
                            SourceTypeNodeId = ancestorId,
                        });
                    }
                }
            }

            // Own fields — the value is whatever the user specified (null when unset); we do NOT
            // auto-number own enum fields, so the editor reflects exactly what was entered.
            if (dt.Definition?.Fields != null)
            {
                foreach (var f in dt.Definition.Fields)
                {
                    fields.Add(new DataTypeFieldDescription
                    {
                        Name = f.Name ?? "",
                        Value = f.Value,
                        DataType = f.DataType,
                        DataTypeName = ResolveBrowseName(addressSpace, f.DataType),
                        ValueRank = f.ValueRank,
                        ArrayDimensions = f.ArrayDimensions,
                        MaxStringLength = f.MaxStringLength,
                        IsOptional = f.IsOptional,
                        AllowSubTypes = f.AllowSubTypes,
                        Description = GetLocalizedText(f.Description) is string desc
                            ? new Opc.Ua.RestfulApi.LocalizedText { Text = desc } : null,
                        IsInherited = false,
                    });
                }
            }

            return new DataTypeDefinitionResponse
            {
                Form = dt.DataTypeForm,
                IsUnion = dt.Definition?.IsUnion,
                IsOptionSet = dt.Definition?.IsOptionSet,
                Fields = fields,
            };
        }

        /// <summary>
        /// Search type definitions across all categories with filtering and pagination.
        /// </summary>
        [HttpGet("query/types")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> QueryTypes(
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] string? filter,
            [FromQuery] string? namespaceUri,
            [FromQuery] string? nodeClass,
            [FromQuery] int start = 0,
            [FromQuery] int count = 25)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                var filterTrimmed = filter?.Trim() ?? "";
                var nsUriFilter = namespaceUri?.Trim();
                int? nodeClassFilter = nodeClass != null ? NodeClassFromString(nodeClass) : null;

                // ObjectType (8), VariableType (16), ReferenceType (32), DataType (64) — types.
                // Object (1) and Variable (2) are instance NodeClasses. We always include
                // top-level instances — those with no ParentId — because they carry no
                // hierarchical references and are therefore invisible in the address-space
                // tree; this list is the only place they can be found. Nested instances
                // (those with a ParentId) are excluded so the list does not expand to every
                // child in the address space.
                const int typeMask = 8 | 16 | 32 | 64;
                const int instanceMask = 1 | 2;
                bool instanceQuery = nodeClassFilter is 1 or 2;
                var allMatches = new List<Opc.Ua.RestfulApi.Node>();

                foreach (var uaNode in addressSpace.Nodes)
                {
                    var nc = (int)(uaNode.NodeClass ?? 0);
                    if (instanceQuery)
                    {
                        if ((nc & instanceMask) == 0) continue;
                        if (nc != nodeClassFilter!.Value) continue;
                        // Top-level instances only.
                        if (!string.IsNullOrEmpty(uaNode.ParentId)) continue;
                    }
                    else
                    {
                        // Default ("All Types") and type-filtered views: accept type
                        // NodeClasses, plus top-level instances (Object/Variable with no
                        // ParentId). A specific nodeClass filter still narrows to that class.
                        bool isType = (nc & typeMask) != 0;
                        bool isTopLevelInstance =
                            (nc & instanceMask) != 0 && string.IsNullOrEmpty(uaNode.ParentId);
                        if (!isType && !isTopLevelInstance) continue;
                        if (nodeClassFilter.HasValue && nc != nodeClassFilter.Value) continue;
                    }

                    var modelUri = ExtractNamespaceUri(uaNode.NodeId);

                    if (!string.IsNullOrEmpty(nsUriFilter) &&
                        !string.Equals(modelUri, nsUriFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var restNode = UaNodeToRestNode(uaNode, addressSpace);

                    if (!string.IsNullOrEmpty(filterTrimmed) &&
                        !(restNode.DisplayName?.Text?.Contains(filterTrimmed, StringComparison.OrdinalIgnoreCase) ?? false) &&
                        !(restNode.BrowseName?.Contains(filterTrimmed, StringComparison.OrdinalIgnoreCase) ?? false))
                        continue;

                    // Include supertype chain for tree navigation
                    if (uaNode.NodeId != null)
                    {
                        restNode.SuperTypeIds = GetSuperTypeIds(addressSpace, uaNode.NodeId);
                    }

                    allMatches.Add(restNode);
                }

                // Sort by display name
                allMatches.Sort((a, b) => string.Compare(
                    a.DisplayName?.Text, b.DisplayName?.Text,
                    StringComparison.OrdinalIgnoreCase));

                if (start < 0) start = 0;
                if (count <= 0) count = 25;

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = allMatches.Skip(start).Take(count).ToList(),
                    TotalCount = allMatches.Count
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error querying types");
                return InternalError(e);
            }
        }

        private static int? NodeClassFromString(string nodeClass)
        {
            return nodeClass.ToLowerInvariant() switch
            {
                "object" => 1,
                "variable" => 2,
                "objecttype" => 8,
                "variabletype" => 16,
                "datatype" => 64,
                "referencetype" => 32,
                _ => null
            };
        }

        private const string OPC_UA_CORE_NS = "http://opcfoundation.org/UA/";

        private static string ExtractNamespaceUri(string? nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return OPC_UA_CORE_NS;
            if (nodeId.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase))
            {
                var semiIdx = nodeId.IndexOf(';');
                if (semiIdx > 4) return nodeId[4..semiIdx];
            }
            // NodeIds without nsu= prefix (e.g. i=58) are in namespace 0 (OPC UA core)
            return OPC_UA_CORE_NS;
        }

        private static List<string> GetSuperTypeIds(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace, string nodeId)
        {
            var chain = new List<string>();
            var current = nodeId;
            var visited = new HashSet<string>();
            while (!string.IsNullOrEmpty(current) && visited.Add(current))
            {
                var inverseRefs = addressSpace.Browse(current, HAS_SUBTYPE, includeForward: false, includeInverse: true);
                if (inverseRefs.Count == 0) break;
                current = inverseRefs[0].TargetNodeId;
                chain.Insert(0, current);
            }
            return chain;
        }

        /// <summary>
        /// Get child nodes of a given node as a flat list with parentNodeId.
        /// Traverses hierarchical references up to the specified depth.
        /// </summary>
        [HttpGet("nodes/{nodeId}/children")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> GetChildren(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] string? referenceTypeId,
            [FromQuery] bool full = false,
            [FromQuery] int depth = 1,
            [FromQuery] int start = 0,
            [FromQuery] int count = 1000,
            [FromQuery] bool includeSubtypes = false,
            [FromQuery] string? modelUri = null)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                // Namespace filter: return the instance subtree under `nodeId` pruned
                // to branches containing a node in `modelUri` (as a flat list with
                // ParentNodeId set). `depth`/`full` are ignored — the tree is complete.
                if (!string.IsNullOrEmpty(modelUri))
                {
                    var filtered = new List<Opc.Ua.RestfulApi.Node>();
                    CollectNamespaceInstanceTree(addressSpace, nodeId, modelUri, filtered);
                    if (start < 0) start = 0;
                    if (count <= 0) count = 1000;
                    return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                    {
                        Results = filtered.Skip(start).Take(count).ToList(),
                        TotalCount = filtered.Count
                    });
                }

                // Default to HierarchicalReferences (i=33)
                var refType = referenceTypeId ?? "i=33";

                // Collect own children
                var results = new List<Opc.Ua.RestfulApi.Node>();
                CollectChildren(addressSpace, nodeId, refType, depth, results, includeSubtypes);

                // Collect inherited children from supertype chain (most derived wins)
                if (full)
                {
                    var ownBrowseNames = new HashSet<string>(
                        results.Select(n => n.BrowseName),
                        StringComparer.OrdinalIgnoreCase);

                    // GetSuperTypeIds returns the chain most-base first; walk it in reverse so
                    // the nearest ancestor that declares a BrowseName wins. Base-first would
                    // let a root type shadow a derived type's re-declaration — e.g. PumpType
                    // re-declares DeviceType's Identification as Mandatory with a different
                    // TypeDefinition, and a subtype of PumpType must inherit PumpType's version.
                    var superTypeIds = GetSuperTypeIds(addressSpace, nodeId);
                    superTypeIds.Reverse();
                    var seenBrowseNames = new HashSet<string>(ownBrowseNames, StringComparer.OrdinalIgnoreCase);
                    var ancestorBrowseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var ancestorId in superTypeIds)
                    {
                        var inherited = new List<Opc.Ua.RestfulApi.Node>();
                        CollectChildren(addressSpace, ancestorId, refType, depth, inherited, includeSubtypes);

                        foreach (var child in inherited)
                        {
                            ancestorBrowseNames.Add(child.BrowseName);

                            if (seenBrowseNames.Contains(child.BrowseName))
                                continue;

                            child.IsInherited = true;
                            child.SourceTypeNodeId = ancestorId;
                            results.Add(child);
                            seenBrowseNames.Add(child.BrowseName);
                        }
                    }

                    // Mark own children that override an ancestor's child
                    foreach (var own in results)
                    {
                        if (own.IsInherited != true && ancestorBrowseNames.Contains(own.BrowseName))
                            own.IsOverride = true;
                    }
                }

                if (start < 0) start = 0;
                if (count <= 0) count = 1000;

                var total = results.Count;
                var paged = results.Skip(start).Take(count).ToList();

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = paged,
                    TotalCount = total
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting children for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        /// <summary>
        /// Get the instance declarations that apply to a node — the children its
        /// TypeDefinition declares, merged with the children authored directly on the
        /// matching declaration in the owning type and its supertypes (most specific wins).
        ///
        /// This is what the Instantiate Children dialog offers. Browsing the TypeDefinition
        /// alone is not enough: a type can author extra children under one of its own
        /// instance declarations, and when a subtype overrides that declaration those
        /// grandchildren stay behind on the supertype's copy.
        /// </summary>
        [HttpGet("nodes/{nodeId}/instance-declarations")]
        public async Task<ActionResult<PaginatedResponse<Opc.Ua.RestfulApi.Node>>> GetNodeInstanceDeclarations(
            string nodeId,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer,
            [FromQuery] int start = 0,
            [FromQuery] int count = 1000)
        {
            try
            {
                var (user, workspace, error) = await ResolveServer(opcUaServer);
                if (error != null) return error;

                var workspaceId = workspace!.Id!.Value;
                var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);

                if (addressSpace.Read(nodeId) == null)
                    return NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Node '{nodeId}' not found."));

                var results = new List<Opc.Ua.RestfulApi.Node>();
                foreach (var decl in addressSpace.GetEffectiveInstanceDeclarations(nodeId))
                {
                    if (decl.SourceNode == null) continue;

                    var rest = UaNodeToRestNode(decl.SourceNode, addressSpace);
                    rest.ParentNodeId = decl.SourceTypeNodeId;
                    rest.ReferenceTypeId = decl.ReferenceTypeId;
                    if (decl.ReferenceTypeId != null)
                        rest.ReferenceType = addressSpace.Read(decl.ReferenceTypeId)?.BrowseName ?? decl.ReferenceTypeId;
                    // Where the declaration was authored: the node itself, one of its
                    // supertypes' copies, or a TypeDefinition further up.
                    rest.SourceTypeNodeId = decl.SourceTypeNodeId;
                    rest.IsInherited = decl.SourceTypeNodeId != nodeId;
                    results.Add(rest);
                }

                if (start < 0) start = 0;
                if (count <= 0) count = 1000;

                return Ok(new PaginatedResponse<Opc.Ua.RestfulApi.Node>
                {
                    Results = results.Skip(start).Take(count).ToList(),
                    TotalCount = results.Count
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting instance declarations for {NodeId}", nodeId);
                return InternalError(e);
            }
        }

        private static int CategoryToNodeClass(string category)
        {
            return category.ToLowerInvariant() switch
            {
                "object-types" => 8,
                "variable-types" => 16,
                "data-types" => 64,
                "reference-types" => 32,
                _ => -1
            };
        }

        private const string HAS_SUBTYPE = "i=45";
        private const string HAS_COMPONENT = "i=47";
        private const string PROPERTY_TYPE = "i=68";              // PropertyType
        private const string HIERARCHICAL_REFERENCES = "i=33";    // HierarchicalReferences
        private const string UINTEGER = "i=28";                   // UInteger — the base of every OptionSet
        private const string NAMESPACE_METADATA_TYPE = "i=11616"; // NamespaceMetadataType
        private const string NAMESPACES_FOLDER = "i=11715";       // Server.Namespaces folder (ns=0)

        /// <summary>
        /// A node is a Property when it is a Variable whose TypeDefinition is
        /// PropertyType (i=68) or a subtype. Properties may not be the source of
        /// hierarchical references, so they can have no children.
        /// </summary>
        private static bool IsPropertyNode(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace? addressSpace,
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UANode? node)
        {
            return node?.NodeClass == JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable
                && !string.IsNullOrEmpty(node.TypeId)
                && addressSpace?.IsTypeOf(node.TypeId, PROPERTY_TYPE) == true;
        }

        private static string StripNamespace(string? value)
        {
            if (value == null) return "";
            var semi = value.IndexOf(';');
            return semi >= 0 ? value[(semi + 1)..] : value;
        }

        /// <summary>
        /// True when the node has no hierarchical children other than its subtypes —
        /// i.e. GetChildren(includeSubtypes: false) would return nothing. Null (rather
        /// than false) when it does, so the flag serializes only when it says "leaf",
        /// matching <see cref="Opc.Ua.RestfulApi.Node.HasNoSubtypes"/>.
        ///
        /// Type rows carry this so a client that lists a type's instance declarations
        /// alongside its subtypes knows whether the row is expandable without fetching.
        /// </summary>
        private static bool? ComputeHasNoChildren(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string nodeId)
        {
            var refs = addressSpace.BrowseWithSubtypes(
                nodeId, HIERARCHICAL_REFERENCES, includeForward: true, includeInverse: false);
            return refs.All(r => r.ReferenceTypeId == HAS_SUBTYPE) ? true : null;
        }

        /// <summary>
        /// Recursively collects subtypes into a flat list with superTypeId set.
        /// </summary>
        private static void CollectSubtypes(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string parentNodeId,
            int remainingDepth,
            List<Opc.Ua.RestfulApi.Node> results)
        {
            if (remainingDepth <= 0) return;

            var refs = addressSpace.Browse(parentNodeId, HAS_SUBTYPE, includeForward: true, includeInverse: false);

            var children = new List<(string targetId, Opc.Ua.RestfulApi.Node node)>();
            foreach (var r in refs)
            {
                var uaNode = addressSpace.Read(r.TargetNodeId);
                if (uaNode == null) continue;

                var restNode = UaNodeToRestNode(uaNode, addressSpace);
                restNode.SuperTypeId = parentNodeId;

                // Instance declarations are never prefetched (this walk follows
                // HasSubtype only), so every row — not just the deepest — needs the
                // flag for a client that shows them.
                restNode.HasNoChildren = ComputeHasNoChildren(addressSpace, r.TargetNodeId);

                // Check if this node has subtypes (for leaf detection)
                if (remainingDepth == 1)
                {
                    var childRefs = addressSpace.Browse(r.TargetNodeId, HAS_SUBTYPE, includeForward: true, includeInverse: false);
                    restNode.HasNoSubtypes = childRefs.Count == 0 ? true : null;
                }

                children.Add((r.TargetNodeId, restNode));
            }

            children.Sort((a, b) => string.Compare(
                a.node.DisplayName?.Text, b.node.DisplayName?.Text,
                StringComparison.OrdinalIgnoreCase));

            foreach (var (targetId, node) in children)
            {
                results.Add(node);
                CollectSubtypes(addressSpace, targetId, remainingDepth - 1, results);
            }
        }

        /// <summary>
        /// True when <paramref name="nodeId"/> belongs to <paramref name="modelUri"/>.
        /// Mirrors the prefix logic in <c>AddressSpace.GetNodeSet</c>: namespaced
        /// nodes carry an <c>nsu={uri};</c> prefix, while core-namespace nodes are
        /// stored unprefixed (e.g. <c>i=85</c>).
        /// </summary>
        private static bool IsInNamespace(string nodeId, string modelUri)
        {
            if (nodeId.StartsWith($"nsu={modelUri};", StringComparison.Ordinal))
                return true;
            return string.Equals(modelUri, OPC_UA_CORE_NS, StringComparison.OrdinalIgnoreCase)
                && !nodeId.StartsWith("nsu=", StringComparison.Ordinal);
        }

        /// <summary>The immediate supertype of a type node (inverse HasSubtype), or null.</summary>
        private static string? GetImmediateSupertype(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace, string nodeId)
        {
            var inv = addressSpace.Browse(nodeId, HAS_SUBTYPE, includeForward: false, includeInverse: true);
            return inv.Count > 0 ? inv[0].TargetNodeId : null;
        }

        /// <summary>
        /// Builds the HasSubtype hierarchy under <paramref name="rootNodeId"/> pruned
        /// to branches that contain a type in <paramref name="modelUri"/>. Each match
        /// and every ancestor on its supertype chain up to (and including) the root is
        /// emitted once, with <c>SuperTypeId</c> set so the client's buildTree can
        /// reconstruct the tree. Pruned leaves get <c>HasNoSubtypes</c> so the client
        /// treats them as terminal. Touches only namespace nodes + their ancestor
        /// chains — not the whole type universe.
        /// </summary>
        private static void CollectNamespaceTypeTree(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string rootNodeId, string modelUri,
            List<Opc.Ua.RestfulApi.Node> results)
        {
            var byId = new Dictionary<string, Opc.Ua.RestfulApi.Node>(StringComparer.Ordinal);

            foreach (var node in addressSpace.Nodes)
            {
                var id = node.NodeId;
                if (string.IsNullOrEmpty(id) || id == rootNodeId) continue;
                if (!IsInNamespace(id, modelUri)) continue;
                // Restrict to this category's hierarchy (e.g. subtypes of BaseObjectType).
                if (!addressSpace.IsTypeOf(id, rootNodeId)) continue;

                // Walk up to the root, materializing the match + its scaffolding.
                var current = id;
                while (!string.IsNullOrEmpty(current) && !byId.ContainsKey(current))
                {
                    var ua = addressSpace.Read(current);
                    if (ua == null) break;
                    var super = GetImmediateSupertype(addressSpace, current);
                    var rest = UaNodeToRestNode(ua, addressSpace);
                    rest.SuperTypeId = super;
                    rest.HasNoChildren = ComputeHasNoChildren(addressSpace, current);
                    byId[current] = rest;
                    if (current == rootNodeId) break;
                    current = super;
                }
            }

            MarkPrunedLeaves(byId, n => n.SuperTypeId, (n, leaf) => n.HasNoSubtypes = leaf);
            results.AddRange(byId.Values);
        }

        /// <summary>
        /// Builds the instance hierarchy under <paramref name="rootNodeId"/> (the
        /// Objects folder, i=85) pruned to branches that contain a node in
        /// <paramref name="modelUri"/>. Each match plus its structural-parent chain
        /// up to the root is emitted with <c>ParentNodeId</c> set; nodes whose chain
        /// never reaches the root are dropped. Pruned leaves get <c>HasNoChildren</c>.
        /// </summary>
        private static void CollectNamespaceInstanceTree(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string rootNodeId, string modelUri,
            List<Opc.Ua.RestfulApi.Node> results)
        {
            var byId = new Dictionary<string, Opc.Ua.RestfulApi.Node>(StringComparer.Ordinal);

            foreach (var node in addressSpace.Nodes)
            {
                var id = node.NodeId;
                if (string.IsNullOrEmpty(id) || id == rootNodeId) continue;
                if (!IsInNamespace(id, modelUri)) continue;
                // Type nodes never live under the Objects folder — skip them.
                if (node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObjectType
                    or JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType
                    or JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType
                    or JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAReferenceType) continue;
                if (byId.ContainsKey(id)) continue;

                // Walk the structural-parent chain up to the root, buffering the
                // path; commit it only if it actually reaches the root.
                var buffer = new List<(string id, string parent)>();
                var current = id;
                var reached = false;
                var localVisited = new HashSet<string>(StringComparer.Ordinal);
                while (!string.IsNullOrEmpty(current))
                {
                    if (current == rootNodeId || byId.ContainsKey(current)) { reached = true; break; }
                    if (!localVisited.Add(current)) break; // cycle guard
                    var ua = addressSpace.Read(current);
                    if (ua == null) break;
                    var sp = GetStructuralParent(addressSpace, ua);
                    if (sp == null) break;
                    buffer.Add((current, sp.Value.parentNodeId));
                    current = sp.Value.parentNodeId;
                }

                if (!reached) continue;
                foreach (var (bid, bparent) in buffer)
                {
                    if (byId.ContainsKey(bid)) continue;
                    var ua = addressSpace.Read(bid);
                    if (ua == null) continue;
                    var rest = UaNodeToRestNode(ua, addressSpace);
                    rest.ParentNodeId = bparent;
                    byId[bid] = rest;
                }
            }

            MarkPrunedLeaves(byId, n => n.ParentNodeId, (n, leaf) => n.HasNoChildren = leaf);
            results.AddRange(byId.Values);
        }

        /// <summary>
        /// Flags nodes in <paramref name="byId"/> that have no child within the set
        /// (no other node points to them via <paramref name="parentSelector"/>).
        /// </summary>
        private static void MarkPrunedLeaves(
            Dictionary<string, Opc.Ua.RestfulApi.Node> byId,
            Func<Opc.Ua.RestfulApi.Node, string?> parentSelector,
            Action<Opc.Ua.RestfulApi.Node, bool> setLeaf)
        {
            var hasChild = new HashSet<string>(
                byId.Values.Select(parentSelector).Where(p => p != null).Select(p => p!),
                StringComparer.Ordinal);
            foreach (var n in byId.Values)
                if (!hasChild.Contains(n.NodeId))
                    setLeaf(n, true);
        }

        /// <summary>
        /// Identifies the structural parent reference of a node — the one
        /// that fixes its place in the hierarchy. For type nodes that's the
        /// inverse HasSubtype to the supertype; for instance nodes it's the
        /// inverse hierarchical reference to ParentId, with a fallback to
        /// whichever inverse hierarchical reference exists when ParentId is
        /// not recorded (top-level instances under a folder typically have
        /// a single inverse Organizes). Reads through the address-space
        /// index so the result is independent of which endpoint the
        /// reference was authored on. Returns null for genuinely orphan
        /// nodes (Root, root type with no supertype).
        /// </summary>
        private static (string parentNodeId, string referenceTypeId)? GetStructuralParent(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UANode node)
        {
            if (string.IsNullOrEmpty(node.NodeId)) return null;

            bool isType = node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObjectType
                or JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType
                or JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType
                or JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAReferenceType;
            if (isType)
            {
                // Subtypes: structural parent is the supertype via inverse
                // HasSubtype.
                var invSubtype = addressSpace.Browse(node.NodeId, HAS_SUBTYPE,
                    includeForward: false, includeInverse: true);
                if (invSubtype.Count > 0)
                    return (invSubtype[0].TargetNodeId, HAS_SUBTYPE);

                // Top-level types (BaseObjectType, BaseDataType, …) have no
                // supertype — they sit under the matching TypesFolder via
                // Organizes. Use the first inverse hierarchical ref so the
                // picker tree can browse from the folder into them.
                var invTypeHier = addressSpace.BrowseWithSubtypes(node.NodeId, "i=33",
                    includeForward: false, includeInverse: true);
                if (invTypeHier.Count == 0) return null;
                var firstHier = invTypeHier[0];
                return (firstHier.TargetNodeId, firstHier.ReferenceTypeId);
            }

            // Instance: prefer the inverse parent reference authored on
            // the node's own References list — when both HasComponent and
            // an extra HasNotifier point at the same ParentId, the index
            // contains both entries (in load order), but only the
            // canonical one will be present on the child's own References.
            // CreateChildNode authors the structural ref on the child;
            // AddReference(parent, …) authors on the parent and only
            // reaches the child via the index.
            var parentId = node.ParentId;
            if (parentId != null && node.References != null)
            {
                foreach (var r in node.References)
                {
                    if (r.TargetId == parentId
                        && (r.IsForward ?? true) == false
                        && r.ReferenceTypeId != null)
                    {
                        return (parentId, r.ReferenceTypeId);
                    }
                }
            }

            // Fallback to the index: covers nodes whose structural parent
            // ref was authored on the parent side (standard NodeSets — e.g.
            // Server under Objects, where Objects forward-authors the
            // Organizes). For top-level instances loaded without ParentId
            // the first inverse hierarchical ref in the index is the
            // folder's Organizes — the only one a typical model carries.
            var invHier = addressSpace.BrowseWithSubtypes(node.NodeId, "i=33",
                includeForward: false, includeInverse: true);
            if (invHier.Count == 0) return null;

            if (parentId != null)
            {
                var match = invHier.FirstOrDefault(r => r.TargetNodeId == parentId);
                if (match != null) return (parentId, match.ReferenceTypeId);
            }

            var first = invHier[0];
            return (first.TargetNodeId, first.ReferenceTypeId);
        }

        /// <summary>
        /// Recursively collects child nodes into a flat list with parentNodeId set.
        /// Uses BrowseWithSubtypes for reference type inheritance. By default
        /// HasSubtype refs are skipped (existing callers want instance
        /// declarations only); the NodePicker tree opts into includeSubtypes
        /// so users can navigate the type hierarchy past pure-type leaves.
        /// </summary>
        private static void CollectChildren(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string parentNodeId,
            string referenceTypeId,
            int remainingDepth,
            List<Opc.Ua.RestfulApi.Node> results,
            bool includeSubtypes = false)
        {
            if (remainingDepth <= 0) return;

            var refs = addressSpace.BrowseWithSubtypes(parentNodeId, referenceTypeId, includeForward: true, includeInverse: false);

            var children = new List<(string targetId, Opc.Ua.RestfulApi.Node node)>();
            foreach (var r in refs)
            {
                // Skip HasSubtype unless the caller explicitly opted in.
                if (!includeSubtypes && r.ReferenceTypeId == HAS_SUBTYPE)
                    continue;

                var uaNode = addressSpace.Read(r.TargetNodeId);
                if (uaNode == null) continue;

                // Only emit the child via its canonical structural parent
                // reference. A target may have multiple hierarchical refs
                // from the same source (e.g. HasComponent + HasNotifier);
                // the children list should still show one row per child,
                // matching the Node row's ParentNodeId+ReferenceTypeId
                // (or SuperTypeId+HasSubtype for type nodes). Other
                // hierarchical refs surface in the references tab.
                var structParent = GetStructuralParent(addressSpace, uaNode);
                if (structParent == null
                    || structParent.Value.parentNodeId != parentNodeId
                    || structParent.Value.referenceTypeId != r.ReferenceTypeId)
                {
                    continue;
                }

                var restNode = UaNodeToRestNode(uaNode, addressSpace);
                restNode.ParentNodeId = parentNodeId;

                // Resolve reference type name
                var refTypeNode = addressSpace.Read(r.ReferenceTypeId);
                restNode.ReferenceType = refTypeNode?.BrowseName ?? r.ReferenceTypeId;
                restNode.ReferenceTypeId = r.ReferenceTypeId;

                if (remainingDepth == 1)
                {
                    var childRefs = addressSpace.BrowseWithSubtypes(r.TargetNodeId, referenceTypeId, includeForward: true, includeInverse: false);
                    // hasNoChildren must reflect the same filter the caller
                    // will see on its next /children call — otherwise the UI
                    // hides the expand chevron on a node that would actually
                    // return rows when expanded.
                    restNode.HasNoChildren = (includeSubtypes
                        ? !childRefs.Any()
                        : childRefs.All(cr => cr.ReferenceTypeId == HAS_SUBTYPE)) ? true : null;
                }

                children.Add((r.TargetNodeId, restNode));
            }

            children.Sort((a, b) => string.Compare(
                a.node.DisplayName?.Text, b.node.DisplayName?.Text,
                StringComparison.OrdinalIgnoreCase));

            foreach (var (targetId, node) in children)
            {
                results.Add(node);
                CollectChildren(addressSpace, targetId, referenceTypeId, remainingDepth - 1, results, includeSubtypes);
            }
        }

        private static string? GetLocalizedText(JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText? lt)
        {
            return lt?.T?.FirstOrDefault()?.ElementAtOrDefault(1);
        }

        /// <summary>
        /// Wraps a plain string as an invariant-locale LocalizedText. Blank means "unset",
        /// so it yields null rather than an empty text (see UpdateNode's DisplayName).
        /// </summary>
        private static JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText? MakeLocalizedText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText
            {
                T = new List<List<string>> { new() { "", text } }
            };
        }

        /// <summary>
        /// Checks a ReferenceType's Symmetric/InverseName pair against Part 3, 5.3.3.
        /// A symmetric type browses the same in both directions and so has no separate
        /// inverse name. A hierarchical one is directional by definition: it is never
        /// symmetric, and the inverse direction has to be nameable (every core subtype
        /// of HierarchicalReferences carries one, starting with i=33 itself).
        /// Returns the reason the pair is invalid, or null when it is fine.
        /// </summary>
        private static string? ValidateReferenceTypeAttributes(
            bool symmetric, string? inverseName, bool isHierarchical)
        {
            if (symmetric)
            {
                if (isHierarchical)
                    return "A subtype of HierarchicalReferences may not be symmetric.";
                if (!string.IsNullOrWhiteSpace(inverseName))
                    return "inverseName may not be set on a symmetric ReferenceType.";
            }
            else if (isHierarchical && string.IsNullOrWhiteSpace(inverseName))
            {
                return "inverseName is required for a subtype of HierarchicalReferences.";
            }
            return null;
        }

        /// <summary>
        /// Serializes a LocalizedText into the per-locale array shape stored under a node's
        /// Attributes JSON (matching NodeSetConverter's LocalizedTextEntry { Locale, Value }).
        /// The XML regeneration path reads DisplayName/Description from Attributes, not from the
        /// dedicated DB columns, so edits must be mirrored here or they vanish on the next rebuild.
        /// </summary>
        private static System.Text.Json.Nodes.JsonArray? LocalizedTextToAttr(
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.LocalizedText? lt)
        {
            if (lt?.T == null || lt.T.Count == 0) return null;
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var entry in lt.T)
            {
                arr.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["Locale"] = entry.ElementAtOrDefault(0) ?? "",
                    ["Value"] = entry.ElementAtOrDefault(1),
                });
            }
            return arr;
        }

        /// <summary>
        /// Ensure the model identified by <paramref name="modelUri"/> has a NamespaceMetadata object,
        /// creating it through the SAME instantiation engine used when a user creates an instance of a
        /// type (<see cref="JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace.Instantiate"/>). Because the
        /// engine walks NamespaceMetadataType's instance declarations, the object gets ALL of its
        /// mandatory children (NamespaceUri, NamespaceVersion, NamespacePublicationDate,
        /// IsNamespaceSubset, StaticNodeIdTypes, StaticNumericNodeIdRange, StaticStringNodeIdPattern) —
        /// not a hand-maintained subset that drifts from the spec. No-op when an object already exists
        /// (e.g. an import whose NodeSet already carried one). The well-known value properties are
        /// seeded here; <see cref="NodeSetEditor.Model.NodeSetConverter.SyncNamespaceMetadataAsync"/>
        /// keeps them in sync afterwards (version change / clone).
        /// </summary>
        private async Task EnsureNamespaceMetadataObjectAsync(Guid workspaceId, string? modelUri)
        {
            if (string.IsNullOrEmpty(modelUri)) return;

            var addressSpace = await _addressSpace.GetAddressSpaceAsync(workspaceId);
            var nsPrefix = $"nsu={modelUri};";

            // Already present? (a NamespaceMetadataType object living in this model's namespace)
            var exists = addressSpace.Nodes.Any(n =>
                n.TypeId == NAMESPACE_METADATA_TYPE
                && n.NodeId != null && n.NodeId.StartsWith(nsPrefix, StringComparison.Ordinal));
            if (exists) return;

            // Look up the model's version / publication date to seed the value properties.
            var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
            var info = models.FirstOrDefault(m => m != null
                && string.Equals(m.ModelUri, modelUri, StringComparison.OrdinalIgnoreCase));
            var version = info?.ModelVersion ?? string.Empty;
            var pubDate = !string.IsNullOrEmpty(info?.PublicationDate)
                ? info!.PublicationDate!
                : DateTime.UtcNow.ToString("o");

            // Instantiate NamespaceMetadataType under the Core Server.Namespaces folder. A
            // null modellingRuleId makes this a standalone instance, so mandatory children are
            // materialized without ModellingRules — exactly as for any user-created instance.
            // The object's BrowseName IS its NamespaceUri (browseName = modelUri).
            var created = addressSpace.Instantiate(
                NAMESPACE_METADATA_TYPE,
                NAMESPACES_FOLDER,
                modelUri,
                browseName: modelUri,
                displayName: modelUri,
                mu => _addressSpace.GetNextNodeIdAsync(workspaceId, mu).Result,
                referenceTypeId: HAS_COMPONENT,
                modellingRuleId: null);

            // Seed the well-known scalar values on the freshly created mandatory children.
            foreach (var v in created.OfType<JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable>())
            {
                switch (BrowseNameLocalPart(v.BrowseName))
                {
                    case "NamespaceUri":
                        v.Value = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant(12, modelUri); break;
                    case "NamespaceVersion":
                        v.Value = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant(12, version); break;
                    case "NamespacePublicationDate":
                        v.Value = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant(13, pubDate); break;
                    case "IsNamespaceSubset":
                        v.Value = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant(1, false); break;
                    case "StaticNodeIdTypes":
                        // IdType[] = [Numeric (0)]. Stored as an Int32 array; the
                        // variable's IdType DataType drives the <ListOfInt32> rendering.
                        v.Value = new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant(6, new List<int> { 0 }); break;
                }
            }

            var changeset = new ModelChangeset();
            foreach (var n in created)
            {
                changeset.Nodes.Add(BuildNodeChange(n, ChangeKind.Upsert));
                if (n.References != null)
                {
                    foreach (var r in n.References)
                    {
                        changeset.References.Add(new ReferenceChange
                        {
                            Kind = ChangeKind.Upsert,
                            SourceNodeId = n.NodeId!,
                            ReferenceTypeId = r.ReferenceTypeId!,
                            TargetNodeId = r.TargetId!,
                            IsForward = r.IsForward ?? true
                        });
                    }
                }
            }

            // System-generated metadata is part of materializing the model itself
            // (create/import) — bypass the editability gate for this one internal write
            // so it succeeds regardless of the link's checkout state.
            await PersistModelAsync(addressSpace, workspaceId, $"nsu={modelUri};i=0", changeset,
                enforceEditable: false);
            _addressSpace.Invalidate(workspaceId);
        }

        /// <summary>Return a BrowseName's local part, stripping any "nsu=...;" namespace prefix.</summary>
        private static string BrowseNameLocalPart(string? browseName)
        {
            if (string.IsNullOrEmpty(browseName)) return string.Empty;
            var semi = browseName.IndexOf(';');
            return semi >= 0 ? browseName[(semi + 1)..] : browseName;
        }

        private async Task PersistModelAsync(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            Guid workspaceId,
            string nodeId,
            ModelChangeset? changeset = null,
            bool enforceEditable = true)
        {
            var modelUri = ExtractNamespaceUri(nodeId);
            if (string.IsNullOrEmpty(modelUri) || modelUri == OPC_UA_CORE_NS)
                throw new InvalidOperationException("Cannot determine model URI from node ID.");

            var models = await _storage.GetWorkspaceModelsAsync(workspaceId);
            var modelInfo = models.FirstOrDefault(m => m != null
                && string.Equals(m.ModelUri, modelUri, StringComparison.OrdinalIgnoreCase));
            if (modelInfo?.Id == null)
                throw new KeyNotFoundException($"Model with URI '{modelUri}' not found in workspace.");

            if (enforceEditable)
            {
                // Model rows are shared across workspaces, so node writes are only
                // allowed on a private, checked-out working copy — the same condition
                // the UI surfaces as IsReadOnly. Without this, a direct API call could
                // bypass the checkout lifecycle and silently edit a shared/published
                // model for every workspace linking it.
                var ws = await _storage.GetWorkspaceAsync(workspaceId);
                var link = ws?.Models?.FirstOrDefault(r => r.Id == modelInfo.Id.Value);
                if (link is not { IsPrivate: true, IsEditable: true })
                {
                    // The in-memory address space may already hold the rejected edit —
                    // drop the cached copy so the next read rebuilds from the database.
                    _addressSpace.Invalidate(workspaceId);
                    throw new ModelReadOnlyException(
                        $"Model '{modelUri}' is read-only in this workspace. Check it out before editing.");
                }
            }

            if (changeset != null)
            {
                changeset.WorkspaceId = workspaceId;
                changeset.ModelId = modelInfo.Id.Value;
                changeset.ModelUri = modelUri;
            }
            else
            {
                changeset = new ModelChangeset
                {
                    WorkspaceId = workspaceId,
                    ModelId = modelInfo.Id.Value,
                    ModelUri = modelUri
                };
            }

            // Only serialize XML if the storage backend needs it (file-backed)
            Stream? xmlStream = null;
            if (_storage.RequiresXmlContent)
            {
                var serializer = NodeSetSerializer.FromAddressSpace(addressSpace, modelUri);
                var ms = new MemoryStream();
                serializer.SaveXml(ms);
                ms.Seek(0, SeekOrigin.Begin);
                xmlStream = ms;
            }

            await _storage.PersistChangesAsync(changeset, xmlStream);
        }

        /// <summary>
        /// Maps a JsonNodeSet UANode to a NodeChange for the changeset.
        /// </summary>
        private static NodeChange BuildNodeChange(
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UANode node, ChangeKind kind)
        {
            var nc = new NodeChange
            {
                Kind = kind,
                NodeId = node.NodeId!,
                NodeClass = (int)(node.NodeClass ?? 0),
                BrowseName = node.BrowseName,
                DisplayName = GetLocalizedText(node.DisplayName),
                Description = GetLocalizedText(node.Description),
                ParentNodeId = node.ParentId,
                TypeDefinitionId = node.TypeId,
                ModellingRule = node.ModellingRuleId,
            };

            // Extract SuperTypeId from references (inverse HasSubtype)
            if (node.References != null)
            {
                foreach (var r in node.References)
                {
                    if (r.ReferenceTypeId == HAS_SUBTYPE && !(r.IsForward ?? true))
                        nc.SuperTypeId = r.TargetId;
                }
            }

            // Build Attributes JsonObject with node-class-specific properties
            var attrs = new System.Text.Json.Nodes.JsonObject();
            string nodeType = node switch
            {
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObjectType => "ObjectType",
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType => "VariableType",
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAReferenceType => "ReferenceType",
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType => "DataType",
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAObject => "Object",
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable => "Variable",
                JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAMethod => "Method",
                _ => "Object"
            };
            attrs["NodeType"] = nodeType;

            // DisplayName/Description are regenerated from the Attributes JSON on the XML
            // rebuild path (NodeSetConverter.PopulateCommonAttributes), not from the dedicated
            // DB columns. Mirror them here so an edit survives the next address-space rebuild.
            var displayNameAttr = LocalizedTextToAttr(node.DisplayName);
            if (displayNameAttr != null) attrs["DisplayName"] = displayNameAttr;
            var descriptionAttr = LocalizedTextToAttr(node.Description);
            if (descriptionAttr != null) attrs["Description"] = descriptionAttr;

            if (node.IsAbstract == true)
                attrs["IsAbstract"] = true;

            // DesignToolOnly is an instance-only flag (top-level Object/Variable).
            // Mirror it into the Attributes JSON so it survives the address-space
            // rebuild (NodeSetConverter restores it onto the Export UAInstance).
            if (node.DesignToolOnly == true)
                attrs["DesignToolOnly"] = true;

            // Conformance units, stored under the NodeSet XML's name for them. The whole
            // Attributes JSON is replaced on upsert, so leaving this out would drop the
            // units an imported nodeset declared the first time any other field is edited.
            if (node.ConformanceUnits is { Count: > 0 } conformanceUnits)
            {
                var categoryArray = new System.Text.Json.Nodes.JsonArray();
                foreach (var unit in conformanceUnits) categoryArray.Add(unit);
                attrs["Category"] = categoryArray;
            }

            switch (node)
            {
                case JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable v:
                    if (v.DataType != null) attrs["DataType"] = v.DataType;
                    if (v.ValueRank.HasValue) attrs["ValueRank"] = v.ValueRank.Value;
                    if (v.ArrayDimensions != null) attrs["ArrayDimensions"] = v.ArrayDimensions;
                    if (v.Value != null) attrs["Value"] = VariantToTypedJson(v.Value, v.DataType);
                    break;
                case JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType vt:
                    if (vt.DataType != null) attrs["DataType"] = vt.DataType;
                    if (vt.ValueRank.HasValue) attrs["ValueRank"] = vt.ValueRank.Value;
                    if (vt.ArrayDimensions != null) attrs["ArrayDimensions"] = vt.ArrayDimensions;
                    if (vt.Value != null) attrs["Value"] = VariantToTypedJson(vt.Value, vt.DataType);
                    break;
                case JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType dt:
                    if (dt.Definition != null)
                    {
                        // Convert to DataTypeDefinitionEntry format expected by DB storage
                        var defEntry = new NodeSetEditor.Model.DataTypeDefinitionEntry
                        {
                            // The JsonNodeSet definition carries no Name; the DataType's BrowseName
                            // (already in nsu=uri;Name form) is the normative source.
                            Name = node.BrowseName,
                            SymbolicName = dt.Definition.SymbolicName,
                            IsUnion = dt.Definition.IsUnion,
                            IsOptionSet = dt.Definition.IsOptionSet,
                        };
                        if (dt.Definition.Fields != null)
                        {
                            defEntry.Fields = dt.Definition.Fields.Select(f => new NodeSetEditor.Model.FieldDefinitionEntry
                            {
                                Name = f.Name,
                                DataType = f.DataType,
                                ValueRank = f.ValueRank,
                                ArrayDimensions = f.ArrayDimensions,
                                Value = f.Value,
                                IsOptional = f.IsOptional,
                                AllowSubTypes = f.AllowSubTypes,
                                SymbolicName = f.SymbolicName,
                                Description = GetLocalizedText(f.Description) is string desc
                                    ? new List<NodeSetEditor.Model.LocalizedTextEntry> { new() { Value = desc } }
                                    : null,
                            }).ToList();
                        }
                        attrs["Definition"] = System.Text.Json.JsonSerializer.SerializeToNode(defEntry);
                    }
                    break;
                case JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAReferenceType rt:
                    if (rt.Symmetric == true) attrs["Symmetric"] = true;
                    // Like DisplayName/Description, InverseName is regenerated from the
                    // Attributes JSON on the XML rebuild path — omit it here and an edit
                    // to it is gone by the next address-space rebuild.
                    var inverseNameAttr = LocalizedTextToAttr(rt.InverseName);
                    if (inverseNameAttr != null) attrs["InverseName"] = inverseNameAttr;
                    break;
            }

            nc.Attributes = attrs;
            return nc;
        }

        private static Opc.Ua.RestfulApi.Node UaNodeToRestNode(
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UANode node,
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace? addressSpace = null)
        {
            // An empty DisplayName is "not set", not "named the empty string" — fall back to
            // the BrowseName the same way a missing one does. Nodes saved before the write
            // path stopped storing empty LocalizedTexts would otherwise render as a bare
            // "[Model]:" in every list.
            var displayName = GetLocalizedText(node.DisplayName) is { Length: > 0 } dn
                ? dn
                : StripNamespace(node.BrowseName);
            var description = GetLocalizedText(node.Description);

            var dataTypeId = node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable v ? v.DataType
                : node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType vt ? vt.DataType
                : null;

            var referenceType = node as JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAReferenceType;
            var inverseName = GetLocalizedText(referenceType?.InverseName);

            return new Opc.Ua.RestfulApi.Node
            {
                NodeId = node.NodeId ?? string.Empty,
                ModelUri = ExtractNamespaceUri(node.NodeId),
                NodeClass = node.NodeClass switch
                {
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObjectType => Opc.Ua.RestfulApi.NodeClass.ObjectType,
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariableType => Opc.Ua.RestfulApi.NodeClass.VariableType,
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAReferenceType => Opc.Ua.RestfulApi.NodeClass.ReferenceType,
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UADataType => Opc.Ua.RestfulApi.NodeClass.DataType,
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAObject => Opc.Ua.RestfulApi.NodeClass.Object,
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAVariable => Opc.Ua.RestfulApi.NodeClass.Variable,
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAMethod => Opc.Ua.RestfulApi.NodeClass.Method,
                    JsonNodeSet::Opc.Ua.JsonNodeSet.Model.NodeClass.UAView => Opc.Ua.RestfulApi.NodeClass.View,
                    _ => Opc.Ua.RestfulApi.NodeClass.Object,
                },
                BrowseName = node.BrowseName ?? string.Empty,
                DisplayName = new Opc.Ua.RestfulApi.LocalizedText { Text = displayName },
                Description = description != null
                    ? new Opc.Ua.RestfulApi.LocalizedText { Text = description }
                    : null,
                IsAbstract = node.IsAbstract,
                // ReferenceType-only pair. Symmetric is always reported (a ReferenceType
                // is symmetric or it isn't) so the client can render and edit it; the
                // model omits it when false. InverseName has no meaning on a symmetric
                // type and the spec forbids one there.
                Symmetric = referenceType != null ? referenceType.Symmetric ?? false : null,
                InverseName = string.IsNullOrEmpty(inverseName) ? null
                    : new Opc.Ua.RestfulApi.LocalizedText { Text = inverseName },
                DesignToolOnly = node.DesignToolOnly,
                IsProperty = IsPropertyNode(addressSpace, node) ? true : null,
                ModellingRule = node.ModellingRuleId,
                TypeDefinition = node.TypeId,
                TypeDefinitionName = ResolveBrowseName(addressSpace, node.TypeId),
                DataType = dataTypeId,
                DataTypeName = ResolveBrowseName(addressSpace, dataTypeId),
                // Variables and VariableTypes always have a ValueRank (Scalar = -1
                // by default); the JsonNodeSet model stores Scalar as null, so
                // surface -1 to the API to keep the attribute present.
                ValueRank = node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable v2 ? (v2.ValueRank ?? -1)
                    : node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType vt2 ? (vt2.ValueRank ?? -1)
                    : null,
                Value = node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariable v3 ? UnwrapVariantValue(v3.Value?.Value)
                    : node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UAVariableType vt3 ? UnwrapVariantValue(vt3.Value?.Value)
                    : null,
                DataTypeForm = node is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.UADataType dtForm ? dtForm.DataTypeForm : null,
                Documentation = string.IsNullOrEmpty(node.Documentation) ? null : node.Documentation,
                // Conformance units. The JsonNodeSet model spells the XML <Category>
                // elements "ConformanceUnits"; the API keeps the XML name.
                Category = node.ConformanceUnits is { Count: > 0 } cu ? new List<string>(cu) : null,
            };
        }

        /// <summary>
        /// Clean up a caller-supplied conformance-unit list: one unit per entry, blanks
        /// dropped, nothing left meaning "no units" rather than an empty element list
        /// (which the NodeSet XML has no way to express).
        /// </summary>
        private static List<string>? NormalizeConformanceUnits(IEnumerable<string>? values)
        {
            if (values == null) return null;
            var units = values
                .Select(v => v?.Trim() ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToList();
            return units.Count > 0 ? units : null;
        }

        /// <summary>
        /// Unwraps <see cref="JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject"/> values
        /// for the REST GET response so that structure-typed variables return the Body JObject
        /// directly (with internal <c>$typeName</c>/<c>$typeNs</c> sidecars stripped). This
        /// keeps the shape symmetric with the PUT payload the client originally sent.
        ///
        /// <para>Because the nodeset library's schema-free XML reader encodes every leaf field
        /// as a Newtonsoft <see cref="Newtonsoft.Json.Linq.JObject"/> carrying a <c>$text</c>
        /// sidecar, this method performs a recursive conversion of the Newtonsoft tree into
        /// a plain System.Text.Json <see cref="System.Text.Json.Nodes.JsonNode"/> tree so that
        /// ASP.NET Core's default serializer produces clean user-facing JSON.</para>
        /// </summary>
        /// <summary>
        /// Renders a runtime <see cref="JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant"/>
        /// value as Part 6 JSON for the REST GET response. ExtensionObject becomes the
        /// <c>{UaTypeId, ...inlined body fields}</c> wrapper; arrays of ExtensionObject
        /// become a JSON array of those wrappers; primitives pass through.
        /// </summary>
        private static object? UnwrapVariantValue(object? value)
        {
            if (value == null) return null;

            if (value is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject eo)
                return BuildPart6ExtensionObjectJson(eo);

            // Newtonsoft JObject / JValue / JArray cannot be serialized by ASP.NET's
            // System.Text.Json — it reflects over their internal members and produces
            // garbage. Bridge to a System.Text.Json node by JSON-text round-trip.
            if (value is Newtonsoft.Json.Linq.JToken jtok)
                return System.Text.Json.Nodes.JsonNode.Parse(
                    jtok.ToString(Newtonsoft.Json.Formatting.None));

            // Excludes JObject etc. — see IsScalarObject's docstring.
            if (value is System.Collections.IList list && !IsScalarObject(value))
            {
                if (list.Count > 0 && list[0] is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject)
                {
                    var arr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var item in list)
                    {
                        if (item is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject itemEo)
                            arr.Add(BuildPart6ExtensionObjectJson(itemEo));
                        else
                            arr.Add(null);
                    }
                    return arr;
                }
                // For arrays of plain primitives, items may include Newtonsoft JTokens
                // (deserialized from DB-stored JSON via Newtonsoft) — convert each to
                // System.Text.Json so the response serializer doesn't choke.
                if (list.Count > 0 && list[0] is Newtonsoft.Json.Linq.JToken)
                {
                    var arr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var item in list)
                    {
                        if (item is Newtonsoft.Json.Linq.JToken jt)
                            arr.Add(System.Text.Json.Nodes.JsonNode.Parse(
                                jt.ToString(Newtonsoft.Json.Formatting.None)));
                        else
                            arr.Add(System.Text.Json.JsonSerializer.SerializeToNode(item));
                    }
                    return arr;
                }
            }

            return value;
        }

        /// <summary>
        /// Builds the Part 6 inline-fields JSON wrapper for an ExtensionObject —
        /// <c>{UaTypeId, &lt;field1&gt;, &lt;field2&gt;, ...}</c> — for outbound REST responses.
        /// </summary>
        private static System.Text.Json.Nodes.JsonObject BuildPart6ExtensionObjectJson(
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject eo)
        {
            var obj = new System.Text.Json.Nodes.JsonObject();
            if (eo.TypeId != null) obj["UaTypeId"] = eo.TypeId;
            if (eo.Encoding is byte enc && enc != 0)
            {
                obj["UaEncoding"] = enc;
                obj["UaBody"] = eo.Body?.ToString();
                return obj;
            }
            if (eo.Body is Newtonsoft.Json.Linq.JObject body)
            {
                var bodyNode = NewtonsoftToSystemTextJson(body) as System.Text.Json.Nodes.JsonObject;
                if (bodyNode != null)
                {
                    foreach (var prop in bodyNode.ToList())
                    {
                        if (prop.Key is "UaTypeId" or "UaEncoding" or "UaBody") continue;
                        bodyNode.Remove(prop.Key);
                        obj[prop.Key] = prop.Value;
                    }
                }
            }
            return obj;
        }

        /// <summary>
        /// Recursively converts a Newtonsoft JToken tree (as produced by the schema-free
        /// variant XML reader) into a System.Text.Json <see cref="System.Text.Json.Nodes.JsonNode"/>
        /// tree suitable for ASP.NET Core response serialization.
        ///
        /// <para>Leaves that look like the reader's canonical element JObject
        /// (<c>{"$typeName": "...", "$text": "..."}</c>) are collapsed to the <c>$text</c>
        /// string value. Nested structures strip all <c>$...</c> sidecars recursively.</para>
        /// </summary>
        private static System.Text.Json.Nodes.JsonNode? NewtonsoftToSystemTextJson(
            Newtonsoft.Json.Linq.JToken token)
        {
            // Single round-trip via JSON text. Newtonsoft knows how to write
            // every JValue type it might hold (double, float, long, ulong,
            // BigInteger, string, bool, DateTime, ...) as the correct JSON
            // primitive — including doubles in scientific form rather than
            // the long-integer expansion. STJ then re-parses to its node tree
            // preserving the exact textual representation. This replaces an
            // earlier per-property recursive walker that mishandled some
            // CLR types (BigInteger reflected as a property soup; sidecar
            // logic for the long-removed schema-free $text leaves).
            if (token == null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null) return null;
            var jsonText = token.ToString(Newtonsoft.Json.Formatting.None);
            return System.Text.Json.Nodes.JsonNode.Parse(jsonText);
        }

        /// <summary>
        /// Resolves a NodeId to a formatted BrowseName with model prefix.
        /// </summary>
        private static string? ResolveBrowseName(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace? addressSpace,
            string? nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || addressSpace == null) return null;
            var target = addressSpace.Read(nodeId);
            return target?.BrowseName;
        }

        #endregion

        #region Licenses

        /// <summary>
        /// Returns the selectable license catalog that drives the data-driven license selector
        /// (curated SPDX subset plus an "Other / Proprietary" entry).
        /// </summary>
        // The license catalog is static, public SPDX reference data (no per-user content), and
        // the client needs it before the user has signed in (it renders in the app shell). Allow
        // anonymous so the query succeeds on first load instead of 401-ing and caching an empty
        // list that never refetches after login.
        [AllowAnonymous]
        [HttpGet("licenses")]
        public async Task<ActionResult<List<LicenseOptionInfo>>> GetLicenses()
        {
            try
            {
                var options = await _storage.GetLicenseOptionsAsync();
                return Ok(options.Select(o => new LicenseOptionInfo
                {
                    SpdxId = o.SpdxId,
                    Name = o.Name,
                    ReferenceUrl = o.ReferenceUrl,
                    IsCustom = o.IsCustom,
                    SortOrder = o.SortOrder,
                }).ToList());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting license catalog");
                return InternalError(e);
            }
        }

        /// <summary>True when <paramref name="url"/> is a valid absolute http(s) URL.</summary>
        private static bool IsValidLicenseUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        /// <summary>
        /// Validates a license selection and resolves its reference URL. For a known catalog
        /// license the canonical URL is used (any supplied URL is ignored). For a custom / "Other"
        /// license (an id not present as a non-custom catalog entry) a valid absolute http(s) URL
        /// is required. Returns (true, resolvedUrl, null) on success, else (false, null, error).
        /// </summary>
        private async Task<(bool Ok, string? ResolvedUrl, string? Error)> ResolveLicenseAsync(string? license, string? licenseUrl)
        {
            if (string.IsNullOrWhiteSpace(license))
                return (false, null, "A license is required.");

            var id = license.Trim();
            var options = await _storage.GetLicenseOptionsAsync();
            var match = options.FirstOrDefault(o =>
                !o.IsCustom && string.Equals(o.SpdxId, id, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return (true, match.ReferenceUrl, null);

            // Custom / "Other" license — a valid reference URL is mandatory.
            var url = licenseUrl?.Trim();
            if (!IsValidLicenseUrl(url))
                return (false, null, "A valid license URL is required for a custom or 'Other' license.");
            return (true, url, null);
        }

        #endregion

        #region User Preferences

        /// <summary>
        /// Get the current user's preferences.
        /// </summary>
        [HttpGet("user/preferences")]
        public async Task<ActionResult<UserPreferences>> GetUserPreferences()
        {
            try
            {
                var user = GetCurrentUser();

                if (!user.IsAuthenticated || user.UserId == null)
                {
                    return Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required."));
                }

                // First sight of this user provisions a unique display Name and a
                // DefaultDomain from their email.
                var pref = await _storage.EnsureUserPreferenceAsync(user.UserId, user.Email, user.DisplayName, user.TenantId);

                return Ok(new UserPreferences
                {
                    SelectedServer = pref.SelectedWorkspaceId.HasValue
                        ? UrnUtils.ToUrn(pref.SelectedWorkspaceId.Value) : null,
                    ThemeMode = pref.ThemeMode,
                    Name = pref.Name,
                    DefaultDomain = pref.DefaultDomain,
                    DefaultLicense = pref.DefaultLicense,
                    DefaultLicenseUrl = pref.DefaultLicenseUrl,
                    DefaultCopyrightHolder = pref.DefaultCopyrightHolder,
                    TermsAccepted = pref.TermsAcceptedAt.HasValue,
                    BetaTester = _betaTesters.IsBetaTester(user.Email),
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting user preferences");
                return InternalError(e);
            }
        }

        /// <summary>
        /// Update the current user's preferences.
        /// </summary>
        [HttpPut("user/preferences")]
        public async Task<ActionResult<UserPreferences>> UpdateUserPreferences([FromBody] UserPreferences preferences)
        {
            try
            {
                var user = GetCurrentUser();

                if (!user.IsAuthenticated || user.UserId == null)
                {
                    return Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required."));
                }

                if (preferences.SelectedServer != null)
                {
                    var (_, workspace, error) = await ResolveServer(preferences.SelectedServer);
                    if (error != null) return error;

                    await _storage.SetUserSelectedWorkspaceAsync(user.UserId, workspace!.Id!.Value);
                }

                if (preferences.ThemeMode != null)
                {
                    var normalised = preferences.ThemeMode.Trim().ToLowerInvariant();
                    if (normalised != "light" && normalised != "dark")
                    {
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                            "themeMode must be 'light' or 'dark'."));
                    }
                    await _storage.SetUserThemeModeAsync(user.UserId, normalised);
                }

                if (preferences.Name != null)
                {
                    var (ok, nameError) = await _storage.SetUserNameAsync(user.UserId, preferences.Name);
                    if (!ok)
                    {
                        return Conflict(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                            nameError ?? "The name is not available."));
                    }
                }

                if (preferences.DefaultDomain != null)
                {
                    await _storage.SetUserDefaultDomainAsync(user.UserId, preferences.DefaultDomain);
                }

                if (preferences.DefaultLicense != null)
                {
                    var url = preferences.DefaultLicenseUrl?.Trim();
                    // If a URL is supplied (always the case for a custom "Other" default) it must be valid.
                    if (!string.IsNullOrWhiteSpace(url) && !IsValidLicenseUrl(url))
                    {
                        return BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument),
                            "defaultLicenseUrl must be a valid absolute http(s) URL."));
                    }
                    await _storage.SetUserDefaultLicenseAsync(user.UserId, preferences.DefaultLicense, url);
                }

                if (preferences.DefaultCopyrightHolder != null)
                {
                    await _storage.SetUserDefaultCopyrightHolderAsync(user.UserId, preferences.DefaultCopyrightHolder);
                }

                // Return the current state (provisioning fills any not-yet-set fields).
                var pref = await _storage.EnsureUserPreferenceAsync(user.UserId, user.Email, user.DisplayName, user.TenantId);

                return Ok(new UserPreferences
                {
                    SelectedServer = pref.SelectedWorkspaceId.HasValue
                        ? UrnUtils.ToUrn(pref.SelectedWorkspaceId.Value) : null,
                    ThemeMode = pref.ThemeMode,
                    Name = pref.Name,
                    DefaultDomain = pref.DefaultDomain,
                    DefaultLicense = pref.DefaultLicense,
                    DefaultLicenseUrl = pref.DefaultLicenseUrl,
                    DefaultCopyrightHolder = pref.DefaultCopyrightHolder,
                    TermsAccepted = pref.TermsAcceptedAt.HasValue,
                    BetaTester = _betaTesters.IsBetaTester(user.Email),
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error updating user preferences");
                return InternalError(e);
            }
        }

        /// <summary>Records acceptance of the Terms of Use for the current user.</summary>
        [HttpPost("user/terms")]
        public async Task<IActionResult> AcceptTerms()
        {
            try
            {
                var user = GetCurrentUser();
                if (!user.IsAuthenticated || user.UserId == null)
                    return Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required."));

                await _storage.AcceptTermsAsync(user.UserId);
                return NoContent();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error recording terms acceptance");
                return InternalError(e);
            }
        }

        #endregion

        #region Helpers

        private AuthenticatedUser GetCurrentUser()
        {
            return AuthenticatedUser.FromClaimsPrincipal(HttpContext.User);
        }

        /// <summary>
        /// Returns the local part of an email (everything before '@'), used as the
        /// "creator" display name on published models. Null/blank in → null out.
        /// </summary>
        private static string? StripEmailDomain(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            var at = email.IndexOf('@');
            return at > 0 ? email[..at] : email;
        }

        /// <summary>
        /// Resolves an applicationUri to a workspace with access checks.
        /// Returns (user, workspace, null) on success, or (null, null, errorResult) on failure.
        /// When <paramref name="requireWrite"/> is true the user must be the
        /// workspace owner; shared (ACL) workspaces are read-only and yield a 403.
        /// </summary>
        private async Task<(AuthenticatedUser? user, Workspace? workspace, ActionResult? error)> ResolveServer(string? applicationUri, bool requireWrite = false)
        {
            var user = GetCurrentUser();

            if (!user.IsAuthenticated || user.UserId == null)
            {
                return (null, null, Unauthorized(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied), "Authentication required.")));
            }

            Workspace? workspace;

            // If no server specified, resolve the default
            if (string.IsNullOrEmpty(applicationUri))
            {
                workspace = await ResolveDefaultWorkspace(user);
                if (workspace == null)
                {
                    return (null, null, NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), "No default server available.")));
                }
            }
            else
            {
                var id = UrnUtils.ParseUrn(applicationUri);
                if (id == null)
                {
                    return (null, null, BadRequest(MakeError(Opc.Ua.StatusCodes.BadInvalidArgument, nameof(Opc.Ua.StatusCodes.BadInvalidArgument), $"Invalid applicationUri: '{applicationUri}'. Expected format: urn:uuid:<guid>")));
                }

                workspace = await GetAccessibleWorkspace(id.Value, user);
                if (workspace == null)
                {
                    return (null, null, NotFound(MakeError(Opc.Ua.StatusCodes.BadNotFound, nameof(Opc.Ua.StatusCodes.BadNotFound), $"Server '{applicationUri}' not found.")));
                }
            }

            if (requireWrite && !CanWrite(workspace, user))
            {
                return (null, null, Forbidden(MakeError(Opc.Ua.StatusCodes.BadUserAccessDenied, nameof(Opc.Ua.StatusCodes.BadUserAccessDenied),
                    "This workspace is read-only. Only the owner can make changes.")));
            }

            return (user, workspace, null);
        }

        /// <summary>
        /// Whether the user may modify the workspace. Owner-only: shared (ACL)
        /// users are read-only.
        /// </summary>
        private static bool CanWrite(Workspace workspace, AuthenticatedUser user)
            => workspace.Owner == user.UserId;

        /// <summary>
        /// Resolves the default workspace: user's selected workspace, or the first accessible one.
        /// </summary>
        private async Task<Workspace?> ResolveDefaultWorkspace(AuthenticatedUser user)
        {
            // Try the user's explicitly selected workspace first
            var selectedId = await _storage.GetUserSelectedWorkspaceAsync(user.UserId!);
            if (selectedId.HasValue)
            {
                var selected = await GetAccessibleWorkspace(selectedId.Value, user);
                if (selected != null) return selected;
            }

            // Fall back to the first accessible workspace
            var workspaces = await _storage.GetWorkspacesAsync(user.UserId!, user.Email);
            return workspaces.FirstOrDefault();
        }

        private async Task<Workspace?> GetAccessibleWorkspace(Guid workspaceId, AuthenticatedUser user)
        {
            var workspace = await _storage.GetWorkspaceAsync(workspaceId);

            if (workspace == null) return null;

            // Owner has access
            if (workspace.Owner == user.UserId) return workspace;

            // ACL check
            var emailLower = user.Email?.ToLowerInvariant();
            if (emailLower != null && workspace.Acl != null && workspace.Acl.Contains(emailLower))
            {
                return workspace;
            }

            return null;
        }

        private string GetBaseUrl()
        {
            return $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/opcua/v1";
        }

        private async Task<WorkspaceDescription> ToWorkspaceDescriptionAsync(
            Workspace ws, Guid? selectedId = null, IReadOnlyDictionary<string, string>? ownerNames = null)
        {
            var user = GetCurrentUser();
            var isOwner = ws.Owner == user.UserId;

            // Prefer the owner's chosen display Name; the email-local-part remains
            // the fallback for owners who never provisioned a preference row.
            string? ownerName = null;
            if (ownerNames != null)
            {
                ownerNames.TryGetValue(ws.Owner!, out ownerName);
            }
            else
            {
                var map = await _storage.GetUserDisplayNamesAsync(new[] { ws.Owner! });
                map.TryGetValue(ws.Owner!, out ownerName);
            }

            return new WorkspaceDescription
            {
                ApplicationUri = UrnUtils.ToUrn(ws.Id!.Value),
                ApplicationName = new Opc.Ua.RestfulApi.LocalizedText { Text = ws.Name },
                DiscoveryUrls = new List<string> { GetBaseUrl() },
                Description = ws.Description != null
                    ? new Opc.Ua.RestfulApi.LocalizedText { Text = ws.Description }
                    : null,
                Owner = ownerName ?? GetOwnerDisplayName(ws, isOwner ? user.Email : null),
                IsOwner = isOwner,
                CanWrite = isOwner,
                // The ACL is a list of collaborator emails (PII) and is only
                // editable by the owner. Don't expose it to shared-in users.
                Acl = isOwner ? ws.Acl : null,
                IsDefault = selectedId.HasValue ? ws.Id == selectedId : null,
                CreatedAt = ws.CreateDate,
                ModifiedAt = ws.ModifyDate,
            };
        }

        /// <summary>
        /// Returns a display-friendly owner name from the workspace's ownerEmail.
        /// Falls back to the provided email (e.g. current user's email for owned workspaces).
        /// Strips the @domain portion (e.g. "randy@example.com" → "randy").
        /// </summary>
        private static string? GetOwnerDisplayName(Workspace ws, string? fallbackEmail)
        {
            var email = ws.OwnerEmail ?? fallbackEmail;
            if (string.IsNullOrEmpty(email)) return null;

            var atIndex = email.IndexOf('@');
            return atIndex > 0 ? email[..atIndex] : email;
        }

        private static WorkspaceNamespaceInfo ToNamespaceInfo(ModelInfo m, bool? isPrivate, bool? isEditable = null)
        {
            return new WorkspaceNamespaceInfo
            {
                Id = m.Id,
                Uri = m.ModelUri ?? string.Empty,
                Name = m.Name,
                Version = m.ModelVersion,
                PublicationDate = m.PublicationDate != null && DateTime.TryParse(m.PublicationDate, out var dt) ? dt : null,
                Description = m.Description != null
                    ? new Opc.Ua.RestfulApi.LocalizedText { Text = m.Description }
                    : null,
                IsPrivate = isPrivate,
                IsEditable = isEditable,
                // A model is editable only when it is private AND checked out.
                IsReadOnly = !(isPrivate == true && isEditable == true),
                License = m.License,
                LicenseUrl = m.LicenseUrl,
                CopyrightHolder = m.CopyrightHolder,
            };
        }

        private static string GenerateExportFileName(ModelInfo modelInfo, string extension = ".xml")
        {
            var invalidChars = Path.GetInvalidFileNameChars()
                .Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
                .Distinct()
                .ToArray();

            string Sanitize(string? input) =>
                string.IsNullOrEmpty(input) ? "unknown"
                : new string(input.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

            var name = Sanitize(modelInfo.Name);
            var version = Sanitize(modelInfo.ModelVersion);
            var datePart = DateTime.TryParse(modelInfo.PublicationDate, out var d) ? d.ToString("yyyy_MM_dd", System.Globalization.CultureInfo.InvariantCulture) : "0000_00_00";

            return $"opcua_{name}_{version}_{datePart}{extension}".ToLowerInvariant();
        }

        private static ErrorResponse MakeError(long code, string symbol, string message)
        {
            return new ErrorResponse
            {
                StatusCode = new Opc.Ua.RestfulApi.StatusCode { Code = code, Symbol = symbol },
                Message = message,
                Timestamp = DateTime.UtcNow
            };
        }

        private ObjectResult Forbidden(ErrorResponse error)
        {
            return StatusCode(403, error);
        }

        private ObjectResult InternalError(Exception e)
        {
            // Don't leak raw exception text (DB/EF internals, paths) to the caller.
            // Log the full exception with the request's trace id and return only that
            // id; it is recorded by Application Insights as operation_Id, so support
            // can find the real cause with:  union exceptions,traces | where operation_Id == "<id>"
            var reference = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier;
            _logger.LogError(e, "Unhandled API error (reference {Reference})", reference);
            return StatusCode(500, MakeError(Opc.Ua.StatusCodes.BadInternalError, nameof(Opc.Ua.StatusCodes.BadInternalError),
                $"An unexpected internal error occurred. Reference ID: {reference}"));
        }

        /// <summary>
        /// Wraps a Variant value in the typed JSON envelope expected by the DB storage layer.
        /// E.g., Variant(12, "Hello") with DataType="i=12" → {"String": "Hello"}
        /// </summary>
        /// <summary>
        /// Converts an in-memory <see cref="JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant"/>
        /// into Part 6 §5.4 JSON for DB persistence. The result is the typed JSON value
        /// directly: built-in scalars as native JSON, arrays as JSON arrays, structures
        /// as the Part 6 inline-fields ExtensionObject wrapper <c>{UaTypeId, ...fields}</c>,
        /// matrices as <c>{Array, Dimensions}</c>. NO legacy <c>{"&lt;TypeName&gt;": value}</c>
        /// wrapper — that shape is incompatible with the Part 6 reader on the way back.
        /// </summary>
        private static System.Text.Json.Nodes.JsonNode? VariantToTypedJson(
            JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant variant, string? dataType)
        {
            if (variant.Value == null) return null;

            // Matrix — multi-dim arrays use {Array, Dimensions}.
            if (variant.Dimensions is { Count: > 1 }
                && variant.Value is System.Collections.IEnumerable matrixItems
                && !IsScalarObject(variant.Value))
            {
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in matrixItems) arr.Add(SerializeVariantValue(item));
                var dims = new System.Text.Json.Nodes.JsonArray();
                foreach (var d in variant.Dimensions) dims.Add(d);
                return new System.Text.Json.Nodes.JsonObject { ["Array"] = arr, ["Dimensions"] = dims };
            }

            // 1-D array — JSON array of items. Excludes types that LOOK like collections
            // but are actually compound scalars (Newtonsoft JObject implements IList over
            // its child JTokens — iterating it would wrap each property's value in a
            // bogus array; same for JValue / ExtensionObject / strings / byte[]).
            if (variant.Value is System.Collections.IList list
                && !IsScalarObject(variant.Value))
            {
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in list) arr.Add(SerializeVariantValue(item));
                return arr;
            }

            // Scalar — value directly (no per-type wrapper).
            return SerializeVariantValue(variant.Value);
        }

        /// <summary>
        /// True for runtime types whose <see cref="System.Collections.IEnumerable"/>
        /// implementation is misleading — these are scalars even though they enumerate.
        /// JObject implements IList&lt;JToken&gt; over its properties; JArray IS a list
        /// but should be processed by the array path; ExtensionObject is a struct;
        /// byte[] is the ByteString primitive; string is its own thing.
        /// </summary>
        private static bool IsScalarObject(object? value) =>
            value is string
            || value is byte[]
            || value is Newtonsoft.Json.Linq.JObject
            || value is Newtonsoft.Json.Linq.JValue
            || value is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject;

        /// <summary>
        /// Serializes a single Variant value into a System.Text.Json JsonNode using Part 6
        /// shapes. ExtensionObject becomes <c>{UaTypeId, ...inlined body fields}</c>;
        /// Newtonsoft <see cref="Newtonsoft.Json.Linq.JObject"/> bodies are bridged via
        /// JSON text round-trip (System.Text.Json can't reflect Newtonsoft types).
        /// </summary>
        private static System.Text.Json.Nodes.JsonNode? SerializeVariantValue(object? value)
        {
            if (value == null) return null;

            if (value is JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject eo)
            {
                var node = new System.Text.Json.Nodes.JsonObject();
                if (eo.TypeId != null) node["UaTypeId"] = eo.TypeId;
                if (eo.Encoding is byte enc && enc != 0)
                {
                    node["UaEncoding"] = enc;
                    node["UaBody"] = eo.Body?.ToString();
                    return node;
                }
                // JSON inline: spread body fields alongside UaTypeId.
                if (eo.Body is Newtonsoft.Json.Linq.JObject bodyJo)
                {
                    var bodyNode = System.Text.Json.Nodes.JsonNode.Parse(
                        bodyJo.ToString(Newtonsoft.Json.Formatting.None)) as System.Text.Json.Nodes.JsonObject;
                    if (bodyNode != null)
                    {
                        foreach (var prop in bodyNode.ToList())
                        {
                            if (prop.Key is "UaTypeId" or "UaEncoding" or "UaBody") continue;
                            bodyNode.Remove(prop.Key);
                            node[prop.Key] = prop.Value;
                        }
                    }
                }
                return node;
            }

            if (value is Newtonsoft.Json.Linq.JObject jo)
                return System.Text.Json.Nodes.JsonNode.Parse(jo.ToString(Newtonsoft.Json.Formatting.None));

            return System.Text.Json.JsonSerializer.SerializeToNode(value);
        }

        /// <summary>
        /// Serializes a list of Variant items (each handled via <see cref="SerializeVariantValue"/>)
        /// into a JsonArray. Needed so arrays of ExtensionObject items are emitted correctly.
        /// </summary>
        private static System.Text.Json.Nodes.JsonArray SerializeVariantItems(System.Collections.IList list)
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var item in list)
            {
                arr.Add(SerializeVariantValue(item));
            }
            return arr;
        }

        /// <summary>
        /// Maps DataType NodeId to OPC UA built-in type number.
        /// </summary>
        /// <summary>
        /// Maps DataType NodeId to OPC UA built-in type number (1-24).
        /// See OPC UA Part 6, Table A.1.
        /// </summary>
        private static readonly Dictionary<string, int> DataTypeToUaType = new()
        {
            ["i=1"] = 1,   // Boolean
            ["i=2"] = 2,   // SByte
            ["i=3"] = 3,   // Byte
            ["i=4"] = 4,   // Int16
            ["i=5"] = 5,   // UInt16
            ["i=6"] = 6,   // Int32
            ["i=7"] = 7,   // UInt32
            ["i=8"] = 8,   // Int64
            ["i=9"] = 9,   // UInt64
            ["i=10"] = 10, // Float
            ["i=11"] = 11, // Double
            ["i=12"] = 12, // String
            ["i=13"] = 13, // DateTime
            ["i=14"] = 14, // Guid
            ["i=15"] = 15, // ByteString
            ["i=16"] = 16, // XmlElement
            ["i=17"] = 17, // NodeId
            ["i=18"] = 18, // ExpandedNodeId
            ["i=19"] = 19, // StatusCode
            ["i=20"] = 20, // QualifiedName
            ["i=21"] = 21, // LocalizedText
            ["i=22"] = 22, // ExtensionObject (Structure)
            ["i=23"] = 23, // DataValue
            ["i=24"] = 24, // Variant (only valid for arrays)
        };

        /// <summary>
        /// Maps UaType number to XML element name using the BuiltInType enum.
        /// </summary>
        private static string UaTypeToXmlName(int uaType) =>
            Enum.IsDefined(typeof(JsonNodeSet::Opc.Ua.JsonNodeSet.Model.BuiltInType), uaType)
                ? ((JsonNodeSet::Opc.Ua.JsonNodeSet.Model.BuiltInType)uaType).ToString()
                : nameof(JsonNodeSet::Opc.Ua.JsonNodeSet.Model.BuiltInType.String);

        /// <summary>
        /// Checks if a node is equal to or a subtype of the given ancestor DataType.
        /// </summary>
        private static bool IsSubtypeOf(
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace addressSpace,
            string nodeId, string ancestorId)
        {
            var current = nodeId;
            var visited = new HashSet<string>();
            while (current != null && visited.Add(current))
            {
                if (current == ancestorId) return true;
                var inverseRefs = addressSpace.Browse(current, HAS_SUBTYPE,
                    includeForward: false, includeInverse: true);
                if (inverseRefs.Count == 0) break;
                current = inverseRefs[0].TargetNodeId;
            }
            return false;
        }

        /// <summary>
        /// Sanitizes ArrayDimensions based on ValueRank.
        /// If ValueRank &lt; 1 (Scalar or Any), ArrayDimensions must be null.
        /// If ValueRank &gt;= 1, ensures the dimension count matches ValueRank.
        /// </summary>
        private static string? SanitizeArrayDimensions(int? valueRank, string? arrayDimensions)
        {
            if (!valueRank.HasValue || valueRank.Value < 1)
                return null;

            if (string.IsNullOrEmpty(arrayDimensions))
            {
                // Generate default dimensions (all zeros) matching ValueRank
                return string.Join(",", Enumerable.Repeat("0", valueRank.Value));
            }

            var parts = arrayDimensions.Split(',');
            if (parts.Length == valueRank.Value)
                return arrayDimensions;

            // Adjust to match ValueRank: truncate or pad with zeros
            var adjusted = parts.Take(valueRank.Value)
                .Concat(Enumerable.Repeat("0", Math.Max(0, valueRank.Value - parts.Length)));
            return string.Join(",", adjusted);
        }

        // Well-known abstract DataType NodeIds → built-in encoding type
        private static readonly Dictionary<string, int> DataTypeEncodingMap = new()
        {
            ["i=29"] = 6,  // Enumeration → Int32
            ["i=22"] = 22, // Structure → ExtensionObject
        };

        /// <summary>
        /// Resolves a custom DataType NodeId to its built-in type number by walking
        /// the supertype chain until a known built-in type is found.
        /// Enumeration subtypes → Int32 (6), Structure subtypes → ExtensionObject (22), etc.
        /// </summary>
        private static int ResolveBuiltInType(string dataType,
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace? addressSpace)
        {
            if (addressSpace == null) return 12; // String fallback

            var visited = new HashSet<string>();
            var current = dataType;
            while (current != null && visited.Add(current))
            {
                if (DataTypeToUaType.TryGetValue(current, out var builtIn))
                    return builtIn;
                if (DataTypeEncodingMap.TryGetValue(current, out var encoding))
                    return encoding;

                // Walk supertype: find inverse HasSubtype reference
                var node = addressSpace.Read(current);
                if (node?.References == null) break;

                string? superTypeId = null;
                foreach (var r in node.References)
                {
                    if (r.ReferenceTypeId == HAS_SUBTYPE && !(r.IsForward ?? true))
                    {
                        superTypeId = r.TargetId;
                        break;
                    }
                }
                current = superTypeId;
            }

            return 12; // String fallback
        }

        /// <summary>
        /// Converts a JSON element from the API request into a Variant.
        /// </summary>
        private static JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant JsonElementToVariant(
            System.Text.Json.JsonElement element, string? dataType, string? arrayDimensions = null,
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace? addressSpace = null)
        {
            int uaType = 12; // Default to String
            if (dataType != null && !DataTypeToUaType.TryGetValue(dataType, out uaType))
            {
                // Resolve custom DataTypes by walking the supertype chain
                uaType = ResolveBuiltInType(dataType, addressSpace);
            }

            // Parse array dimensions (e.g., "3,3" → [3,3])
            List<int>? dims = null;
            if (!string.IsNullOrEmpty(arrayDimensions))
            {
                dims = arrayDimensions.Split(',')
                    .Select(s => int.TryParse(s.Trim(), out var d) ? d : 0)
                    .ToList();
                // Only keep dimensions for multi-dimensional (2+)
                if (dims.Count <= 1) dims = null;
            }

            object? value;
            if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                // Flatten to a list of typed values (multi-dimensional arrays are flat in OPC UA)
                var items = new List<object>();
                FlattenJsonArray(element, uaType, items, dataType, addressSpace);
                value = items;
            }
            else if (uaType == 22 && element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Structure / Union value — parse as a Newtonsoft JObject and wrap as a
                // canonical Json.ExtensionObject whose TypeId is the variable's DataType
                // NodeId. A $typeName sidecar is attached using the DataType BrowseName so
                // the XML emission path can produce a properly named struct wrapper element.
                value = ParseStructureBody(element, dataType, addressSpace);
            }
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Object
                && (uaType == 19 || uaType == 21 || uaType == 23))
            {
                // Compound built-in objects per Part 6: StatusCode (19) {Code, Symbol?},
                // LocalizedText (21) {Locale?, Text?}, DataValue (23). Stored as a
                // Newtonsoft JObject so the XML writer's dedicated cases pick them up
                // and so VariantToTypedJson surfaces them as Part 6 JSON on read.
                value = Newtonsoft.Json.Linq.JObject.Parse(element.GetRawText());
            }
            else
            {
                value = element.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => element.GetString(),
                    System.Text.Json.JsonValueKind.Number => uaType switch
                    {
                        1 => element.GetInt32() != 0,
                        2 => (sbyte)element.GetInt32(),
                        3 => (byte)element.GetInt32(),
                        4 => (short)element.GetInt32(),
                        5 => (ushort)element.GetInt32(),
                        6 => element.GetInt32(),
                        7 => element.GetUInt32(),
                        8 => element.GetInt64(),
                        9 => element.GetUInt64(),
                        10 => element.GetSingle(),
                        11 => element.GetDouble(),
                        _ => element.GetDouble()
                    },
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => element.GetRawText()
                };
            }

            return new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.Variant(uaType, value, dims);
        }

        /// <summary>
        /// Parses an inbound JSON object into a Part 6 ExtensionObject wrapping a
        /// Newtonsoft body. The DataType NodeId is preserved on the wrapper so the
        /// XML emit path (<see cref="JsonNodeSet::Opc.Ua.JsonNodeSet.VariantConverter"/>)
        /// can recover the inner struct wrapper element name from the AddressSpace.
        /// No bespoke <c>$typeName</c> sidecar is attached — Part 6 carries that
        /// information in <c>UaTypeId</c> alone.
        ///
        /// Accepts both shapes the API edge sees:
        ///   * Part 6 inline-fields wrapper:  {UaTypeId: "...", Field1: ..., Field2: ...}
        ///   * Bare body object:              {Field1: ..., Field2: ...}
        /// </summary>
        private static JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject ParseStructureBody(
            System.Text.Json.JsonElement element, string? dataType,
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace? addressSpace)
        {
            var raw = Newtonsoft.Json.Linq.JObject.Parse(element.GetRawText());

            // Pull TypeId from UaTypeId if present, falling back to the variable's DataType.
            string? typeId = raw["UaTypeId"]?.ToString() ?? dataType;

            // Strip the Part 6 reserved keys; what remains is the body fields.
            var body = new Newtonsoft.Json.Linq.JObject();
            foreach (var prop in raw.Properties())
            {
                if (prop.Name is "UaTypeId" or "UaEncoding" or "UaBody") continue;
                body.Add(prop.Name, prop.Value);
            }

            return new JsonNodeSet::Opc.Ua.JsonNodeSet.Model.ExtensionObject
            {
                TypeId = typeId,
                Body = body,
            };
        }

        /// <summary>
        /// Recursively flattens a JSON array (including nested arrays for multi-dimensional)
        /// into a flat list of typed values.
        /// </summary>
        private static void FlattenJsonArray(System.Text.Json.JsonElement arr, int uaType,
            List<object> items, string? dataType = null,
            JsonNodeSet::Opc.Ua.JsonNodeSet.AddressSpace? addressSpace = null)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    FlattenJsonArray(item, uaType, items, dataType, addressSpace);
                }
                else if (uaType == 22 && item.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // ExtensionObject array item → wrap as canonical Json.ExtensionObject.
                    items.Add(ParseStructureBody(item, dataType, addressSpace));
                }
                else if (item.ValueKind == System.Text.Json.JsonValueKind.Object
                    && (uaType == 19 || uaType == 21 || uaType == 23))
                {
                    // StatusCode / LocalizedText / DataValue array item — store as JObject
                    // (Part 6 compound shape). The XML writer's per-built-in cases handle it.
                    items.Add(Newtonsoft.Json.Linq.JObject.Parse(item.GetRawText()));
                }
                else
                {
                    object? val = item.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Number => uaType switch
                        {
                            5 => (object)(ushort)item.GetInt32(),
                            6 => item.GetInt32(),
                            7 => item.GetUInt32(),
                            11 => item.GetDouble(),
                            10 => item.GetSingle(),
                            _ => item.GetDouble()
                        },
                        System.Text.Json.JsonValueKind.String => item.GetString()!,
                        System.Text.Json.JsonValueKind.True => true,
                        System.Text.Json.JsonValueKind.False => false,
                        _ => item.GetRawText()
                    };
                    if (val != null) items.Add(val);
                }
            }
        }

        #endregion
    }

    #region Request DTOs

    public class CreateServerRequest
    {
        public string? ApplicationName { get; set; }
        public string? Description { get; set; }
        public List<string>? Acl { get; set; }
    }

    public class UpdateServerRequest
    {
        public string? ApplicationName { get; set; }
        public string? Description { get; set; }
        public List<string>? Acl { get; set; }
    }

    public class CreateNamespaceRequest
    {
        public string? Uri { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }

        /// <summary>License identifier (SPDX id, or a custom id when "Other / Proprietary"). Required.</summary>
        public string? License { get; set; }

        /// <summary>Reference URL — required (and must be a valid http(s) URL) for a custom/"Other" license.</summary>
        public string? LicenseUrl { get; set; }

        /// <summary>Copyright holder. Required.</summary>
        public string? CopyrightHolder { get; set; }
    }

    public class CheckinNamespaceRequest
    {
        /// <summary>One of "keep", "publish", or "discard".</summary>
        public string? Action { get; set; }

        /// <summary>
        /// Caller-chosen version (keep or publish). When blank, the server derives one — the
        /// next published version for "publish", the working version with its <c>-alpha</c>
        /// dropped for "keep". Either way "keep" strips the working-copy suffix.
        /// </summary>
        public string? Version { get; set; }

        /// <summary>Description for the published model (publish only). Required to publish.</summary>
        public string? Description { get; set; }
    }

    public class UpdateNamespaceRequest
    {
        public string? Uri { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }

        /// <summary>License identifier — editable for private models. Validated like create.</summary>
        public string? License { get; set; }

        /// <summary>License URL — required (valid http(s)) for a custom/"Other" license.</summary>
        public string? LicenseUrl { get; set; }

        /// <summary>Copyright holder — editable for private models. Required when changing the license.</summary>
        public string? CopyrightHolder { get; set; }
    }

    public class LinkNamespaceRequest
    {
        public bool? IsPrivate { get; set; }
    }

    public class CreateNodeRequest
    {
        public string? ModelUri { get; set; }
        public string? NodeClass { get; set; }
        public string? BrowseName { get; set; }
        /// <summary>
        /// Optional: namespace URI for the BrowseName. When set, overrides ModelUri for BrowseName qualification.
        /// Used when overriding inherited children whose BrowseName namespace differs from the node's model.
        /// </summary>
        public string? BrowseNameModelUri { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? ReferenceTypeId { get; set; }
        public string? TypeDefinitionId { get; set; }
        public string? ModellingRuleId { get; set; }
        public bool? IsAbstract { get; set; }

        /// <summary>ReferenceType only: the reference reads the same in both directions.</summary>
        public bool? Symmetric { get; set; }

        /// <summary>
        /// ReferenceType only: how the reference reads when browsed backwards
        /// (e.g. "ComponentOf" for HasComponent). Not allowed on a symmetric type.
        /// </summary>
        public string? InverseName { get; set; }

        /// <summary>
        /// DataType only: the type is a set of bit flags rather than a plain number.
        /// Requires a supertype derived from UInteger, and can only be set here —
        /// an OptionSet cannot be turned into a plain DataType afterwards, or back.
        /// </summary>
        public bool? IsOptionSet { get; set; }

        /// <summary>
        /// Conformance units the node belongs to — the NodeSet XML's &lt;Category&gt;
        /// elements. Blank entries are dropped; an empty list means none.
        /// </summary>
        public List<string>? Category { get; set; }

        /// <summary>
        /// Top-level Object/Variable only. Settable only at creation. Top-level
        /// Variables are always design-tool-only regardless of this value.
        /// </summary>
        public bool? DesignToolOnly { get; set; }
        public string? DataType { get; set; }
        public int? ValueRank { get; set; }
        public string? ArrayDimensions { get; set; }

        /// <summary>
        /// Optional: the instance declaration this child is being created from (what the
        /// Instantiate Children dialog offered). A Variable takes its default Value from
        /// that declaration; without it the value is resolved from the type hierarchy by
        /// BrowseName path.
        /// </summary>
        public string? SourceNodeId { get; set; }
    }

    public class UpdateNodeRequest
    {
        public string? BrowseName { get; set; }
        public string? BrowseNameModelUri { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public bool? IsAbstract { get; set; }

        /// <summary>ReferenceType only: the reference reads the same in both directions.</summary>
        public bool? Symmetric { get; set; }

        /// <summary>
        /// ReferenceType only: how the reference reads when browsed backwards. Blank
        /// clears it. Not allowed on a symmetric type — send it together with
        /// <see cref="Symmetric"/>=false to switch a symmetric type over in one call.
        /// </summary>
        public string? InverseName { get; set; }

        /// <summary>
        /// DataType only, and read-only here: an OptionSet is decided at creation.
        /// Sending the value the DataType already has is accepted (so a client can
        /// echo back the whole node); asking to change it is rejected.
        /// </summary>
        public bool? IsOptionSet { get; set; }

        /// <summary>
        /// Conformance units the node belongs to — the NodeSet XML's &lt;Category&gt;
        /// elements. Omit to leave them alone; send an empty list to clear them.
        /// </summary>
        public List<string>? Category { get; set; }

        public string? TypeDefinitionId { get; set; }
        public string? ModellingRuleId { get; set; }
        /// <summary>
        /// Optional: the hierarchical reference type connecting this node to its
        /// structural parent. When set (and different), the parent→child reference
        /// is retyped. Must be a subtype of HierarchicalReferences (i=33). Ignored
        /// for overrides/type hierarchy — see UpdateNode.
        /// </summary>
        public string? ReferenceTypeId { get; set; }
        public string? DataType { get; set; }
        public int? ValueRank { get; set; }
        public string? ArrayDimensions { get; set; }

        /// <summary>
        /// Variable value. Accepts a JSON element that will be stored as a Variant.
        /// For scalar values: a primitive (string, number, boolean).
        /// For arrays: a JSON array.
        /// </summary>
        public System.Text.Json.JsonElement? Value { get; set; }
    }

    public class CreateReferenceRequest
    {
        public string? ReferenceTypeId { get; set; }
        public string? TargetNodeId { get; set; }
        public bool? IsForward { get; set; }
    }

    public class InstantiateRequest
    {
        public string? ParentNodeId { get; set; }
        public string? ModelUri { get; set; }
        public string? BrowseName { get; set; }
        public string? DisplayName { get; set; }
        public string? ReferenceTypeId { get; set; }
        public string? ModellingRuleId { get; set; }
    }

    public class InstantiateChildrenRequest
    {
        /// <summary>
        /// The instance declaration the target node was created from. Preferred over
        /// <see cref="TypeNodeId"/>: it carries the children the owning type authored under
        /// the declaration, which the TypeDefinition alone does not know about.
        /// </summary>
        public string? SourceNodeId { get; set; }

        /// <summary>
        /// TypeDefinition to expand. Used when the target node was not created from a
        /// declaration (e.g. a free-standing instance).
        /// </summary>
        public string? TypeNodeId { get; set; }

        public string? ModelUri { get; set; }
    }

    public class AddInterfaceRequest
    {
        public string? InterfaceTypeNodeId { get; set; }
        public string? ModelUri { get; set; }
    }

    #endregion
}
