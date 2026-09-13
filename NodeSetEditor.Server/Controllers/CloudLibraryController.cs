using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Opc.Ua.CloudLibraryApi;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Controllers
{
    [ApiController]
    [ApiExplorerSettings(IgnoreApi = true)] // Not part of the published API surface.
    [Route("api/opcua/v1/cloudlibrary")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class CloudLibraryController : ControllerBase
    {
        private readonly CloudLibraryClient? _cloudLib;
        private readonly IConfiguration _config;
        private readonly INodeSetStorageService _storage;
        private readonly IWorkspaceAddressSpaceService _addressSpace;
        private readonly ILogger<CloudLibraryController> _logger;

        public CloudLibraryController(
            IConfiguration config,
            INodeSetStorageService storage,
            IWorkspaceAddressSpaceService addressSpace,
            ILogger<CloudLibraryController> logger,
            CloudLibraryClient? cloudLib = null)
        {
            _config = config;
            _storage = storage;
            _addressSpace = addressSpace;
            _logger = logger;
            _cloudLib = cloudLib;

            if (_cloudLib != null)
            {
                var clientId = _config["CloudLibraryClientId"];
                var clientSecret = _config["CloudLibraryClientSecret"];
                if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
                    _cloudLib.SetBasicAuth(clientId, clientSecret);
            }
        }

        /// <summary>
        /// Search the OPC UA Cloud Library. Returns metadata for matching models,
        /// deduplicated by namespace URI (latest version only).
        /// </summary>
        [HttpGet("search")]
        [AllowAnonymous] // Read-only search of the public Cloud Library index.
        public async Task<ActionResult<List<CloudLibraryModelInfo>>> Search(
            [FromQuery] string? keyword,
            [FromQuery] string? namespaceUri,
            [FromQuery] int offset = 0,
            [FromQuery] int limit = 100)
        {
            if (_cloudLib == null)
                return BadRequest(new { error = "Cloud Library not configured." });

            try
            {
                var keywords = string.IsNullOrWhiteSpace(keyword) ? null : new[] { keyword };

                // Parse exclusion filters from config
                var filtersCsv = _config["CloudLibraryModelFilters"];
                var filters = string.IsNullOrWhiteSpace(filtersCsv)
                    ? Array.Empty<string>()
                    : filtersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // The Cloud Library exposes the namespace URI either at the
                // top level (NodesetNamespaceUri) or nested under the Nodeset
                // object — try both so we don't lose results to whichever
                // shape happens to come back.
                static string? NsUri(UANodesetResult r) =>
                    r.Nodeset?.NamespaceUri ?? r.NodesetNamespaceUri;
                static string? NsVersion(UANodesetResult r) =>
                    r.Nodeset?.Version ?? r.Version;
                static DateTime? NsPubDate(UANodesetResult r) =>
                    r.Nodeset?.PublicationDate ?? r.PublicationDate;
                static string? NsTitle(UANodesetResult r) =>
                    r.Title ?? r.NodesetTitle;

                // The same namespace can be published by several submissions under DIFFERENT titles
                // (e.g. the IRDI dictionary appears both as "UA Part 19: Dictionary References" and,
                // bundled in the PADIM release with a HIGHER version, as "Process Automation Devices
                // - PADIM"). Dedup keeps the newest for content, but its title may belong to the
                // bundling spec — so track every title seen per URI and, when they conflict, show a
                // URI-derived name instead of mislabeling the namespace in the browse picker.
                var titlesByUri = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                // Filtering happens *after* the upstream fetch, so a single
                // upstream page may yield zero usable results. Page upstream
                // until either the dedup set is large enough to cover
                // offset+limit, or upstream is exhausted, or we hit the safety
                // cap. Dedup is done across all pages — keep the latest
                // version per namespace URI.
                const int upstreamPageSize = 100;
                const int maxUpstreamPages = 200; // ~20k raw items; safety cap

                // An exact namespace lookup must never answer "not published" just because the
                // entry sits past the first window — it drives the import picker and the
                // dependency flows, so it sweeps the whole catalog.
                var exactNamespaceLookup = !string.IsNullOrWhiteSpace(namespaceUri);

                var deduped = new Dictionary<string, UANodesetResult>(StringComparer.OrdinalIgnoreCase);
                int totalRaw = 0;
                int totalWithUri = 0;
                int totalAfterExclude = 0;
                int upstreamOffset = 0;
                int pagesFetched = 0;

                for (int page = 0; page < maxUpstreamPages; page++)
                {
                    // find2's namespaceUri parameter is not wired up on the Cloud Library side, and
                    // offset/limit are applied before any filtering, so passing it returned nothing
                    // for every namespace past the first window. Search the URI as a KEYWORD and
                    // keep only exact matches (see the namespaceUri filter in the loop below).
                    var batch = await _cloudLib.Find2Async(
                        exactNamespaceLookup ? new[] { namespaceUri! } : keywords,
                        null, upstreamOffset, upstreamPageSize);

                    if (batch == null) break;

                    pagesFetched++;
                    totalRaw += batch.Length;
                    upstreamOffset += upstreamPageSize;

                    foreach (var r in batch)
                    {
                        var uri = NsUri(r);
                        if (uri == null) continue;
                        if (namespaceUri != null
                            && !string.Equals(uri, namespaceUri, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        // The OPC UA standard core namespace is built-in and
                        // upgrades go through the DbTool console, never via
                        // the import dialog — drop it from the search results.
                        if (IsCoreNamespace(uri)) continue;
                        totalWithUri++;
                        if (filters.Any(f => uri.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                        totalAfterExclude++;

                        var title = NsTitle(r);
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            if (!titlesByUri.TryGetValue(uri, out var seenTitles))
                                titlesByUri[uri] = seenTitles = new HashSet<string>(StringComparer.Ordinal);
                            seenTitles.Add(title);
                        }

                        if (deduped.TryGetValue(uri, out var existing))
                        {
                            // Keep the latest version (best-effort version compare;
                            // fall back to publication date)
                            var rVer = Version.TryParse(NsVersion(r), out var v1) ? v1 : new Version();
                            var eVer = Version.TryParse(NsVersion(existing), out var v2) ? v2 : new Version();
                            int cmp = rVer.CompareTo(eVer);
                            if (cmp > 0
                                || (cmp == 0 && (NsPubDate(r) ?? DateTime.MinValue)
                                              > (NsPubDate(existing) ?? DateTime.MinValue)))
                            {
                                deduped[uri] = r;
                            }
                        }
                        else
                        {
                            deduped[uri] = r;
                        }
                    }

                    if (batch.Length == 0) break;

                    // Stop early once we have enough deduped items to cover
                    // the caller's offset + limit. Note: the caller's next
                    // page may still discover *more* deduped items, but every
                    // single request fetches enough to satisfy itself.
                    if (deduped.Count >= offset + limit) break;
                }

                // Stable order: by namespace URI so paging is deterministic
                // across requests.
                var orderedAll = deduped.Values
                    .OrderBy(r => NsUri(r), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var paged = orderedAll
                    .Skip(Math.Max(0, offset))
                    .Take(Math.Max(0, limit))
                    .Select(r =>
                    {
                        var info = ToModelInfo(r);
                        // Conflicting titles for this namespace → the submission title is unreliable;
                        // show a URI-derived name so the picker doesn't mislabel it.
                        if (info.NamespaceUri != null
                            && titlesByUri.TryGetValue(info.NamespaceUri, out var seenTitles)
                            && seenTitles.Count > 1)
                        {
                            info.Title = NamespaceNaming.DeriveFromUri(info.NamespaceUri);
                            info.Description = null;
                        }
                        return info;
                    })
                    .ToList();

                _logger.LogInformation(
                    "Cloud Library search offset={Offset} limit={Limit}: pages={Pages} raw={Raw} withUri={WithUri} afterExclude={AfterExclude} dedup={Dedup} returned={Returned}",
                    offset, limit, pagesFetched, totalRaw, totalWithUri, totalAfterExclude, deduped.Count, paged.Count);

                return Ok(paged);
            }
            catch (HttpRequestException e)
            {
                var reference = ErrorReference();
                _logger.LogError(e, "Cloud Library search failed (reference {Reference})", reference);
                return StatusCode(502, new { error = "Cloud Library request failed.", reference });
            }
        }

        /// <summary>
        /// Import a model from the Cloud Library into a workspace.
        /// Downloads the nodeset if not already in the DB, then links it to the workspace.
        /// </summary>
        [HttpPost("import/{identifier}")]
        public async Task<IActionResult> Import(
            string identifier,
            [FromHeader(Name = "OpcUa-Server")] string? opcUaServer)
        {
            try
            {
                var user = GetCurrentUser();
                if (!user.IsAuthenticated || user.UserId == null)
                    return Unauthorized();

                // Resolve workspace
                var workspaces = await _storage.GetWorkspacesAsync(user.UserId, user.Email);
                var workspace = ResolveWorkspace(workspaces, opcUaServer);
                if (workspace == null)
                    return NotFound(new { error = "Workspace not found." });

                // Import mutates the workspace (links/replaces models and
                // rewrites metadata), so it is owner-only — ACL collaborators
                // have read-only access.
                if (workspace.Owner != user.UserId)
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new { error = "This workspace is read-only. Only the owner can import models." });

                if (_cloudLib == null)
                    return BadRequest(new { error = "Cloud Library not configured." });

                var workspaceId = workspace.Id!.Value;

                // Download from Cloud Library
                var downloaded = await _cloudLib.DownloadAsync(identifier);
                if (downloaded?.Nodeset?.NodesetXml == null)
                    return NotFound(new { error = $"Model {identifier} not found or has no XML content." });

                var nsUri = downloaded.Nodeset.NamespaceUri;

                // The OPC UA standard core namespace is built-in / shared and
                // must NEVER be imported as a workspace model. If the cloud
                // library happens to expose it, treat the import as a no-op.
                // Otherwise the replace path would unlink the existing core,
                // and the subsequent upload would create a private workspace
                // copy of the core nodeset — both broken outcomes the user
                // reported (400 on first OK, then a private "core" appearing
                // after a retry).
                if (IsCoreNamespace(nsUri))
                {
                    _logger.LogInformation(
                        "Cloud Library import skipped: '{NsUri}' is the standard OPC UA core namespace (built-in)",
                        nsUri);
                    return Ok(new
                    {
                        modelUri = nsUri,
                        skipped = true,
                        reason = "core_namespace_built_in",
                    });
                }

                // If the workspace already has a model for this namespace URI,
                // either skip (incoming is same/older) or replace (incoming is
                // newer). The import dialog selects multiple models and a user
                // shouldn't get a 400 for a benign duplicate.
                var existingModels = await _storage.GetWorkspaceModelsAsync(workspaceId);
                var existingModel = existingModels.FirstOrDefault(m =>
                    m != null && string.Equals(m.ModelUri, nsUri, StringComparison.OrdinalIgnoreCase));

                if (existingModel != null)
                {
                    var newVer = downloaded.Nodeset.Version;
                    var newDate = downloaded.Nodeset.PublicationDate?.ToString("o");
                    var existingVer = existingModel.ModelVersion;
                    var existingDate = existingModel.PublicationDate;

                    // Canonical rule lives in NodeSetEditor.Server.Model.ModelInfo:
                    // major-upgrade not allowed, newer minor/patch wins, then
                    // publication date as tiebreaker.
                    if (!Model.ModelInfo.ShouldReplaceWithIncoming(newVer, newDate, existingVer, existingDate))
                    {
                        _logger.LogInformation(
                            "Cloud Library import skipped: '{NsUri}' existing v{ExistingVer}/{ExistingDate}, incoming v{NewVer}/{NewDate}",
                            nsUri, existingVer, existingDate, newVer, newDate);
                        return Ok(new
                        {
                            modelId = existingModel.Id,
                            modelUri = existingModel.ModelUri,
                            name = existingModel.Name,
                            version = existingModel.ModelVersion,
                            skipped = true,
                            reason = "not_an_upgrade",
                        });
                    }

                    _logger.LogInformation(
                        "Cloud Library import replacing: '{NsUri}' existing v{ExistingVer}/{ExistingDate} → incoming v{NewVer}/{NewDate}",
                        nsUri, existingVer, existingDate, newVer, newDate);

                    // Remove the existing workspace link so the new version can
                    // be linked in. The shared model row stays in the DB so
                    // other workspaces aren't affected.
                    await _addressSpace.RemoveModelAsync(workspaceId, nsUri!);
                }

                // Parse and upload into DB (handles dependency resolution)
                var xmlBytes = System.Text.Encoding.UTF8.GetBytes(downloaded.Nodeset.NodesetXml);
                using var ms = new MemoryStream(xmlBytes);
                var nodeSet = Opc.Ua.Export.UANodeSet.Read(ms);

                // License/copyright: prefer the file's own embedded SPDX headers (with OPC
                // Foundation defaults for foundation namespaces), then fall back to the Cloud
                // Library metadata that was previously ignored. Stamped read-only at genesis.
                var (resLicense, resLicenseUrl, resCopyright) =
                    NodeSetEditor.Model.SpdxHeaders.ResolveForImport(xmlBytes, nsUri);
                var cloudLicense = !string.IsNullOrWhiteSpace(resLicense) ? resLicense : downloaded.License?.Trim();
                var cloudCopyright = !string.IsNullOrWhiteSpace(resCopyright)
                    ? resCopyright
                    : NodeSetEditor.Model.SpdxHeaders.ExtractCopyrightHolder(downloaded.CopyrightText);

                // Cloud Library imports always land as a SHARED read-only link
                // in the workspace. A separate "create editable copy" feature
                // will fork them to a private model when the user wants to edit.
                var modelInfo = await _storage.UploadNodeSetAsync(workspaceId, nodeSet, isPrivate: false,
                    license: cloudLicense, licenseUrl: resLicenseUrl, copyrightHolder: cloudCopyright,
                    origin: NodeSetEditor.Model.ModelOrigin.CloudLibrary);

                // Update name/description from Cloud Library metadata — BUT if the CL publishes this
                // namespace under conflicting titles, downloaded.Title may belong to a bundling spec
                // (e.g. the IRDI dictionary re-published as "…PADIM"). In that case don't stamp it;
                // keep the URI-derived name UploadNodeSetAsync already set.
                var titleAmbiguous = await NamespaceTitlesConflictAsync(nsUri);
                var importTitle = titleAmbiguous ? null : downloaded.Title?.Trim();
                var importDesc = titleAmbiguous ? null : downloaded.Description?.Trim();
                if (!string.IsNullOrWhiteSpace(importTitle) || !string.IsNullOrWhiteSpace(importDesc))
                {
                    await _storage.UpdateModelInfoAsync(
                        workspaceId,
                        modelInfo.Id ?? Guid.Empty,
                        importTitle,
                        null,
                        importDesc);
                }

                _addressSpace.Invalidate(workspaceId);

                _logger.LogInformation(
                    "Imported Cloud Library model '{Title}' ({NsUri}) id={Identifier} into workspace {WorkspaceId}",
                    downloaded.Title, nsUri, identifier, workspaceId);

                return Ok(new
                {
                    modelId = modelInfo.Id,
                    modelUri = modelInfo.ModelUri,
                    name = importTitle ?? modelInfo.Name,
                    version = modelInfo.ModelVersion,
                });
            }
            catch (InvalidOperationException e)
            {
                return BadRequest(new { error = e.Message });
            }
            catch (HttpRequestException e)
            {
                var reference = ErrorReference();
                _logger.LogError(e, "Cloud Library download failed for identifier {Id} (reference {Reference})", identifier, reference);
                return StatusCode(502, new { error = "Cloud Library request failed.", reference });
            }
            catch (Exception e)
            {
                var reference = ErrorReference();
                _logger.LogError(e, "Error importing Cloud Library model {Id} (reference {Reference})", identifier, reference);
                return StatusCode(500, new { error = "An unexpected internal error occurred.", reference });
            }
        }

        /// <summary>
        /// True when the Cloud Library publishes the given namespace under more than one distinct
        /// title — meaning any single submission's title (e.g. from a by-identifier download) is an
        /// unreliable display name for the namespace. Best-effort: returns false on any failure.
        /// </summary>
        private async Task<bool> NamespaceTitlesConflictAsync(string? namespaceUri)
        {
            if (_cloudLib == null || string.IsNullOrWhiteSpace(namespaceUri)) return false;
            try
            {
                var results = await _cloudLib.Find2Async(namespaceUri: namespaceUri, limit: 1000);
                if (results == null) return false;
                var distinctTitles = results
                    .Where(r => string.Equals(
                        r.Nodeset?.NamespaceUri ?? r.NodesetNamespaceUri, namespaceUri, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Title ?? r.NodesetTitle)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                return distinctTitles > 1;
            }
            catch
            {
                return false;
            }
        }

        private static CloudLibraryModelInfo ToModelInfo(UANodesetResult ns)
        {
            // Cloud Library returns namespace metadata either inline under
            // Nodeset or at the top level (NodesetNamespaceUri / Version /
            // PublicationDate). Coalesce so callers always get a value.
            return new CloudLibraryModelInfo
            {
                Identifier = ns.Nodeset?.Identifier ?? ns.NodesetId,
                Title = ns.Title ?? ns.NodesetTitle,
                Description = ns.Description,
                NamespaceUri = ns.Nodeset?.NamespaceUri ?? ns.NodesetNamespaceUri,
                Version = ns.Nodeset?.Version ?? ns.Version,
                PublicationDate = ns.Nodeset?.PublicationDate ?? ns.PublicationDate,
                License = ns.License,
                CopyrightText = ns.CopyrightText,
                DocumentationUrl = ns.DocumentationUrl,
                IconUrl = ns.IconUrl,
                Keywords = ns.Keywords,
                RequiredModels = (ns.Nodeset?.RequiredModels ?? ns.RequiredNodesets)?
                    .Select(r => new CloudLibraryRequiredModel
                    {
                        NamespaceUri = r.NamespaceUri,
                        Version = r.Version,
                        PublicationDate = r.PublicationDate,
                    }).ToArray(),
                NumberOfDownloads = ns.NumberOfDownloads,
            };
        }

        private Services.AuthenticatedUser GetCurrentUser()
        {
            return Services.AuthenticatedUser.FromClaimsPrincipal(HttpContext.User);
        }

        // Correlation id returned to the client on errors; recorded by Application
        // Insights as operation_Id so support can find the logged exception via:
        // union exceptions,traces | where operation_Id == "<reference>"
        private string ErrorReference() =>
            System.Diagnostics.Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier;

        private const string CoreNamespace = "http://opcfoundation.org/UA/";

        /// <summary>
        /// True when the given URI refers to the OPC UA standard core
        /// namespace ("http://opcfoundation.org/UA/"). Trailing slash and
        /// case insensitive — the cloud library has been seen with both
        /// representations.
        /// </summary>
        private static bool IsCoreNamespace(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            var normalized = uri.TrimEnd('/');
            var core = CoreNamespace.TrimEnd('/');
            return string.Equals(normalized, core, StringComparison.OrdinalIgnoreCase);
        }

        private static NodeSetEditor.Server.Model.Workspace? ResolveWorkspace(
            List<NodeSetEditor.Server.Model.Workspace> workspaces, string? opcUaServer)
        {
            if (string.IsNullOrEmpty(opcUaServer))
                return workspaces.FirstOrDefault();

            var id = Opc.Ua.RestfulApi.UrnUtils.ParseUrn(opcUaServer);
            return id.HasValue
                ? workspaces.FirstOrDefault(w => w.Id == id.Value)
                : null;
        }
    }

    public class CloudLibraryModelInfo
    {
        public string? Identifier { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? NamespaceUri { get; set; }
        public string? Version { get; set; }
        public DateTime? PublicationDate { get; set; }
        public string? License { get; set; }
        public string? CopyrightText { get; set; }
        public string? DocumentationUrl { get; set; }
        public string? IconUrl { get; set; }
        public string[]? Keywords { get; set; }
        public CloudLibraryRequiredModel[]? RequiredModels { get; set; }
        public int? NumberOfDownloads { get; set; }
    }

    public class CloudLibraryRequiredModel
    {
        public string? NamespaceUri { get; set; }
        public string? Version { get; set; }
        public DateTime? PublicationDate { get; set; }
    }
}
