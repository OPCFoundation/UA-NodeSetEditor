extern alias JsonNodeSet;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Server.Model;
using Opc.Ua.Export;
using NodeClass = NodeSetEditor.Server.Model.NodeClass;
using DbWorkspace = NodeSetEditor.Model.Workspace;
using DbModel = NodeSetEditor.Model.Model;

namespace NodeSetEditor.Server.Services
{
    public class DbNodeSetStorageService : INodeSetStorageService
    {
        private const string UaCoreNamespace = "http://opcfoundation.org/UA/";

        private readonly NodeSetEditor.Model.NodeSetEditorDbContext _db;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<DbNodeSetStorageService> _logger;
        private readonly IConfiguration _config;
        private readonly Opc.Ua.CloudLibraryApi.CloudLibraryClient? _cloudLib;

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public DbNodeSetStorageService(
            NodeSetEditor.Model.NodeSetEditorDbContext db,
            IWebHostEnvironment environment,
            ILogger<DbNodeSetStorageService> logger,
            IConfiguration config,
            Opc.Ua.CloudLibraryApi.CloudLibraryClient? cloudLib = null)
        {
            _db = db;
            _environment = environment;
            _logger = logger;
            _config = config;
            _cloudLib = cloudLib;

            if (_cloudLib != null)
            {
                var clientId = _config["CloudLibraryClientId"];
                var clientSecret = _config["CloudLibraryClientSecret"];
                if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
                    _cloudLib.SetBasicAuth(clientId, clientSecret);
            }
        }

        #region User-Scoped Workspace Operations

        public async Task<List<Workspace>> GetWorkspacesAsync(string userId, string? userEmail)
        {
            var emailLower = userEmail?.ToLowerInvariant();

            var workspaces = await _db.Workspaces
                .Include(w => w.Acl)
                .Include(w => w.Models)
                .Where(w =>
                    w.OwnerUserId == userId ||                                       // owner
                    (emailLower != null && w.Acl!.Any(a => a.Email == emailLower)))  // ACL
                .OrderByDescending(w => w.CreatedAt)
                .ToListAsync();

            if (workspaces.Count == 0)
            {
                // Auto-create a Sandbox workspace for new users.
                // "Sandbox" was chosen over "Default" after user feedback that the latter
                // wasn't intuitive at the top of the workspace tree — Sandbox communicates
                // "personal experimental workspace" and is the convention used by other
                // cloud / dev tools.
                var ws = await CreateWorkspaceInternalAsync(userId, userEmail, "Sandbox", null, null);
                workspaces.Add(ws);
            }

            return workspaces.Select(ToDto).ToList();
        }

        public async Task<Workspace> CreateWorkspaceAsync(string userId, string? ownerEmail, string name, string? description, List<string>? acl = null)
        {
            // Enforce name uniqueness within the user's workspaces
            var exists = await _db.Workspaces.AnyAsync(w =>
                w.OwnerUserId == userId && w.Name == name);

            if (exists)
                throw new InvalidOperationException($"A workspace named '{name}' already exists.");

            var ws = await CreateWorkspaceInternalAsync(userId, ownerEmail, name, description, acl);
            return ToDto(ws);
        }

        public async Task DeleteWorkspaceAsync(string userId, Guid workspaceId)
        {
            var ws = await _db.Workspaces.FindAsync(workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            if (string.Equals(ws.Name, "Sandbox", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Sandbox workspace cannot be deleted.");

            if (ws.OwnerUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to delete this workspace.");

            _db.Workspaces.Remove(ws);
            await _db.SaveChangesAsync();
        }

        #endregion

        #region Id-Scoped Workspace Operations (caller must have already authorized access)

        public async Task<Workspace?> GetWorkspaceAsync(Guid workspaceId)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Acl)
                .Include(w => w.Models)
                .FirstOrDefaultAsync(w => w.Id == workspaceId);

            return ws == null ? null : ToDto(ws);
        }

        public async Task<Workspace> UpdateWorkspaceAsync(Guid workspaceId, string? name, string? description, List<string>? acl = null)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Acl)
                .Include(w => w.Models)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            if (name != null && !string.Equals(name, ws.Name, StringComparison.OrdinalIgnoreCase))
            {
                var exists = await _db.Workspaces.AnyAsync(w =>
                    w.Id != workspaceId && w.OwnerUserId == ws.OwnerUserId && w.Name == name);
                if (exists)
                    throw new InvalidOperationException($"A workspace named '{name}' already exists.");
            }

            ws.Name = name ?? ws.Name;
            ws.Description = description ?? ws.Description;
            ws.ModifiedAt = DateTime.UtcNow;

            if (acl != null)
            {
                // Replace ACL
                _db.WorkspaceAcls.RemoveRange(ws.Acl ?? new List<NodeSetEditor.Model.WorkspaceAcl>());
                ws.Acl = acl.Select(e => new NodeSetEditor.Model.WorkspaceAcl
                {
                    WorkspaceId = workspaceId,
                    Email = e.ToLowerInvariant()
                }).DistinctBy(a => a.Email).ToList();
            }

            await _db.SaveChangesAsync();
            return ToDto(ws);
        }

        #endregion

        #region ACL

        public async Task<List<string>> GetWorkspaceAclAsync(string userId, Guid workspaceId)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Acl)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            if (ws.OwnerUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to view this workspace's ACL.");

            return ws.Acl?.Select(a => a.Email).ToList() ?? new List<string>();
        }

        public async Task SetWorkspaceAclAsync(string userId, Guid workspaceId, List<string> emails)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Acl)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            if (ws.OwnerUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to modify this workspace's ACL.");

            _db.WorkspaceAcls.RemoveRange(ws.Acl ?? new List<NodeSetEditor.Model.WorkspaceAcl>());
            ws.Acl = emails.Select(e => new NodeSetEditor.Model.WorkspaceAcl
            {
                WorkspaceId = workspaceId,
                Email = e.ToLowerInvariant()
            }).DistinctBy(a => a.Email).ToList();

            await _db.SaveChangesAsync();
        }

        #endregion

        #region Model Management

        public async Task<List<ModelInfo>> GetSharedModelsAsync()
        {
            // Return models that have at least one non-private workspace reference,
            // OR are not referenced by any workspace at all (orphaned/imported models).
            // Excludes models that are ONLY private to specific workspaces.
            var models = await _db.Models
                .Where(m => !m.Workspaces!.Any() || m.Workspaces!.Any(wm => !wm.IsPrivate))
                .Select(m => new ModelInfo
                {
                    Id = m.Id,
                    ModelUri = m.Uri,
                    Name = m.Name,
                    Description = m.Description,
                    ModelVersion = m.Version,
                    PublicationDate = m.PublicationDate,
                    HasErrors = m.HasErrors,
                    License = m.License,
                    LicenseUrl = m.LicenseUrl,
                    CopyrightHolder = m.CopyrightHolder
                })
                .ToListAsync();

            return models;
        }

        public async Task<List<ModelInfo>> GetPublishedModelsAsync()
        {
            // Models explicitly published (check-in "publish"), available for other
            // users/workspaces to link. Published is a property of the model version,
            // independent of any workspace link's IsPrivate visibility.
            var rows = await _db.Models
                .Where(m => m.Published)
                .Select(m => new
                {
                    Info = new ModelInfo
                    {
                        Id = m.Id,
                        ModelUri = m.Uri,
                        Name = m.Name,
                        Description = m.Description,
                        ModelVersion = m.Version,
                        PublicationDate = m.PublicationDate,
                        HasErrors = m.HasErrors,
                        Creator = m.Creator,
                        License = m.License,
                        LicenseUrl = m.LicenseUrl,
                        CopyrightHolder = m.CopyrightHolder
                    },
                    m.CreatorUserId
                })
                .ToListAsync();

            // Resolve the publisher's current display Name (falls back to the
            // email-local-part snapshot stored in Creator when unavailable).
            var names = await GetUserDisplayNamesAsync(
                rows.Where(r => r.CreatorUserId != null).Select(r => r.CreatorUserId!));
            foreach (var r in rows)
            {
                if (r.CreatorUserId != null && names.TryGetValue(r.CreatorUserId, out var name))
                    r.Info.Creator = name;
            }

            return rows.Select(r => r.Info).ToList();
        }

        public async Task<bool> IsModelLinkableAsync(Guid modelId)
        {
            // A model that exists in the DB is linkable by anyone only when it is shared:
            // explicitly published, OR a Cloud Library copy (the trusted global cache of a
            // published namespace). Anything else — a user's authored model or uploaded file —
            // belongs to its own workspace, no matter how its links happen to be flagged.
            //
            // NOTE: link flags deliberately do NOT confer shareability. Treating "has a
            // non-private link" as shared let one user's model become authoritative for a
            // namespace and shadow the Cloud Library for everyone else.
            var dbModel = await _db.Models
                .Where(m => m.Id == modelId)
                .Select(m => new { m.Published, m.Origin })
                .FirstOrDefaultAsync();

            if (dbModel != null)
                return dbModel.Published || dbModel.Origin == NodeSetEditor.Model.ModelOrigin.CloudLibrary;

            // Not a DB row — permit only a genuine Cloud Library catalog GUID (a deterministic
            // hash of a public catalog identifier), which the link flow imports on demand. A
            // private model's id is a random GUID and will not resolve here.
            return await ResolveCloudLibraryIdAsync(modelId) != null;
        }

        public async Task<List<ModelInfo?>> GetWorkspaceModelsAsync(Guid workspaceId)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Models!)
                    .ThenInclude(wm => wm.Model)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            var results = new List<ModelInfo?>();

            if (ws.Models == null || ws.Models.Count == 0)
                return results;

            // Collapse to one row per URI. A workspace can hold two links for the same URI during
            // a checkout (the retained good version plus the editable -alpha working copy); show
            // only the active one — prefer the editable working copy, then the highest VersionNorm.
            var chosenIds = ws.Models
                .Where(wm => wm.Model != null)
                .GroupBy(wm => wm.Model!.Uri)
                .Select(g => g
                    .OrderByDescending(wm => wm.IsEditable)
                    .ThenByDescending(wm => wm.Model!.VersionNorm, StringComparer.Ordinal)
                    .First().ModelId)
                .ToHashSet();

            foreach (var wm in ws.Models)
            {
                var m = wm.Model;
                if (m == null || !chosenIds.Contains(wm.ModelId)) continue;

                results.Add(ToModelInfo(m));
            }

            return results;
        }

        public async Task<List<ModelVersionInfo>> GetModelVersionsAsync(Guid workspaceId, Guid modelId)
        {
            var uri = await _db.WorkspaceModels
                .Where(wm => wm.WorkspaceId == workspaceId && wm.ModelId == modelId)
                .Select(wm => wm.Model!.Uri)
                .FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException($"Model '{modelId}' is not in workspace '{workspaceId}'.");

            // Everything this workspace holds for the URI (the version in use, the backup a
            // checkout retained, any shared links) plus every published release of it — the
            // published ones are visible to everyone, so they belong in the list even when
            // this workspace has not linked them. Other workspaces' private rows are not
            // visible here and are deliberately left out.
            var links = await _db.WorkspaceModels
                .Include(wm => wm.Model)
                .Where(wm => wm.WorkspaceId == workspaceId && wm.Model!.Uri == uri)
                .ToListAsync();

            var models = links.Select(l => l.Model!).ToList();
            var linkedIds = models.Select(m => m.Id).ToHashSet();

            var published = await _db.Models
                .Where(m => m.Uri == uri && m.Published && !linkedIds.Contains(m.Id))
                .ToListAsync();
            models.AddRange(published);

            // Same collapse rule the workspace model list uses, so "current" here means the
            // version the rest of the app is actually serving for this URI.
            var currentId = links
                .OrderByDescending(wm => wm.IsEditable)
                .ThenByDescending(wm => wm.Model!.VersionNorm, StringComparer.Ordinal)
                .Select(wm => wm.ModelId)
                .FirstOrDefault();

            // One query for the whole set rather than one per version.
            var ids = models.Select(m => m.Id).ToList();
            var otherCounts = (await _db.WorkspaceModels
                .Where(wm => ids.Contains(wm.ModelId) && wm.WorkspaceId != workspaceId)
                .GroupBy(wm => wm.ModelId)
                .Select(g => new { ModelId = g.Key, Count = g.Count() })
                .ToListAsync())
                .ToDictionary(x => x.ModelId, x => x.Count);

            var results = new List<ModelVersionInfo>();
            foreach (var m in models)
            {
                var link = links.FirstOrDefault(l => l.ModelId == m.Id);
                var otherCount = otherCounts.TryGetValue(m.Id, out var c) ? c : 0;

                var info = new ModelVersionInfo
                {
                    Id = m.Id,
                    Version = m.Version,
                    PublicationDate = m.PublicationDate,
                    Origin = m.Origin.ToString(),
                    IsPrivate = link?.IsPrivate == true,
                    IsPublished = m.Published,
                    IsCurrent = m.Id == currentId,
                    IsEditable = link?.IsEditable == true,
                    OtherWorkspaceCount = otherCount,
                };
                info.BlockedReason = DescribeUndeletableVersion(info, link != null);
                info.CanDelete = info.BlockedReason == null;
                results.Add(info);
            }

            // Newest first, matching how versions read everywhere else.
            return results
                .OrderByDescending(r => DbModel.NormalizeVersion(r.Version), StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The single rule for whether a stored version may be deleted from a workspace, stated
        /// as the reason it may NOT be — null means it can. Used both to annotate the list and
        /// to enforce the delete, so the button and the endpoint can never disagree.
        /// </summary>
        private static string? DescribeUndeletableVersion(ModelVersionInfo info, bool isLinkedHere)
        {
            if (!isLinkedHere)
                return "This version is not in this workspace.";
            if (info.IsCurrent)
                return "This is the version the workspace is using.";
            if (!info.IsPrivate || info.IsPublished)
                return "Published and shared versions cannot be deleted.";
            if (info.OtherWorkspaceCount > 0)
                return "Another workspace is using this version.";
            return null;
        }

        public async Task DeleteModelVersionAsync(Guid workspaceId, Guid modelId, Guid versionId)
        {
            // Re-derive the state rather than trusting the caller's view of it: the list the
            // user clicked in may be stale (another workspace could have linked the version
            // since, or a check-in could have made it the current one).
            var versions = await GetModelVersionsAsync(workspaceId, modelId);
            var target = versions.FirstOrDefault(v => v.Id == versionId)
                ?? throw new KeyNotFoundException($"Version '{versionId}' not found for this model.");

            if (!target.CanDelete)
                throw new InvalidOperationException(target.BlockedReason ?? "This version cannot be deleted.");

            var link = await _db.WorkspaceModels
                .FirstOrDefaultAsync(wm => wm.WorkspaceId == workspaceId && wm.ModelId == versionId)
                ?? throw new KeyNotFoundException($"Version '{versionId}' is not in workspace '{workspaceId}'.");

            _db.WorkspaceModels.Remove(link);
            await _db.SaveChangesAsync();

            // Unlinking is not enough — the row and its node graph would linger unreachable.
            // DeleteModelIfOrphanedAsync re-checks that nothing else references it.
            await DeleteModelIfOrphanedAsync(versionId);
        }

        public async Task DeleteModelIfOrphanedAsync(Guid modelId)
        {
            var model = await _db.Models.FindAsync(modelId);
            if (model == null) return;

            // Published versions live in the shared catalog and may be (re)linked by other
            // workspaces even when no link currently exists — never delete them here.
            if (model.Published) return;

            // Still referenced by a workspace? Then it isn't orphaned.
            if (await _db.WorkspaceModels.AnyAsync(wm => wm.ModelId == modelId)) return;

            // Unreachable private working copy — drop it and its node graph.
            await _db.References.Where(r => r.ModelId == modelId).ExecuteDeleteAsync();
            await _db.Nodes.Where(n => n.ModelId == modelId).ExecuteDeleteAsync();
            _db.Models.Remove(model);
            await _db.SaveChangesAsync();
        }

        public async Task<Workspace> UpdateWorkspaceModelsAsync(Guid workspaceId, List<ModelReference> models)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Models)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            // Replace junction table entries
            _db.WorkspaceModels.RemoveRange(ws.Models ?? new List<NodeSetEditor.Model.WorkspaceModel>());

            // Carry BOTH link flags through. This rebuilds the junction rows for the
            // whole workspace (link/unlink flows), so dropping IsEditable here would
            // silently lock every checked-out working copy in the workspace.
            ws.Models = models.Select(mr => new NodeSetEditor.Model.WorkspaceModel
            {
                WorkspaceId = workspaceId,
                ModelId = mr.Id,
                IsPrivate = mr.IsPrivate,
                IsEditable = mr.IsEditable
            }).ToList();

            ws.ModifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return ToDto(ws);
        }

        #endregion

        #region Checkout / Checkin

        public async Task<ModelInfo> CheckoutModelAsync(Guid workspaceId, Guid modelId)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Models!)
                    .ThenInclude(wm => wm.Model)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            var link = ws.Models?.FirstOrDefault(wm => wm.ModelId == modelId)
                ?? throw new KeyNotFoundException($"Model '{modelId}' is not in workspace '{workspaceId}'.");
            var source = link.Model
                ?? throw new KeyNotFoundException($"Model with Id '{modelId}' not found.");

            // Already an editable working copy — nothing to do.
            if (link.IsEditable)
                return ToModelInfo(source);

            // ALWAYS mint a new -alpha working copy with the patch incremented (export the source
            // and re-import it under the bumped version), even when the source is itself a kept
            // private checkpoint. The source link is left untouched so it becomes the backup a later
            // Discard restores; the display layer hides it while the working copy exists.
            var newVersion = DbModel.BumpForCheckout(source.Version);
            var nodeSet = await NodeSetEditor.Model.NodeSetConverter.CreateNodeSetAsync(_db, source.Uri!, source.Version);
            if (nodeSet.Models is { Length: > 0 })
            {
                // Set BOTH the freeform Version and the SemVer ModelVersion. StoreNodeSetAsync
                // derives VersionNorm from (ModelVersion ?? Version); if ModelVersion still held the
                // source's value, the clone would share the source's VersionNorm and the unique
                // (Uri, VersionNorm) dedup would overwrite the source row's content in place
                // instead of adding a new row — destroying the backup we need for Discard.
                nodeSet.Models[0].Version = newVersion;
                nodeSet.Models[0].ModelVersion = newVersion;
                // A newly minted version is published "now" — don't inherit the source's date.
                nodeSet.Models[0].PublicationDate = DateTime.UtcNow;
                nodeSet.Models[0].PublicationDateSpecified = true;
            }

            var clone = await NodeSetEditor.Model.NodeSetConverter.StoreNodeSetAsync(_db, nodeSet);
            // A working copy is editor content from here on, even when checked out from a
            // Cloud Library import — it must not inherit the trusted CloudLibrary provenance
            // and start satisfying other workspaces' dependency lookups for this namespace.
            clone.Origin = NodeSetEditor.Model.ModelOrigin.Authored;
            clone.Name = source.Name;
            clone.Description = source.Description;
            // License/copyright are immutable and follow the model across checkout — an editor
            // working on a checked-out copy cannot relicense it.
            clone.License = source.License;
            clone.LicenseUrl = source.LicenseUrl;
            clone.CopyrightHolder = source.CopyrightHolder;
            await _db.SaveChangesAsync();

            // Refresh the working copy's NamespaceMetadata to the new version / publication date.
            await TrySyncNamespaceMetadataAsync(clone);

            ws.Models ??= new List<NodeSetEditor.Model.WorkspaceModel>();
            ws.Models.Add(new NodeSetEditor.Model.WorkspaceModel
            {
                WorkspaceId = workspaceId,
                ModelId = clone.Id,
                Model = clone,
                IsPrivate = true,
                IsEditable = true
            });
            ws.ModifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            // Keep at most two PRIVATE versions of this URI (the working copy + one backup) so
            // repeated checkout/check-in cycles don't pile up old private checkpoints. Shared
            // versions are never pruned.
            await PrunePrivateVersionsAsync(workspaceId, clone.Uri!, keep: 2);

            var uriLinkCount = await _db.WorkspaceModels
                .CountAsync(wm => wm.WorkspaceId == workspaceId && wm.Model!.Uri == clone.Uri);
            _logger.LogInformation(
                "Checkout '{Uri}': {SourceVer} (private={SrcPriv}) → {NewVer}; {Count} workspace link(s) for this URI",
                clone.Uri, source.Version, link.IsPrivate, newVersion, uriLinkCount);

            return ToModelInfo(clone);
        }

        /// <summary>
        /// Trim the workspace's PRIVATE versions of <paramref name="uri"/> to the newest
        /// <paramref name="keep"/> (by VersionNorm), deleting older private working copies/backups.
        /// Shared links are left untouched, and a model row is removed only when no workspace
        /// still references it.
        /// </summary>
        private async Task PrunePrivateVersionsAsync(Guid workspaceId, string uri, int keep)
        {
            var privateLinks = await _db.WorkspaceModels
                .Include(wm => wm.Model)
                .Where(wm => wm.WorkspaceId == workspaceId && wm.IsPrivate && wm.Model!.Uri == uri)
                .OrderByDescending(wm => wm.Model!.VersionNorm)
                .ToListAsync();

            var stale = privateLinks.Skip(keep).ToList();
            if (stale.Count == 0) return;

            foreach (var s in stale)
                _db.WorkspaceModels.Remove(s);
            await _db.SaveChangesAsync();

            foreach (var s in stale)
            {
                var referenced = await _db.WorkspaceModels.AnyAsync(wm => wm.ModelId == s.ModelId);
                if (!referenced)
                {
                    var orphan = await _db.Models.FindAsync(s.ModelId);
                    if (orphan != null) _db.Models.Remove(orphan);
                }
            }
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Create or refresh the model's NamespaceMetadata object/properties. Best-effort: a failure
        /// here must not break the create/checkout/publish/edit operation that triggered it.
        /// </summary>
        private async Task TrySyncNamespaceMetadataAsync(DbModel model)
        {
            try
            {
                await NodeSetEditor.Model.NodeSetConverter.SyncNamespaceMetadataAsync(_db, model);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync NamespaceMetadata for model '{Uri}' {Version}",
                    model.Uri, model.Version);
            }
        }

        public async Task<ModelInfo?> CheckinModelAsync(Guid workspaceId, Guid modelId, string action,
            string? version = null, string? description = null, string? creator = null,
            string? creatorUserId = null)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Models!)
                    .ThenInclude(wm => wm.Model)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            var link = ws.Models?.FirstOrDefault(wm => wm.ModelId == modelId)
                ?? throw new KeyNotFoundException($"Model '{modelId}' is not in workspace '{workspaceId}'.");
            var model = link.Model
                ?? throw new KeyNotFoundException($"Model with Id '{modelId}' not found.");

            switch (action?.Trim().ToLowerInvariant())
            {
                case "keep":
                {
                    // Lock the working copy in place — no visibility change, but the version
                    // does settle: -alpha means "checked out for editing", and this model is
                    // not being edited any more. The caller may name the version instead (the
                    // dialog offers the stripped one, editable); either way the working-copy
                    // suffix comes off. A model kept at an unchanged version is left alone so
                    // repeated keeps are a no-op.
                    var keptVersion = DbModel.StripWorkingSuffix(
                        string.IsNullOrWhiteSpace(version) ? model.Version : version);

                    if (!string.Equals(keptVersion, model.Version, StringComparison.Ordinal))
                    {
                        // Rows are deduped on (Uri, VersionNorm), so landing on a version another
                        // row of this URI already holds would collide with it — including the
                        // backup retained at checkout, which Discard still needs.
                        var keptNorm = DbModel.NormalizeVersion(keptVersion);
                        var keptTaken = await _db.Models.AnyAsync(m =>
                            m.Id != model.Id && m.Uri == model.Uri && m.VersionNorm == keptNorm);
                        if (keptTaken)
                            throw new ModelVersionConflictException(
                                $"Version '{keptVersion}' already exists for '{model.Uri}'.");

                        model.Version = keptVersion;
                        model.SetVersionNorm(keptVersion);
                    }

                    link.IsEditable = false;
                    ws.ModifiedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    // The version lives in the model's own NamespaceMetadata node too.
                    await TrySyncNamespaceMetadataAsync(model);
                    return ToModelInfo(model);
                }

                case "publish":
                {
                    // A description is mandatory to publish.
                    if (string.IsNullOrWhiteSpace(description))
                        throw new InvalidOperationException("A description is required to publish a model.");

                    // Publishing is what makes a model visible to OTHER users, so it is the
                    // point where a local edit could start standing in for a namespace the UA
                    // Cloud Library already publishes. Editing such a namespace is fine — it
                    // just stays in the editor's own workspace (add it, then check it out) or
                    // goes back to the Cloud Library. It never becomes the shared answer here.
                    await GuardCloudLibraryNamespaceAsync(model.Uri);

                    // Version: use the publisher's chosen version when supplied (guarding against a
                    // collision with another published version of the same URI), otherwise strip the
                    // working copy's -alpha to a published -beta. If another user already published
                    // this major.minor.patch, bump the pre-release counter (-beta.1, -beta.2, …) so
                    // the last check-in wins — the highest counter is the newest published version.
                    string newVersion;
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        newVersion = version.Trim();
                        var norm = DbModel.NormalizeVersion(newVersion);
                        var taken = await _db.Models.AnyAsync(m =>
                            m.Id != model.Id && m.Uri == model.Uri && m.VersionNorm == norm);
                        if (taken)
                            throw new ModelVersionConflictException(
                                $"Version '{newVersion}' is already published for '{model.Uri}'.");
                    }
                    else
                    {
                        newVersion = await ComputeNextPublishedVersionAsync(model.Uri, model.Version, model.Id);
                    }
                    model.Version = newVersion;
                    model.SetVersionNorm(newVersion);
                    // Publishing mints a new version — stamp it with the current publication date.
                    model.PublicationDate = DateTime.UtcNow.ToString("o");
                    // Persist the publisher-supplied description and creator. CreatorUserId
                    // lets the shared-model picker resolve the publisher's current display
                    // Name; Creator (email local part) is the fallback snapshot.
                    model.Description = description.Trim();
                    model.Creator = creator;
                    model.CreatorUserId = creatorUserId;
                    // Mark the model version as published so other users/workspaces can link it.
                    model.Published = true;
                    // Make public and lock.
                    link.IsPrivate = false;
                    link.IsEditable = false;
                    ws.ModifiedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    await TrySyncNamespaceMetadataAsync(model);
                    return ToModelInfo(model);
                }

                case "discard":
                {
                    // Discard must never remove the URI from the workspace. Query the DB directly
                    // for another link to the same URI (the backup retained at checkout — a private
                    // checkpoint OR a shared release) rather than trusting the in-memory navigation,
                    // which can omit links added in an earlier request.
                    var hasRetainedVersion = await _db.WorkspaceModels
                        .AnyAsync(wm => wm.WorkspaceId == workspaceId
                            && wm.ModelId != model.Id
                            && wm.Model!.Uri == model.Uri);

                    _logger.LogInformation(
                        "Discard '{Uri}' {Version}: hasRetainedVersion={Retained} (true=restore previous, false=reset empty)",
                        model.Uri, model.Version, hasRetainedVersion);

                    if (hasRetainedVersion)
                    {
                        // Existing model: drop the working copy; the previous version (a private
                        // backup, or a shared release) was never removed and reappears now.
                        _db.WorkspaceModels.Remove(link);
                        await _db.SaveChangesAsync();

                        var stillReferenced = await _db.WorkspaceModels.AnyAsync(wm => wm.ModelId == model.Id);
                        if (!stillReferenced)
                            _db.Models.Remove(model);

                        ws.ModifiedAt = DateTime.UtcNow;
                        await _db.SaveChangesAsync();
                        return null;
                    }

                    // Brand-new model with no prior version: delete the working copy and re-create
                    // it empty at the SAME version so the model stays in the workspace.
                    return await ResetModelToEmptyAsync(ws, link, model);
                }

                default:
                    throw new InvalidOperationException(
                        $"Unknown check-in action '{action}'. Expected keep, publish, or discard.");
            }
        }

        /// <summary>
        /// Delete a working-copy model that has no prior version and re-create it empty at the same
        /// URI/version (the initial created state), linked private and locked. Used by Discard for a
        /// brand-new model so the model stays in the workspace instead of disappearing.
        /// </summary>
        private async Task<ModelInfo?> ResetModelToEmptyAsync(
            DbWorkspace ws, NodeSetEditor.Model.WorkspaceModel link, DbModel model)
        {
            var uri = model.Uri!;
            var version = model.Version;
            var name = model.Name;
            var pubDate = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(model.PublicationDate) && DateTime.TryParse(model.PublicationDate, out var pd))
                pubDate = pd;

            // Remove the working copy entirely (cascade clears its nodes/references/types).
            _db.WorkspaceModels.Remove(link);
            ws.Models!.Remove(link);
            await _db.SaveChangesAsync();
            var stillReferenced = await _db.WorkspaceModels.AnyAsync(wm => wm.ModelId == model.Id);
            if (!stillReferenced)
            {
                _db.Models.Remove(model);
                await _db.SaveChangesAsync();
            }

            // Re-create an empty model at the same URI and version.
            var emptyNodeSet = new UANodeSet
            {
                NamespaceUris = new[] { uri },
                Models = new[]
                {
                    new ModelTableEntry
                    {
                        ModelUri = uri,
                        Version = version,
                        PublicationDate = pubDate,
                        PublicationDateSpecified = true,
                        RequiredModel = new[]
                        {
                            new ModelTableEntry
                            {
                                ModelUri = UaCoreNamespace,
                                PublicationDate = DateTime.MinValue,
                                PublicationDateSpecified = false
                            }
                        }
                    }
                }
            };

            var empty = await NodeSetEditor.Model.NodeSetConverter.StoreNodeSetAsync(_db, emptyNodeSet);
            empty.Origin = NodeSetEditor.Model.ModelOrigin.Authored;
            empty.Name = name;
            await _db.SaveChangesAsync();

            ws.Models!.Add(new NodeSetEditor.Model.WorkspaceModel
            {
                WorkspaceId = ws.Id,
                ModelId = empty.Id,
                Model = empty,
                IsPrivate = true,
                IsEditable = false
            });
            ws.ModifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return ToModelInfo(empty);
        }

        /// <summary>
        /// Compute the next free published version for a model URI. Publishing strips the
        /// working copy's <c>-alpha</c> suffix to <c>M.m.p-beta</c>; if another user already
        /// published that exact version (concurrent check-in of the same model), the pre-release
        /// counter is bumped — <c>-beta.1</c>, <c>-beta.2</c>, … — so the last check-in wins
        /// (the highest counter is the newest published version, which a VersionNorm-descending
        /// sort surfaces first). <paramref name="excludeModelId"/> is the model being published,
        /// so its own current version is not treated as a conflict.
        /// NOTE: superseded <c>-beta.N</c> rows are NOT pruned here; they accumulate until a
        /// future cleanup feature reclaims them (out of scope).
        /// </summary>
        private async Task<string> ComputeNextPublishedVersionAsync(string? modelUri, string? workingVersion, Guid excludeModelId)
        {
            var baseVersion = DbModel.PublishVersion(workingVersion); // "M.m.p-beta"
            var baseNorm = DbModel.NormalizeVersion(baseVersion);
            if (baseNorm == null) return baseVersion;

            // Normalized versions of OTHER models for this URI that share the "M.m.p-beta" prefix
            // (i.e. the base -beta and any -beta.N counters already taken).
            var takenNorms = await _db.Models
                .Where(m => m.Id != excludeModelId
                    && m.Uri == modelUri
                    && m.VersionNorm != null
                    && m.VersionNorm.StartsWith(baseNorm))
                .Select(m => m.VersionNorm!)
                .ToListAsync();

            var taken = new HashSet<string>(takenNorms, StringComparer.Ordinal);
            if (!taken.Contains(baseNorm))
                return baseVersion; // "M.m.p-beta" itself is free

            for (var n = 1; ; n++)
            {
                var candidate = $"{baseVersion}.{n}";
                var candidateNorm = DbModel.NormalizeVersion(candidate);
                if (candidateNorm != null && !taken.Contains(candidateNorm))
                    return candidate;
            }
        }

        #endregion

        #region Download/Upload

        public async Task<(Stream FileStream, ModelInfo ModelInfo)> GetModelFileStreamAsync(Guid workspaceId, Guid modelId)
        {
            var model = await _db.Models.FindAsync(modelId)
                ?? throw new KeyNotFoundException($"Model with Id '{modelId}' not found.");

            // Generate XML dynamically from DB nodes/references
            var nodeSet = await NodeSetEditor.Model.NodeSetConverter.CreateNodeSetAsync(_db, model.Uri!, model.Version);
            using var raw = new MemoryStream();
            nodeSet.Write(raw);
            // Emit the SPDX copyright/license/URL headers so they round-trip on every export.
            var injected = NodeSetEditor.Model.SpdxHeaders.InjectIntoXml(
                raw.ToArray(), model.CopyrightHolder, model.License, model.LicenseUrl);
            var ms = new MemoryStream(injected) { Position = 0 };

            return (ms, ToModelInfo(model));
        }

        public async Task<(Stream FileStream, ModelInfo ModelInfo)> GetSharedModelFileStreamAsync(Guid modelId)
        {
            // The modelId may be a Cloud Library deterministic GUID rather than a DB GUID.
            // Ensure the model is imported into the DB first.
            var dbModelId = await EnsureModelFromCatalogAsync(modelId);
            return await GetModelFileStreamAsync(Guid.Empty, dbModelId);
        }

        // Each chunk request is bounded to 200 MB at the Kestrel layer ([RequestSizeLimit]); this
        // bounds the assembled total across all chunks of a single multi-chunk upload.
        private const long MaxNodeSetUploadBytes = 200L * 1024 * 1024;

        public async Task<UploadResult> HandleUploadChunkAsync(
            Guid workspaceId, Stream content, string fileName,
            int chunkIndex, int totalChunks, string? uploadId,
            string? license = null, string? licenseUrl = null, string? copyrightHolder = null)
        {
            if (totalChunks < 1) throw new InvalidOperationException("totalChunks must be at least 1.");
            if (chunkIndex < 0 || chunkIndex >= totalChunks)
                throw new InvalidOperationException("chunkIndex is out of range.");

            using var chunkBuffer = new MemoryStream();
            await content.CopyToAsync(chunkBuffer);
            var chunkData = chunkBuffer.ToArray();

            byte[] bytes;
            if (totalChunks == 1)
            {
                bytes = chunkData;
            }
            else
            {
                // Multi-chunk upload: stage this chunk as a row keyed by (uploadId, chunkIndex) —
                // avoids repeatedly rewriting a growing blob — until all chunks have arrived.
                uploadId ??= Guid.NewGuid().ToString("N");

                var existing = await _db.NodeSetUploadChunks.FindAsync(uploadId, chunkIndex);
                if (existing == null)
                {
                    _db.NodeSetUploadChunks.Add(new NodeSetEditor.Model.NodeSetUploadChunk
                    {
                        UploadId = uploadId,
                        ChunkIndex = chunkIndex,
                        WorkspaceId = workspaceId,
                        FileName = fileName,
                        TotalChunks = totalChunks,
                        CreatedUtc = DateTime.UtcNow,
                        Data = chunkData,
                    });
                }
                else
                {
                    // Idempotent on retry of the same chunk index.
                    existing.Data = chunkData;
                    existing.TotalChunks = totalChunks;
                    existing.FileName = fileName;
                }
                await _db.SaveChangesAsync();

                var received = await _db.NodeSetUploadChunks.CountAsync(c => c.UploadId == uploadId);
                if (received < totalChunks)
                {
                    return new UploadResult
                    {
                        UploadId = uploadId,
                        ChunksReceived = received,
                        IsComplete = false,
                    };
                }

                // Final chunk arrived — assemble, verifying total size before allocating the blob.
                var chunks = await _db.NodeSetUploadChunks
                    .Where(c => c.UploadId == uploadId)
                    .OrderBy(c => c.ChunkIndex)
                    .ToListAsync();

                var totalLength = chunks.Sum(c => (long)c.Data.Length);
                if (totalLength > MaxNodeSetUploadBytes)
                {
                    _db.NodeSetUploadChunks.RemoveRange(chunks);
                    await _db.SaveChangesAsync();
                    throw new InvalidOperationException(
                        $"The NodeSet exceeds the {MaxNodeSetUploadBytes / (1024 * 1024)} MB limit.");
                }

                var assembled = new byte[totalLength];
                var offset = 0;
                foreach (var c in chunks)
                {
                    Buffer.BlockCopy(c.Data, 0, assembled, offset, c.Data.Length);
                    offset += c.Data.Length;
                }
                bytes = assembled;

                _db.NodeSetUploadChunks.RemoveRange(chunks);
                await _db.SaveChangesAsync();
            }

            // Parse so we can both read the embedded SPDX headers and the NodeSet itself.
            // UANodeSet.Read prohibits DTD internally, so entity-expansion attacks are blocked.
            using var buffer = new MemoryStream(bytes);
            var nodeSet = UANodeSet.Read(buffer);

            var modelUri = nodeSet.Models?.FirstOrDefault()?.ModelUri;
            // Prefer embedded SPDX (and OPC Foundation defaults) over the dialog-supplied values;
            // require a license + copyright; validate a custom license's URL. Throws → HTTP 400.
            var (effLicense, effUrl, effCopyright) = await ResolveImportLicenseOrThrowAsync(
                bytes, modelUri, license, licenseUrl, copyrightHolder);

            // An imported NodeSet lands as a private model that is already checked out for
            // editing, so the user can immediately work on it. Unlike CheckoutModelAsync, the
            // version is left exactly as it came in — import never bumps it to an -alpha working
            // copy; the imported version IS the working copy.
            var modelInfo = await UploadNodeSetAsync(workspaceId, nodeSet, isPrivate: true, isEditable: true,
                license: effLicense, licenseUrl: effUrl, copyrightHolder: effCopyright);

            return new UploadResult
            {
                UploadId = uploadId ?? Guid.NewGuid().ToString(),
                ChunksReceived = totalChunks,
                IsComplete = true,
                Model = modelInfo
            };
        }

        /// <summary>
        /// Computes the effective license/copyright for an imported file: embedded SPDX headers
        /// (and OPC Foundation defaults for foundation namespaces) take precedence over the
        /// dialog-supplied fallback values. Throws <see cref="InvalidOperationException"/> (→ 400)
        /// when no license + copyright can be determined, or when a custom/"Other" license lacks a
        /// valid http(s) URL. For a known catalog license the canonical URL is substituted.
        /// </summary>
        private async Task<(string License, string? Url, string Copyright)> ResolveImportLicenseOrThrowAsync(
            byte[] bytes, string? modelUri, string? providedLicense, string? providedUrl, string? providedCopyright)
        {
            var (embLic, embUrl, embCopyright) = NodeSetEditor.Model.SpdxHeaders.ResolveForImport(bytes, modelUri);

            // The user-supplied values come from the import dialog, where the auto-detected (embedded)
            // values were shown for confirmation/override — so the user's choice wins; embedded is the
            // fallback for non-UI callers that pass nothing.
            var license = (!string.IsNullOrWhiteSpace(providedLicense) ? providedLicense : embLic)?.Trim() ?? string.Empty;
            var url = (!string.IsNullOrWhiteSpace(providedUrl) ? providedUrl : embUrl)?.Trim();
            var copyright = (!string.IsNullOrWhiteSpace(providedCopyright) ? providedCopyright : embCopyright)?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(copyright))
                throw new InvalidOperationException(
                    "A license and copyright holder are required to import this NodeSet.");

            var options = await _db.LicenseOptions.ToListAsync();
            var known = options.FirstOrDefault(o =>
                !o.IsCustom && string.Equals(o.SpdxId, license, StringComparison.OrdinalIgnoreCase));
            if (known != null)
                return (license, known.ReferenceUrl, copyright);

            // Custom / "Other" license — a valid reference URL is mandatory.
            var validUrl = !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out var u)
                && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
            if (!validUrl)
                throw new InvalidOperationException(
                    "A valid license URL is required for a custom or 'Other' license.");
            return (license, url, copyright);
        }

        #endregion

        #region NodeSet Operations

        public async Task<UANodeSet> DownloadNodeSetAsync(Guid workspaceId, string modelUri, string? version = null)
        {
            // Generate dynamically from DB nodes/references — no stored XML
            return await NodeSetEditor.Model.NodeSetConverter.CreateNodeSetAsync(_db, modelUri, version);
        }

        public async Task<ModelInfo> UploadNodeSetAsync(Guid workspaceId, UANodeSet nodeSet, bool isPrivate = true, bool isEditable = false,
            string? license = null, string? licenseUrl = null, string? copyrightHolder = null,
            NodeSetEditor.Model.ModelOrigin origin = NodeSetEditor.Model.ModelOrigin.Upload)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Models)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            var primaryModel = nodeSet.Models?.FirstOrDefault()
                ?? throw new InvalidOperationException("The NodeSet does not contain valid model information.");

            var modelUri = primaryModel.ModelUri;

            // Check for duplicate in workspace
            var workspaceModelIds = ws.Models?.Select(wm => wm.ModelId).ToList() ?? new List<Guid>();
            if (workspaceModelIds.Count > 0)
            {
                var duplicate = await _db.Models.AnyAsync(m =>
                    workspaceModelIds.Contains(m.Id) &&
                    m.Uri == modelUri);

                if (duplicate)
                    throw new InvalidOperationException($"A model with URI '{modelUri}' already exists in this workspace.");
            }

            // Recursively import dependencies from file catalog before storing the model
            var importing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modelUri! };
            await EnsureDependenciesAsync(workspaceId, nodeSet, importing);

            // Store via NodeSetConverter (nodes, references, hierarchy — no XML blob)
            var dbModel = await NodeSetEditor.Model.NodeSetConverter.StoreNodeSetAsync(_db, nodeSet);
            // Provenance follows the CONTENT, so it is re-stamped even when this replaces an
            // existing version's nodes: a Cloud Library import over a previously uploaded row
            // makes it a Cloud Library copy, and an upload over a cached one makes it an upload.
            dbModel.Origin = origin;

            // Seed a name from the URI only when the row has no curated name yet
            // (StoreNodeSetAsync defaults Name to the URI for brand-new rows; a
            // re-import of an existing version keeps the row and its name).
            if (string.IsNullOrEmpty(dbModel.Name) || dbModel.Name == dbModel.Uri)
                dbModel.Name = DeriveModelName(modelUri ?? "Unknown");

            // Stamp license/copyright at genesis only. On a re-import of an existing version
            // the row keeps its original values (set-once, immutable) — same preservation rule
            // as the curated Name above.
            if (string.IsNullOrWhiteSpace(dbModel.License) && !string.IsNullOrWhiteSpace(license))
            {
                dbModel.License = license.Trim();
                dbModel.LicenseUrl = string.IsNullOrWhiteSpace(licenseUrl) ? null : licenseUrl.Trim();
                dbModel.CopyrightHolder = string.IsNullOrWhiteSpace(copyrightHolder) ? null : copyrightHolder.Trim();
            }
            await _db.SaveChangesAsync();

            // Every model gets a NamespaceMetadata object kept in sync with its version.
            await TrySyncNamespaceMetadataAsync(dbModel);

            // Add dependencies to workspace too
            await AddDependenciesToWorkspaceAsync(ws, nodeSet);

            // Add the primary model to workspace. The caller decides whether
            // this link is private (editable workspace copy — typical for file
            // uploads) or shared read-only (cloud library imports).
            ws.Models ??= new List<NodeSetEditor.Model.WorkspaceModel>();
            ws.Models.Add(new NodeSetEditor.Model.WorkspaceModel
            {
                WorkspaceId = workspaceId,
                ModelId = dbModel.Id,
                IsPrivate = isPrivate,
                IsEditable = isEditable
            });
            ws.ModifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return ToModelInfo(dbModel);
        }

        public async Task<List<TypeDefinition>> QueryNodesAsync(Guid workspaceId, NodeQueryFilter filter)
        {
            var ws = await _db.Workspaces
                .Include(w => w.Models)
                .FirstOrDefaultAsync(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException($"Workspace with Id '{workspaceId}' not found.");

            var modelIds = ws.Models?.Select(wm => wm.ModelId).ToList() ?? new List<Guid>();

            var query = _db.Nodes
                .Where(n => modelIds.Contains(n.ModelId))
                .Where(n => n.ParentNodeId == null); // top-level only

            if (filter.NodeClass.HasValue)
                query = query.Where(n => n.NodeClass == (int)filter.NodeClass.Value);

            if (!string.IsNullOrEmpty(filter.NameFilter))
            {
                // Escape ILike wildcards so user input is treated as a literal
                // substring, not a pattern. Backslash is the chosen escape char.
                var escaped = filter.NameFilter
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_");
                query = query.Where(n =>
                    (n.BrowseName != null && EF.Functions.ILike(n.BrowseName, $"%{escaped}%", "\\")) ||
                    (n.DisplayName != null && EF.Functions.ILike(n.DisplayName, $"%{escaped}%", "\\")));
            }

            if (!string.IsNullOrEmpty(filter.NodeId))
                query = query.Where(n => n.NodeId == filter.NodeId);

            // TypeDefinitionId filter via References table
            if (!string.IsNullOrEmpty(filter.TypeDefinitionId))
            {
                var nodeIdsWithTypeDef = _db.References
                    .Where(r => r.ReferenceTypeId == "i=40" && r.IsForward && r.TargetNodeId == filter.TypeDefinitionId)
                    .Select(r => r.SourceNodeId);

                query = query.Where(n => nodeIdsWithTypeDef.Contains(n.NodeId));
            }

            var nodes = await query.Take(500).ToListAsync();

            return nodes.Select(n => new TypeDefinition
            {
                NodeId = n.NodeId,
                BrowseName = n.BrowseName,
                DisplayName = n.DisplayName,
                Description = n.Description,
                NodeClass = (NodeClass)n.NodeClass
            }).ToList();
        }

        #endregion

        #region Model Metadata

        public async Task<ModelInfo> UpdateModelInfoAsync(Guid workspaceId, Guid modelId, string? name, string? version, string? description,
            string? license = null, string? licenseUrl = null, string? copyrightHolder = null,
            bool enforceReadOnlyReserved = false)
        {
            // Model rows are shared across workspaces, so a caller-supplied modelId
            // alone is not an authorization boundary: without this check a user who
            // can write to ANY workspace could mutate another workspace's (or a
            // published) model by id. Require the (workspaceId, modelId) link to
            // exist first, mirroring the membership check in CheckoutModelAsync.
            var isLinked = await _db.WorkspaceModels
                .AnyAsync(wm => wm.WorkspaceId == workspaceId && wm.ModelId == modelId);
            if (!isLinked)
                throw new KeyNotFoundException($"Model '{modelId}' is not in workspace '{workspaceId}'.");

            var model = await _db.Models.FindAsync(modelId)
                ?? throw new KeyNotFoundException($"Model with Id '{modelId}' not found.");

            // Models in the reserved OPC Foundation namespace are read-only even when held as a
            // private copy — their canonical license/metadata must not be altered.
            if (enforceReadOnlyReserved && model.Uri != null
                && model.Uri.StartsWith("http://opcfoundation.org/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Models in the OPC Foundation namespace (http://opcfoundation.org/) are read-only and cannot be edited.");
            }

            if (name != null) model.Name = name;
            var versionChanged = false;
            if (version != null)
            {
                model.Version = string.IsNullOrEmpty(version) ? null : version;
                // Keep the normalized form (and the unique (Uri, VersionNorm) index) in sync.
                model.SetVersionNorm(model.Version);
                // A manual version change also stamps a fresh publication date.
                model.PublicationDate = DateTime.UtcNow.ToString("o");
                versionChanged = true;
            }
            if (description != null) model.Description = string.IsNullOrEmpty(description) ? null : description;
            // License/copyright are editable for private models (the controller gates this). When a
            // license is supplied the caller has already validated it and resolved its URL.
            if (license != null)
            {
                model.License = license;
                model.LicenseUrl = licenseUrl;
            }
            if (copyrightHolder != null) model.CopyrightHolder = copyrightHolder;

            await _db.SaveChangesAsync();

            // Refresh NamespaceMetadata (NamespaceVersion / NamespacePublicationDate / ModelVersion).
            if (versionChanged)
                await TrySyncNamespaceMetadataAsync(model);

            return ToModelInfo(model);
        }

        public bool RequiresXmlContent => false;

        public async Task PersistChangesAsync(ModelChangeset changeset, Stream? xmlContent)
        {
            var modelId = changeset.ModelId;

            // Process node deletions
            foreach (var nc in changeset.Nodes.Where(n => n.Kind == ChangeKind.Delete))
            {
                // Delete references involving this node
                await _db.References
                    .Where(r => r.ModelId == modelId
                        && (r.SourceNodeId == nc.NodeId || r.TargetNodeId == nc.NodeId))
                    .ExecuteDeleteAsync();

                await _db.Nodes
                    .Where(n => n.ModelId == modelId && n.NodeId == nc.NodeId)
                    .ExecuteDeleteAsync();
            }

            // Process node upserts
            foreach (var nc in changeset.Nodes.Where(n => n.Kind == ChangeKind.Upsert))
            {
                var existing = await _db.Nodes
                    .FirstOrDefaultAsync(n => n.ModelId == modelId && n.NodeId == nc.NodeId);

                if (existing != null)
                {
                    if (nc.NodeClass.HasValue) existing.NodeClass = nc.NodeClass.Value;
                    existing.BrowseName = nc.BrowseName ?? existing.BrowseName;
                    existing.DisplayName = nc.DisplayName ?? existing.DisplayName;
                    existing.Description = nc.Description;
                    existing.ParentNodeId = nc.ParentNodeId ?? existing.ParentNodeId;
                    existing.SuperTypeId = nc.SuperTypeId ?? existing.SuperTypeId;
                    existing.TypeDefinitionId = nc.TypeDefinitionId ?? existing.TypeDefinitionId;
                    existing.ModellingRule = nc.ModellingRule ?? existing.ModellingRule;
                    if (nc.Attributes != null) existing.Attributes = nc.Attributes;

                    // Sync denormalized references when node columns change
                    if (nc.TypeDefinitionId != null)
                        await SyncForwardReferenceAsync(modelId, nc.NodeId, "i=40", nc.TypeDefinitionId);
                    if (nc.ModellingRule != null)
                        await SyncForwardReferenceAsync(modelId, nc.NodeId, "i=37", nc.ModellingRule);
                }
                else
                {
                    var maxOrdinal = await _db.Nodes
                        .Where(n => n.ModelId == modelId)
                        .MaxAsync(n => (int?)n.Ordinal) ?? 0;

                    _db.Nodes.Add(new NodeSetEditor.Model.Node
                    {
                        ModelId = modelId,
                        NodeId = nc.NodeId,
                        NodeClass = nc.NodeClass ?? 0,
                        BrowseName = nc.BrowseName,
                        DisplayName = nc.DisplayName,
                        Description = nc.Description,
                        ParentNodeId = nc.ParentNodeId,
                        SuperTypeId = nc.SuperTypeId,
                        TypeDefinitionId = nc.TypeDefinitionId,
                        ModellingRule = nc.ModellingRule,
                        Attributes = nc.Attributes,
                        Ordinal = maxOrdinal + 100,
                    });
                }
            }

            // Process reference deletions. A reference may live in the DB as
            // either the forward representation (Source=src, Target=tgt,
            // IsForward=fwd) or the inverse one (Source=tgt, Target=src,
            // IsForward=!fwd) depending on which endpoint authored it —
            // CreateChildNode for example writes the parent→child link on
            // the child as inverse. Match either form so a delete from the
            // parent's references view actually removes the row.
            foreach (var rc in changeset.References.Where(r => r.Kind == ChangeKind.Delete))
            {
                await _db.References
                    .Where(r => r.ModelId == modelId
                        && (
                            (r.SourceNodeId == rc.SourceNodeId
                                && r.TargetNodeId == rc.TargetNodeId
                                && r.ReferenceTypeId == rc.ReferenceTypeId
                                && r.IsForward == rc.IsForward)
                            || (r.SourceNodeId == rc.TargetNodeId
                                && r.TargetNodeId == rc.SourceNodeId
                                && r.ReferenceTypeId == rc.ReferenceTypeId
                                && r.IsForward == !rc.IsForward)
                        ))
                    .ExecuteDeleteAsync();
            }

            // Process reference upserts
            foreach (var rc in changeset.References.Where(r => r.Kind == ChangeKind.Upsert))
            {
                var exists = await _db.References.AnyAsync(r =>
                    r.ModelId == modelId
                    && r.SourceNodeId == rc.SourceNodeId
                    && r.ReferenceTypeId == rc.ReferenceTypeId
                    && r.TargetNodeId == rc.TargetNodeId
                    && r.IsForward == rc.IsForward);

                if (!exists)
                {
                    var maxOrdinal = await _db.References
                        .Where(r => r.ModelId == modelId)
                        .MaxAsync(r => (int?)r.Ordinal) ?? 0;

                    _db.References.Add(new NodeSetEditor.Model.Reference
                    {
                        ModelId = modelId,
                        SourceNodeId = rc.SourceNodeId,
                        ReferenceTypeId = rc.ReferenceTypeId,
                        TargetNodeId = rc.TargetNodeId,
                        IsForward = rc.IsForward,
                        Ordinal = maxOrdinal + 100,
                    });
                }
            }

            await _db.SaveChangesAsync();
        }

        public async Task SaveModelContentAsync(Guid workspaceId, Guid modelId, Stream content)
        {
            // Used only during initial import. For incremental changes, use PersistChangesAsync.
            var model = await _db.Models.FindAsync(modelId)
                ?? throw new KeyNotFoundException($"Model with Id '{modelId}' not found.");

            using var ms = new MemoryStream();
            await content.CopyToAsync(ms);
            ms.Position = 0;
            var nodeSet = Opc.Ua.Export.UANodeSet.Read(ms);

            // Capture workspace links before StoreNodeSetAsync cascade-deletes them
            var existingLinks = await _db.WorkspaceModels
                .Where(wm => wm.ModelId == modelId)
                .Select(wm => new { wm.WorkspaceId, wm.IsPrivate })
                .ToListAsync();

            var updatedModel = await NodeSetEditor.Model.NodeSetConverter.StoreNodeSetAsync(_db, nodeSet);
            // Content supplied by the caller replaces the row's nodes, so the row is no longer
            // a verbatim copy of whatever it was imported from — carry the origin across only
            // when it was already editor content, otherwise drop the trusted CloudLibrary mark.
            updatedModel.Origin = model.Origin == NodeSetEditor.Model.ModelOrigin.CloudLibrary
                ? NodeSetEditor.Model.ModelOrigin.Upload
                : model.Origin;

            if (model.Name != model.Uri) updatedModel.Name = model.Name;
            updatedModel.Description = model.Description;
            // License/copyright are set-once — carry the existing values across a content re-save,
            // and stamp from the file's embedded SPDX headers if the row never had any.
            updatedModel.License = model.License;
            updatedModel.LicenseUrl = model.LicenseUrl;
            updatedModel.CopyrightHolder = model.CopyrightHolder;
            StampImportLicenseIfMissing(updatedModel, ms.ToArray());

            // Re-create workspace links
            if (updatedModel.Id != modelId)
            {
                foreach (var link in existingLinks)
                {
                    _db.WorkspaceModels.Add(new NodeSetEditor.Model.WorkspaceModel
                    {
                        WorkspaceId = link.WorkspaceId,
                        ModelId = updatedModel.Id,
                        IsPrivate = link.IsPrivate
                    });
                }
            }

            await _db.SaveChangesAsync();
        }

        #endregion

        #region User Preferences

        public async Task<Guid?> GetUserSelectedWorkspaceAsync(string userId)
        {
            var pref = await _db.UserPreferences.FindAsync(userId);
            return pref?.SelectedWorkspaceId;
        }

        public async Task SetUserSelectedWorkspaceAsync(string userId, Guid workspaceId)
        {
            var pref = await GetOrCreateUserPreferenceAsync(userId);
            pref.SelectedWorkspaceId = workspaceId;
            await _db.SaveChangesAsync();
        }

        public async Task<string?> GetUserThemeModeAsync(string userId)
        {
            var pref = await _db.UserPreferences.FindAsync(userId);
            return pref?.ThemeMode;
        }

        public async Task SetUserThemeModeAsync(string userId, string themeMode)
        {
            var pref = await GetOrCreateUserPreferenceAsync(userId);
            pref.ThemeMode = themeMode;
            await _db.SaveChangesAsync();
        }

        public async Task<NodeSetEditor.Model.UserPreference> EnsureUserPreferenceAsync(string userId, string? email, string? displayName = null, string? tenantId = null)
        {
            var pref = await GetOrCreateUserPreferenceAsync(userId);

            if (string.IsNullOrEmpty(pref.Name))
            {
                // Fill the display Name with a DB-side conditional update so a
                // concurrent first-login request can never be overwritten: the SPA
                // fires several requests in parallel for a new user, and an
                // unconditional save here let the slower request see the faster
                // one's freshly committed name as "taken" and overwrite the row
                // with a numbered variant (randy → randy2).
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var candidate = await GenerateUniqueUserNameAsync(email);
                    try
                    {
                        await _db.UserPreferences
                            .Where(p => p.UserId == userId && (p.Name == null || p.Name == string.Empty))
                            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, candidate));
                        await _db.Entry(pref).ReloadAsync();
                        break;
                    }
                    catch (Exception e) when (IsUniqueViolation(e))
                    {
                        // A different user claimed the candidate concurrently
                        // (unique index on Name) — regenerate once.
                    }
                }
            }

            var changed = false;

            if (string.IsNullOrEmpty(pref.DefaultDomain))
            {
                var domain = ExtractEmailDomain(email);
                if (!string.IsNullOrEmpty(domain)) { pref.DefaultDomain = domain; changed = true; }
            }

            // Refresh token-sourced fields on every login — they can change in AAD.
            if (email != null && pref.Email != email) { pref.Email = email; changed = true; }

            // DisplayName: the Microsoft sign-in supplies a real name ("name" claim); the
            // email-code sign-in only has the email (its display name IS the email). Prefer a real
            // name and never let an email-only one overwrite it:
            //   - Microsoft login (name != email) always refreshes it — upgrading an email-derived
            //     name from an earlier email-code login.
            //   - Email-code login (name == email) fills it only when nothing better is stored yet.
            bool IsEmail(string? s) => s != null && email != null
                && s.Trim().Equals(email.Trim(), StringComparison.OrdinalIgnoreCase);
            if (displayName != null && pref.DisplayName != displayName)
            {
                var incomingIsRealName = !IsEmail(displayName);
                var haveRealName = !string.IsNullOrWhiteSpace(pref.DisplayName) && !IsEmail(pref.DisplayName);
                if (incomingIsRealName || !haveRealName)
                {
                    pref.DisplayName = displayName;
                    changed = true;
                }
            }

            if (tenantId != null && pref.TenantId != tenantId) { pref.TenantId = tenantId; changed = true; }

            // Seed create-model defaults for a new user (seed-if-missing, overridable in the account
            // drawer): copyright holder defaults to the user's display name; license defaults to MIT.
            if (string.IsNullOrEmpty(pref.DefaultCopyrightHolder) && !string.IsNullOrWhiteSpace(pref.DisplayName))
            {
                pref.DefaultCopyrightHolder = pref.DisplayName!.Trim();
                changed = true;
            }
            // If the copyright holder is still just the email — defaulted during an email-code login —
            // upgrade it once a real display name is available (a later Microsoft sign-in). A copyright
            // holder the user set to anything else is left untouched.
            else if (IsEmail(pref.DefaultCopyrightHolder)
                && !string.IsNullOrWhiteSpace(pref.DisplayName) && !IsEmail(pref.DisplayName))
            {
                pref.DefaultCopyrightHolder = pref.DisplayName!.Trim();
                changed = true;
            }
            if (string.IsNullOrEmpty(pref.DefaultLicense))
            {
                pref.DefaultLicense = "MIT";
                changed = true;
            }

            // TenantDomain: email domain is the best available proxy without authenticated
            // Graph API calls. For B2B guests this will reflect the home identity domain
            // rather than the hosting tenant — an accepted limitation.
            var emailDomain = ExtractEmailDomain(email);
            if (emailDomain != null && pref.TenantDomain != emailDomain) { pref.TenantDomain = emailDomain; changed = true; }

            if (changed) await _db.SaveChangesAsync();

            return pref;
        }

        public async Task<(bool Ok, string? Error)> SetUserNameAsync(string userId, string name)
        {
            var normalized = name.Trim();
            if (string.IsNullOrEmpty(normalized))
                return (false, "Name cannot be empty.");

            var lowered = normalized.ToLowerInvariant();
            var taken = await _db.UserPreferences.AnyAsync(p =>
                p.UserId != userId && p.Name != null && p.Name.ToLower() == lowered);
            if (taken)
                return (false, $"The name '{normalized}' is already taken.");

            var pref = await GetOrCreateUserPreferenceAsync(userId);
            pref.Name = normalized;
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException e) when (IsUniqueViolation(e))
            {
                // The unique index on Name is the backstop for a concurrent claim
                // that slipped past the app-level check above.
                await _db.Entry(pref).ReloadAsync();
                return (false, $"The name '{normalized}' is already taken.");
            }
            return (true, null);
        }

        public async Task SetUserDefaultDomainAsync(string userId, string? domain)
        {
            var normalized = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();

            var pref = await GetOrCreateUserPreferenceAsync(userId);
            pref.DefaultDomain = normalized;
            await _db.SaveChangesAsync();
        }

        public async Task SetUserDefaultLicenseAsync(string userId, string? license, string? licenseUrl)
        {
            var pref = await GetOrCreateUserPreferenceAsync(userId);
            pref.DefaultLicense = string.IsNullOrWhiteSpace(license) ? null : license.Trim();
            pref.DefaultLicenseUrl = string.IsNullOrWhiteSpace(licenseUrl) ? null : licenseUrl.Trim();
            await _db.SaveChangesAsync();
        }

        public async Task SetUserDefaultCopyrightHolderAsync(string userId, string? copyrightHolder)
        {
            var pref = await GetOrCreateUserPreferenceAsync(userId);
            pref.DefaultCopyrightHolder = string.IsNullOrWhiteSpace(copyrightHolder) ? null : copyrightHolder.Trim();
            await _db.SaveChangesAsync();
        }

        public async Task<List<NodeSetEditor.Model.LicenseOption>> GetLicenseOptionsAsync()
        {
            return await _db.LicenseOptions
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Name)
                .ToListAsync();
        }

        public async Task AcceptTermsAsync(string userId)
        {
            var pref = await GetOrCreateUserPreferenceAsync(userId);
            pref.TermsAcceptedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Find the user's preference row, creating an empty one if missing. Safe
        /// under the first-login burst: the SPA fires several requests in parallel
        /// (preferences, discovery, workspace auto-create) that all try to provision
        /// the row. The DB-side ON CONFLICT DO NOTHING makes the losers of the
        /// insert race no-op without raising a unique violation — EF logs handled
        /// DbUpdateExceptions at Error level, so catching one is not enough to keep
        /// the logs (and Azure alerts) clean.
        /// </summary>
        private async Task<NodeSetEditor.Model.UserPreference> GetOrCreateUserPreferenceAsync(string userId)
        {
            var pref = await _db.UserPreferences.FindAsync(userId);
            if (pref != null) return pref;

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO ""UserPreferences"" (""UserId"") VALUES ({userId}) ON CONFLICT (""UserId"") DO NOTHING");

            return await _db.UserPreferences.FindAsync(userId)
                ?? throw new InvalidOperationException(
                    $"Failed to provision the UserPreference row for '{userId}'.");
        }


        private static bool IsUniqueViolation(Exception e)
        {
            // SaveChanges wraps the Postgres error in DbUpdateException;
            // ExecuteUpdateAsync surfaces it directly.
            var pg = (e as Npgsql.PostgresException) ?? (e.InnerException as Npgsql.PostgresException);
            return pg?.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;
        }

        public async Task<Dictionary<string, string>> GetUserDisplayNamesAsync(IEnumerable<string> userIds)
        {
            var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<string, string>();

            return await _db.UserPreferences
                .Where(p => ids.Contains(p.UserId) && p.Name != null)
                .ToDictionaryAsync(p => p.UserId, p => p.Name!);
        }

        /// <summary>
        /// Builds a globally-unique display name from the email's local part (e.g.
        /// "randy@example.com" → "randy"), appending the smallest digit suffix needed
        /// to avoid collisions ("randy", "randy2", "randy3", …). Falls back to "user"
        /// when no usable local part is available.
        /// </summary>
        private async Task<string> GenerateUniqueUserNameAsync(string? email)
        {
            var baseName = ExtractEmailLocalPart(email);
            if (string.IsNullOrEmpty(baseName)) baseName = "user";

            var existing = await _db.UserPreferences
                .Where(p => p.Name != null)
                .Select(p => p.Name!)
                .ToListAsync();
            var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

            if (!taken.Contains(baseName)) return baseName;
            for (var i = 2; ; i++)
            {
                var candidate = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static string? ExtractEmailLocalPart(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            var at = email.IndexOf('@');
            var local = at > 0 ? email[..at] : email;
            return local.Trim();
        }

        private static string? ExtractEmailDomain(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            var at = email.IndexOf('@');
            return at > 0 && at < email.Length - 1 ? email[(at + 1)..].Trim() : null;
        }

        #endregion

        #region Private Helpers

        private async Task<DbWorkspace> CreateWorkspaceInternalAsync(
            string userId, string? ownerEmail, string name, string? description, List<string>? acl)
        {
            var ws = new DbWorkspace
            {
                Name = name,
                Description = description,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                OwnerUserId = userId,
                OwnerEmail = ownerEmail,
                Models = new List<NodeSetEditor.Model.WorkspaceModel>()
            };

            if (acl != null)
            {
                ws.Acl = acl.Select(e => new NodeSetEditor.Model.WorkspaceAcl
                {
                    Email = e.ToLowerInvariant()
                }).DistinctBy(a => a.Email).ToList();
            }

            // Add the latest UA Core model
            var coreModel = await GetLatestCoreModelAsync();
            if (coreModel != null)
            {
                ws.Models.Add(new NodeSetEditor.Model.WorkspaceModel
                {
                    ModelId = coreModel.Id,
                    IsPrivate = false
                });
            }

            _db.Workspaces.Add(ws);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Created workspace '{Name}' ({Id}) for user {UserId} with Core model",
                ws.Name, ws.Id, userId);

            return ws;
        }

        private async Task<DbModel?> GetLatestCoreModelAsync()
        {
            var existing = await _db.Models
                .Where(m => m.Uri == UaCoreNamespace)
                .OrderByDescending(m => m.VersionNorm)
                .FirstOrDefaultAsync();
            if (existing != null) return existing;

            // Self-heal path: when the DB has no Core model (fresh init script
            // failed, or the user reset the schema without re-importing), fall
            // back to the Core NodeSet embedded in Opc.Ua.NodeSetSerializer so every
            // workspace can still be created with a usable type system. The
            // initialize_db.ps1 import path is still preferred (it pulls the
            // latest from GitHub); this is the safety net.
            try
            {
                using var stream = LoadEmbeddedCoreNodeSetStream();
                if (stream == null)
                {
                    _logger.LogWarning(
                        "No Core NodeSet in DB and no embedded fallback found. " +
                        "Workspaces will be created without Core — run db/initialize_db.ps1 to import it.");
                    return null;
                }
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                var coreBytes = buffer.ToArray();
                buffer.Position = 0;
                var nodeSet = UANodeSet.Read(buffer);
                var dbModel = await NodeSetEditor.Model.NodeSetConverter.StoreNodeSetAsync(_db, nodeSet);
                // The embedded Core NodeSet is the published OPC Foundation release — same
                // standing as a Cloud Library copy for dependency resolution.
                dbModel.Origin = NodeSetEditor.Model.ModelOrigin.CloudLibrary;

                // The Core NodeSet is an OPC Foundation model — stamp the Foundation license/copyright
                // (set-once) so the self-healed Core row satisfies the "every model has a license" rule.
                if (string.IsNullOrWhiteSpace(dbModel.License))
                {
                    var (lic, licUrl, cr) = NodeSetEditor.Model.SpdxHeaders.ResolveForImport(coreBytes, dbModel.Uri);
                    if (!string.IsNullOrWhiteSpace(lic))
                    {
                        dbModel.License = lic;
                        dbModel.LicenseUrl = licUrl;
                        dbModel.CopyrightHolder = cr;
                    }
                }
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "Imported embedded Core NodeSet into DB ({ModelId}, version {Version}).",
                    dbModel.Id, dbModel.Version);
                return dbModel;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to import embedded Core NodeSet — workspace will be created without Core.");
                return null;
            }
        }

        /// <summary>
        /// Opens a stream over the Core NodeSet XML embedded in the
        /// <c>Opc.Ua.NodeSetSerializer</c> assembly (resource name
        /// <c>Opc.Ua.NodeSetSerializer.Resources.Opc.Ua.NodeSet2.Services.xml</c>).
        /// Returns null if the resource isn't present.
        /// </summary>
        private static Stream? LoadEmbeddedCoreNodeSetStream()
        {
            const string resourceName = "Opc.Ua.NodeSetSerializer.Resources.Opc.Ua.NodeSet2.Services.xml";
            var asm = typeof(JsonNodeSet::Opc.Ua.NodeSetSerializer.CoreNodeSetLoader).Assembly;
            return asm.GetManifestResourceStream(resourceName);
        }

        private async Task<DbModel?> FindModelAsync(string modelUri, string? version)
        {
            var query = _db.Models.Where(m => m.Uri == modelUri);

            if (!string.IsNullOrEmpty(version))
            {
                var norm = DbModel.NormalizeVersion(version);
                query = query.Where(m => m.VersionNorm == norm);
            }

            return await query
                .OrderByDescending(m => m.VersionNorm)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// Workspace-aware model lookup. Private models in this workspace always win
        /// regardless of version. Falls back to shared models (excluding other workspaces' privates).
        /// </summary>
        private async Task<DbModel?> FindModelForWorkspaceAsync(Guid workspaceId, string modelUri, string? version)
        {
            // 1. Check for a private model in THIS workspace with matching URI — always wins.
            // A workspace may hold two private rows for one URI during a checkout (the retained
            // good version plus the editable -alpha working copy), so the tiebreak is
            // deterministic: prefer the editable working copy, then the highest VersionNorm.
            var privateModel = await _db.WorkspaceModels
                .Include(wm => wm.Model)
                .Where(wm => wm.WorkspaceId == workspaceId
                    && wm.IsPrivate
                    && wm.Model!.Uri == modelUri)
                .OrderByDescending(wm => wm.IsEditable)
                .ThenByDescending(wm => wm.Model!.VersionNorm)
                .Select(wm => wm.Model)
                .FirstOrDefaultAsync();

            if (privateModel != null)
                return privateModel;

            // 2. Any model already linked into THIS workspace (a shared link, e.g. a Cloud
            //    Library import) satisfies the lookup — the workspace's own composition wins.
            var linkedModel = await _db.WorkspaceModels
                .Include(wm => wm.Model)
                .Where(wm => wm.WorkspaceId == workspaceId && wm.Model!.Uri == modelUri)
                .OrderByDescending(wm => wm.Model!.VersionNorm)
                .Select(wm => wm.Model)
                .FirstOrDefaultAsync();

            if (linkedModel != null && (string.IsNullOrEmpty(version)
                || linkedModel.VersionNorm == DbModel.NormalizeVersion(version)))
            {
                return linkedModel;
            }

            // 3. Fall back to rows that are shared for everyone: a Cloud Library copy or an
            //    explicitly published model. A row is NOT shared merely because some other
            //    workspace links it non-privately — see IsModelLinkableAsync.
            var query = _db.Models
                .Where(m => m.Uri == modelUri)
                .Where(m => m.Origin == NodeSetEditor.Model.ModelOrigin.CloudLibrary || m.Published);

            if (!string.IsNullOrEmpty(version))
            {
                var norm = DbModel.NormalizeVersion(version);
                query = query.Where(m => m.VersionNorm == norm);
            }

            return await query
                .OrderByDescending(m => m.VersionNorm)
                .FirstOrDefaultAsync();
        }

        private static Workspace ToDto(DbWorkspace ws)
        {
            return new Workspace
            {
                Id = ws.Id,
                Name = ws.Name,
                Description = ws.Description,
                CreateDate = ws.CreatedAt,
                ModifyDate = ws.ModifiedAt,
                Owner = ws.OwnerUserId,
                OwnerEmail = ws.OwnerEmail,
                Models = ws.Models?.Select(wm => new ModelReference
                {
                    Id = wm.ModelId,
                    IsPrivate = wm.IsPrivate,
                    IsEditable = wm.IsEditable
                }).ToList(),
                Acl = ws.Acl?.Select(a => a.Email).ToList()
            };
        }

        private static ModelInfo ToModelInfo(DbModel m)
        {
            return new ModelInfo
            {
                Id = m.Id,
                ModelUri = m.Uri,
                Name = m.Name,
                Description = m.Description,
                ModelVersion = m.Version,
                PublicationDate = m.PublicationDate,
                HasErrors = m.HasErrors,
                Creator = m.Creator,
                License = m.License,
                LicenseUrl = m.LicenseUrl,
                CopyrightHolder = m.CopyrightHolder
            };
        }

        private static string DeriveModelName(string uri) => NamespaceNaming.DeriveFromUri(uri);

        /// <summary>
        /// Picks the display Name for an auto-imported model. Cloud Library <c>Title</c> is
        /// submission-scoped, not namespace-scoped: a single submission that spans several
        /// namespaces (e.g. PADIM plus the IRDI dictionary it bundles) reports the SAME Title for
        /// each, which would seed two different-URI rows with an identical Name — they then look
        /// like a duplicate in the workspace list. When the candidate Title is already used by a
        /// model with a DIFFERENT URI, fall back to a URI-derived name so the namespaces stay
        /// distinguishable ("PADIM" vs "IRDI"); otherwise keep the friendlier catalog Title.
        /// </summary>
        private async Task<string> DisambiguateImportNameAsync(string? candidateTitle, string uri)
        {
            var name = string.IsNullOrWhiteSpace(candidateTitle) ? DeriveModelName(uri) : candidateTitle.Trim();
            var collides = await _db.Models.AnyAsync(m => m.Name == name && m.Uri != uri);
            return collides ? DeriveModelName(uri) : name;
        }

        #endregion

        #region File Catalog → DB Import

        /// <summary>
        /// Ensures a model exists in the DB. If the ID matches a DB model, returns it directly.
        /// Otherwise checks if it's a Cloud Library deterministic GUID and imports from there.
        /// </summary>
        public async Task<Guid> EnsureModelFromCatalogAsync(Guid catalogModelId)
        {
            // Fast path: the ID is already a DB model
            var existing = await _db.Models.FirstOrDefaultAsync(m => m.Id == catalogModelId);
            if (existing != null)
                return existing.Id;

            // Check if this is a Cloud Library deterministic GUID
            var clId = await ResolveCloudLibraryIdAsync(catalogModelId);
            if (clId != null)
            {
                var dbId = await ImportFromCloudLibraryAsync(clId);
                if (dbId.HasValue) return dbId.Value;
            }

            throw new KeyNotFoundException($"Model '{catalogModelId}' not found in DB or Cloud Library.");
        }

        /// <summary>
        /// Updates or removes a forward reference in the References table to stay in sync
        /// with a denormalized Node column (TypeDefinitionId, ModellingRule).
        /// Empty targetNodeId removes the reference; non-empty upserts it.
        /// </summary>
        private async Task SyncForwardReferenceAsync(Guid modelId, string sourceNodeId, string referenceTypeId, string? targetNodeId)
        {
            var existing = await _db.References
                .FirstOrDefaultAsync(r => r.ModelId == modelId
                    && r.SourceNodeId == sourceNodeId
                    && r.ReferenceTypeId == referenceTypeId
                    && r.IsForward);

            if (string.IsNullOrEmpty(targetNodeId))
            {
                // Remove
                if (existing != null)
                    _db.References.Remove(existing);
            }
            else if (existing != null)
            {
                // Update target
                existing.TargetNodeId = targetNodeId;
            }
            else
            {
                // Insert
                var maxOrdinal = await _db.References
                    .Where(r => r.ModelId == modelId)
                    .MaxAsync(r => (int?)r.Ordinal) ?? 0;

                _db.References.Add(new NodeSetEditor.Model.Reference
                {
                    ModelId = modelId,
                    SourceNodeId = sourceNodeId,
                    ReferenceTypeId = referenceTypeId,
                    TargetNodeId = targetNodeId,
                    IsForward = true,
                    Ordinal = maxOrdinal + 100,
                });
            }
        }

        /// <summary>
        /// Ensures all RequiredModels from a NodeSet exist in the DB.
        /// For each dependency: if a model with the same URI and equal-or-later version
        /// already exists in the DB, skip it. Otherwise import from the Cloud Library.
        /// Non-workspace-aware overload for Cloud Library imports (global context).
        /// </summary>
        private Task EnsureDependenciesAsync(UANodeSet nodeSet, HashSet<string> importing)
            => EnsureDependenciesAsync(Guid.Empty, nodeSet, importing);

        /// <summary>
        /// Workspace-aware dependency resolution. Private models in the workspace satisfy
        /// dependencies regardless of version. Falls back to shared models then Cloud Library.
        /// </summary>
        private async Task EnsureDependenciesAsync(Guid workspaceId, UANodeSet nodeSet, HashSet<string> importing)
        {
            var primaryModel = nodeSet.Models?.FirstOrDefault();
            if (primaryModel?.RequiredModel == null) return;

            foreach (var req in primaryModel.RequiredModel)
            {
                if (string.IsNullOrEmpty(req.ModelUri)) continue;

                // Don't recurse into models we're already importing (cycle guard)
                if (importing.Contains(req.ModelUri)) continue;

                // 1. Anything already linked into THIS workspace satisfies the dependency,
                //    regardless of version or provenance — the user's own composition wins.
                var linkedMatch = await _db.WorkspaceModels
                    .Where(wm => wm.WorkspaceId == workspaceId
                        && wm.Model!.Uri == req.ModelUri)
                    .FirstOrDefaultAsync();

                if (linkedMatch != null)
                    continue;

                // 2. A Cloud Library copy already cached in the DB, if it is new enough.
                //    ONLY CloudLibrary-origin rows qualify here. A model authored or uploaded
                //    by some other user must never satisfy this workspace's dependency: that
                //    is how one user's private nodeset came to shadow the Cloud Library copy
                //    of a published namespace for everybody else.
                var reqVersionNorm = DbModel.NormalizeVersion(req.ModelVersion ?? req.Version);
                var cachedMatch = await _db.Models
                    .Where(m => m.Uri == req.ModelUri
                        && m.Origin == NodeSetEditor.Model.ModelOrigin.CloudLibrary)
                    .OrderByDescending(m => m.VersionNorm)
                    .FirstOrDefaultAsync();

                if (cachedMatch != null)
                {
                    if (reqVersionNorm == null ||
                        string.Compare(cachedMatch.VersionNorm, reqVersionNorm, StringComparison.Ordinal) >= 0)
                    {
                        continue; // cached Cloud Library copy is current or later
                    }

                    _logger.LogInformation(
                        "Dependency '{Uri}' in DB is v{DbVersion} but v{Required} needed, upgrading from Cloud Library",
                        req.ModelUri, cachedMatch.Version, req.ModelVersion ?? req.Version);
                }

                // 3. Fetch it from the Cloud Library.
                if (await ImportDependencyFromCloudLibraryAsync(req.ModelUri, importing))
                    continue;

                // 4. Last resort: a model another user explicitly PUBLISHED here. This covers
                //    namespaces the Cloud Library doesn't carry. Publishing is an explicit,
                //    attributable act and is blocked for namespaces the Cloud Library does
                //    carry (see CheckinModelAsync "publish").
                var publishedMatch = await _db.Models
                    .Where(m => m.Uri == req.ModelUri && m.Published)
                    .OrderByDescending(m => m.VersionNorm)
                    .FirstOrDefaultAsync();

                if (publishedMatch != null)
                {
                    _logger.LogInformation(
                        "Dependency '{Uri}' resolved to published model {ModelId} v{Version} (not in Cloud Library)",
                        req.ModelUri, publishedMatch.Id, publishedMatch.Version);
                    continue;
                }

                _logger.LogWarning("Dependency '{Uri}' not found in DB or Cloud Library", req.ModelUri);
            }
        }

        /// <summary>
        /// Adds all RequiredModels of a NodeSet to the workspace if not already present —
        /// walking the FULL transitive closure (a dependency's own dependencies, and so on),
        /// not just the primary model's direct RequiredModel list. A NodeSet XML only declares
        /// its immediate requirements (e.g. an uploaded model may require only Machinery, while
        /// Machinery itself requires IA) — without recursing here, transitive dependencies would
        /// be imported into the DB (by EnsureDependenciesAsync, which IS recursive) but never
        /// linked into the workspace, leaving the workspace's address space incomplete even
        /// though the data exists. Assumes the dependencies already exist in the DB.
        /// </summary>
        private async Task AddDependenciesToWorkspaceAsync(DbWorkspace ws, UANodeSet nodeSet)
        {
            var primaryModel = nodeSet.Models?.FirstOrDefault();
            if (primaryModel?.RequiredModel == null) return;

            ws.Models ??= new List<NodeSetEditor.Model.WorkspaceModel>();
            var existingModelIds = ws.Models.Select(wm => wm.ModelId).ToHashSet();

            var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var req in primaryModel.RequiredModel)
            {
                if (!string.IsNullOrEmpty(req.ModelUri) && seenUris.Add(req.ModelUri))
                    queue.Enqueue(req.ModelUri);
            }

            while (queue.Count > 0)
            {
                var uri = queue.Dequeue();

                // Workspace-aware lookup: private model in this workspace wins.
                // Pass null for the version so we get the latest matching URI in
                // the DB. EnsureDependenciesAsync has already guaranteed an
                // equal-or-newer version is present, and requiring an exact
                // version match here drops deps whenever the cached version
                // happens to be different from the one declared in RequiredModel
                // (e.g. cloud library has 1.05.05 but the importing nodeset
                // declares 1.05.04 — should still satisfy the dependency).
                var dbModel = await FindModelForWorkspaceAsync(ws.Id, uri, null);
                if (dbModel == null) continue; // unresolved (e.g. Cloud Library miss) — nothing to link or recurse into

                if (!existingModelIds.Contains(dbModel.Id))
                {
                    // Determine if the matched model is private to this workspace
                    var isPrivateInWorkspace = await _db.WorkspaceModels
                        .AnyAsync(wm => wm.WorkspaceId == ws.Id
                            && wm.ModelId == dbModel.Id
                            && wm.IsPrivate);

                    ws.Models.Add(new NodeSetEditor.Model.WorkspaceModel
                    {
                        WorkspaceId = ws.Id,
                        ModelId = dbModel.Id,
                        IsPrivate = isPrivateInWorkspace
                    });
                    existingModelIds.Add(dbModel.Id);

                    _logger.LogInformation(
                        "Auto-added dependency '{Uri}' ({ModelId}) to workspace {WorkspaceId} (private={IsPrivate})",
                        uri, dbModel.Id, ws.Id, isPrivateInWorkspace);
                }

                // Recurse into this dependency's own RequiredModels (stored as fallback
                // metadata by StoreNodeSetAsync/BuildMetadata) to reach the full closure.
                if (dbModel.Metadata?["RequiredModels"] is JsonArray reqArr)
                {
                    foreach (var reqEntry in reqArr)
                    {
                        var reqUri = reqEntry?["ModelUri"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(reqUri) && seenUris.Add(reqUri))
                            queue.Enqueue(reqUri);
                    }
                }
            }
        }


        #endregion

        #region Cloud Library Import

        /// <summary>
        /// Deterministic GUID from a Cloud Library string identifier. This is the id a Cloud Library
        /// model is addressed by before it has been imported, so any caller deriving it (the
        /// workspace integration tests do) must hash identically.
        /// </summary>
        private static Guid StringToGuid(string id)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes($"cloudlib:{id}"));
            return new Guid(bytes);
        }

        /// <summary>
        /// Checks if a GUID matches a Cloud Library model by searching the CL index.
        /// Returns the CL string identifier if found, null otherwise.
        /// </summary>
        private async Task<string?> ResolveCloudLibraryIdAsync(Guid guid)
        {
            if (_cloudLib == null) return null;

            try
            {
                // Page the whole catalog and check if any identifier's deterministic GUID matches.
                // Cloud Library returns identifier either nested (Nodeset.Identifier) or at the top
                // level (NodesetId) depending on the response shape. A single limit=500 call left
                // every catalog entry past the 500th unresolvable.
                const int pageSize = 100;
                const int maxPages = 200;
                for (var page = 0; page < maxPages; page++)
                {
                    var batch = await _cloudLib.Find2Async(offset: page * pageSize, limit: pageSize);
                    if (batch == null || batch.Length == 0) break;

                    var hit = batch
                        .Select(r => r.Nodeset?.Identifier ?? r.NodesetId)
                        .FirstOrDefault(id => id != null && StringToGuid(id) == guid);
                    if (hit != null) return hit;

                    if (batch.Length < pageSize) break;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Downloads a model from the Cloud Library by string identifier,
        /// stores it in the DB (with recursive dependency resolution), and returns the DB GUID.
        /// </summary>
        /// <summary>
        /// Stamps license/copyright onto a freshly auto-imported model (dependency, catalog, or
        /// cloud import) when it has none yet. Prefers the file's embedded SPDX headers (and OPC
        /// Foundation defaults for foundation namespaces); falls back to Cloud Library metadata.
        /// Set-once: existing values are preserved. Caller is responsible for SaveChanges.
        /// </summary>
        private static void StampImportLicenseIfMissing(DbModel dbModel, byte[]? rawXml,
            string? cloudLicense = null, string? cloudCopyrightText = null)
        {
            if (!string.IsNullOrWhiteSpace(dbModel.License)) return;

            var (license, url, copyright) = NodeSetEditor.Model.SpdxHeaders.ResolveForImport(rawXml, dbModel.Uri);
            // Fall back to Cloud Library metadata when the file carries no embedded headers/defaults.
            if (string.IsNullOrWhiteSpace(license) && !string.IsNullOrWhiteSpace(cloudLicense))
                license = cloudLicense.Trim();
            if (string.IsNullOrWhiteSpace(copyright))
                copyright = NodeSetEditor.Model.SpdxHeaders.ExtractCopyrightHolder(cloudCopyrightText);

            if (string.IsNullOrWhiteSpace(license)) return;
            dbModel.License = license;
            dbModel.LicenseUrl = url;
            dbModel.CopyrightHolder = copyright;
        }

        private async Task<Guid?> ImportFromCloudLibraryAsync(string clIdentifier)
        {
            if (_cloudLib == null) return null;

            try
            {
                var downloaded = await _cloudLib.DownloadAsync(clIdentifier);
                if (downloaded?.Nodeset?.NodesetXml == null)
                {
                    _logger.LogWarning("Cloud Library model {Id} has no XML content", clIdentifier);
                    return null;
                }

                var nsUri = downloaded.Nodeset.NamespaceUri;

                // Reuse a cached Cloud Library copy if we already have one. Only
                // CloudLibrary-origin rows count: reusing ANY row for the URI meant a model
                // someone authored or uploaded under a published namespace was handed back
                // here instead of the Cloud Library content the caller asked for.
                var cachedByUri = await _db.Models
                    .Where(m => m.Uri == nsUri
                        && m.Origin == NodeSetEditor.Model.ModelOrigin.CloudLibrary)
                    .OrderByDescending(m => m.VersionNorm)
                    .FirstOrDefaultAsync();
                if (cachedByUri != null)
                    return cachedByUri.Id;

                _logger.LogInformation(
                    "Importing Cloud Library model '{Title}' ({NsUri}) id={Id}",
                    downloaded.Title, nsUri, clIdentifier);

                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(downloaded.Nodeset.NodesetXml));
                var nodeSet = UANodeSet.Read(ms);

                // Recursive dependency resolution
                var importing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nsUri! };
                await EnsureDependenciesAsync(nodeSet, importing);

                var dbModel = await NodeSetEditor.Model.NodeSetConverter.StoreNodeSetAsync(_db, nodeSet);
                dbModel.Origin = NodeSetEditor.Model.ModelOrigin.CloudLibrary;
                // Curated names/descriptions in the DB are never overridden by
                // automatic imports — only seed them when missing.
                if (string.IsNullOrEmpty(dbModel.Name) || dbModel.Name == dbModel.Uri)
                    dbModel.Name = await DisambiguateImportNameAsync(downloaded.Title, dbModel.Uri ?? nsUri ?? "Unknown");
                if (string.IsNullOrEmpty(dbModel.Description))
                    dbModel.Description = downloaded.Description;
                StampImportLicenseIfMissing(dbModel, ms.ToArray(), downloaded.License, downloaded.CopyrightText);
                await _db.SaveChangesAsync();

                return dbModel.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import Cloud Library model {Id}", clIdentifier);
                return null;
            }
        }

        /// <summary>
        /// Imports a dependency by namespace URI from the Cloud Library.
        /// Used as fallback when the file catalog doesn't have the model.
        /// </summary>
        /// <summary>
        /// Writes a per-call JSON dump of a Cloud Library namespace lookup to the filesystem so a
        /// mislabeled/duplicate import can be diagnosed offline: every result the CL returned (title,
        /// namespace, id, version, publication date, description), which entries share the queried
        /// namespace, the entry we selected, and whether the titles conflicted. OFF by default —
        /// enable with <c>CloudLibraryDiagnostics:Enabled=true</c> (files land in
        /// <c>CloudLibraryDiagnostics:Path</c>, defaulting to &lt;ContentRoot&gt;/cloudlib-diagnostics).
        /// Never throws into the import path.
        /// </summary>
        private void DumpCloudLibDiagnostics(
            string queryNamespace,
            Opc.Ua.CloudLibraryApi.UANodesetResult[]? allResults,
            List<Opc.Ua.CloudLibraryApi.UANodesetResult> sameNamespace,
            Opc.Ua.CloudLibraryApi.UANodesetResult? selected,
            bool titleAmbiguous)
        {
            try
            {
                if (!_config.GetValue<bool>("CloudLibraryDiagnostics:Enabled")) return;

                var dir = _config["CloudLibraryDiagnostics:Path"];
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Path.Combine(_environment.ContentRootPath, "cloudlib-diagnostics");
                Directory.CreateDirectory(dir);

                static object Row(Opc.Ua.CloudLibraryApi.UANodesetResult r) => new
                {
                    title = r.Title,
                    namespaceUri = r.Nodeset?.NamespaceUri ?? r.NodesetNamespaceUri,
                    identifier = r.Nodeset?.Identifier ?? r.NodesetId,
                    version = r.Nodeset?.Version ?? r.Version,
                    publicationDate = r.Nodeset?.PublicationDate ?? r.PublicationDate,
                    description = r.Description,
                };

                var now = DateTime.UtcNow;
                var payload = new
                {
                    timestampUtc = now,
                    queryNamespace,
                    totalResults = allResults?.Length ?? 0,
                    sameNamespaceCount = sameNamespace.Count,
                    selectedIdentifier = selected != null ? (selected.Nodeset?.Identifier ?? selected.NodesetId) : null,
                    selectedTitle = selected?.Title,
                    titleAmbiguous,
                    // The entries that share the queried namespace (the candidates we chose among),
                    // newest-first — this is where the mislabeling originates.
                    sameNamespace = sameNamespace.Select(Row).ToArray(),
                    // Everything the CL returned for the query, for full context.
                    allResults = (allResults ?? Array.Empty<Opc.Ua.CloudLibraryApi.UANodesetResult>()).Select(Row).ToArray(),
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                var safeNs = new string(queryNamespace.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                if (safeNs.Length > 80) safeNs = safeNs[^80..];
                var file = Path.Combine(dir, $"cloudlib-{now:yyyyMMdd-HHmmss-fff}-{safeNs}.json");
                File.WriteAllText(file, json);
                _logger.LogInformation("Cloud Library diagnostics for '{Ns}' → {File}", queryNamespace, file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write Cloud Library diagnostics for {Ns}", queryNamespace);
            }
        }

        /// <summary>Cloud Library namespace URI, wherever the response shape happens to carry it.</summary>
        private static string? CloudLibNsUri(Opc.Ua.CloudLibraryApi.UANodesetResult r)
            => r.Nodeset?.NamespaceUri ?? r.NodesetNamespaceUri;

        /// <summary>
        /// Every Cloud Library entry for <paramref name="namespaceUri"/>, newest first.
        ///
        /// NEVER pass the URI in find2's <c>namespaceUri</c> parameter: it is not wired up on the
        /// Cloud Library side, and because offset/limit are applied before any filtering, a
        /// namespace past the first window came back as an empty result — which callers read as
        /// "this namespace isn't published". Search for the URI as a KEYWORD (which is wired up)
        /// and keep only exact namespace matches here.
        /// </summary>
        private async Task<List<Opc.Ua.CloudLibraryApi.UANodesetResult>> FindCloudLibraryEntriesAsync(
            string namespaceUri)
        {
            var matches = new List<Opc.Ua.CloudLibraryApi.UANodesetResult>();
            if (_cloudLib == null) return matches;

            const int pageSize = 100;
            const int maxPages = 50;
            var keywords = new[] { namespaceUri };
            for (var page = 0; page < maxPages; page++)
            {
                var batch = await _cloudLib.Find2Async(
                    keywords: keywords, namespaceUri: null,
                    offset: page * pageSize, limit: pageSize);
                if (batch == null || batch.Length == 0) break;

                // Keyword hits are fuzzy — a URI keyword also matches neighbouring namespaces that
                // merely mention it (e.g. every Mining/* spec that requires Mining/General). Only
                // an exact namespace match is this namespace.
                matches.AddRange(batch.Where(r => string.Equals(
                    CloudLibNsUri(r), namespaceUri, StringComparison.OrdinalIgnoreCase)));

                if (batch.Length < pageSize) break;
            }

            return matches
                .OrderByDescending(r => r.Nodeset?.PublicationDate ?? r.PublicationDate)
                .ThenByDescending(r => r.Nodeset?.Version ?? r.Version)
                .ToList();
        }

        /// <summary>
        /// Blocks publishing a model whose namespace the UA Cloud Library already publishes.
        /// Publishing is the act that makes a model the shared answer for its URI to every other
        /// user, so allowing it here would let a local edit supersede the Cloud Library — the
        /// failure this guard exists to prevent. Editing such a namespace is unrestricted inside
        /// the owning workspace; only the hand-off to other users is closed.
        ///
        /// Fails CLOSED on a Cloud Library error (publishing is rare and retryable, and a
        /// lookup failure must not become a way around the check), and is a no-op when no Cloud
        /// Library is configured at all — with no Cloud Library there is nothing to supersede.
        /// </summary>
        private async Task GuardCloudLibraryNamespaceAsync(string? modelUri)
        {
            if (string.IsNullOrWhiteSpace(modelUri) || _cloudLib == null) return;

            bool inCloudLibrary;
            try
            {
                // Keyword lookup with exact-match filtering — the only namespace lookup that
                // actually works against the Cloud Library (find2's namespaceUri parameter is not
                // wired up, and returns empty for anything past the first window).
                inCloudLibrary = (await FindCloudLibraryEntriesAsync(modelUri)).Count > 0;

                // Second opinion from the namespace list, which answers membership directly. Only
                // ever used to turn a "no" into a "yes": a keyword index that fails to match a URI
                // would silently reopen the hole this guard exists to close, whereas this endpoint
                // failing costs nothing. Deliberately broader than NodeSet identity (where a
                // trailing slash is significant and never normalised) — over-matching just blocks
                // a publish a human can sort out.
                if (!inCloudLibrary)
                {
                    try
                    {
                        var namespaces = await _cloudLib.GetNamespacesAsync();
                        static string Key(string s) => s.TrimEnd('/').ToLowerInvariant();
                        inCloudLibrary = namespaces?.Any(n => Key(n) == Key(modelUri)) == true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "Cloud Library namespace list unavailable while checking '{Uri}'", modelUri);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Cloud Library lookup failed while checking whether '{Uri}' may be published", modelUri);
                throw new InvalidOperationException(
                    $"Could not check whether '{modelUri}' is published in the UA Cloud Library, " +
                    "so publishing is blocked. Try again when the Cloud Library is reachable.");
            }

            if (inCloudLibrary)
            {
                _logger.LogInformation(
                    "Publish blocked for '{Uri}': the namespace is published in the UA Cloud Library", modelUri);
                throw new InvalidOperationException(
                    $"'{modelUri}' is published in the UA Cloud Library, so it cannot be published here — " +
                    "a local copy must never become the shared answer for a Cloud Library namespace. " +
                    "Keep working on it in this workspace (add the Cloud Library model, then check it out), " +
                    "or submit the change to the UA Cloud Library.");
            }
        }

        private async Task<bool> ImportDependencyFromCloudLibraryAsync(string namespaceUri, HashSet<string> importing)
        {
            if (_cloudLib == null) return false;

            try
            {
                static string? NsId(Opc.Ua.CloudLibraryApi.UANodesetResult r) =>
                    r.Nodeset?.Identifier ?? r.NodesetId;

                // A single namespace can be published by SEVERAL Cloud Library submissions under
                // DIFFERENT titles — e.g. the IRDI dictionary exists both standalone ("UA Part 19:
                // Dictionary References") and re-published inside the PADIM release ("Process
                // Automation Devices - PADIM"), both with NamespaceUri = .../Dictionary/IRDI. We
                // still take the newest for CONTENT, but when the titles disagree the submission
                // title is an unreliable name for this namespace (it may belong to the bundling
                // spec), so we fall back to a URI-derived name instead of mislabeling it.
                //
                // Paged and matched locally — a find2 call carrying namespaceUri silently misses
                // any namespace past the first window, which made this return "not in the Cloud
                // Library" and left the dependency unresolved.
                var sameNs = await FindCloudLibraryEntriesAsync(namespaceUri);

                var match = sameNs.FirstOrDefault();
                var matchId = match != null ? NsId(match) : null;

                // Titles conflict across submissions for this same namespace → don't trust the title.
                var titleAmbiguous = match != null
                    && sameNs.Any(r => !string.Equals(r.Title, match.Title, StringComparison.Ordinal));

                // Optional filesystem dump of exactly what the Cloud Library returned for this
                // namespace and which entry we selected (config-gated; see DumpCloudLibDiagnostics).
                DumpCloudLibDiagnostics(namespaceUri, sameNs.ToArray(), sameNs, match, titleAmbiguous);

                if (string.IsNullOrEmpty(matchId)) return false;

                var downloaded = await _cloudLib.DownloadAsync(matchId);
                if (downloaded?.Nodeset?.NodesetXml == null) return false;

                _logger.LogInformation(
                    "Importing dependency '{Title}' ({NsUri}) from Cloud Library",
                    match!.Title, namespaceUri);

                importing.Add(namespaceUri);

                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(downloaded.Nodeset.NodesetXml));
                var nodeSet = UANodeSet.Read(ms);

                await EnsureDependenciesAsync(nodeSet, importing);

                var dbModel = await NodeSetEditor.Model.NodeSetConverter.StoreNodeSetAsync(_db, nodeSet);
                dbModel.Origin = NodeSetEditor.Model.ModelOrigin.CloudLibrary;
                // Curated names/descriptions in the DB are never overridden by
                // automatic imports — only seed them when missing. This is the
                // path that used to clobber UA Core's name when a dependency
                // upgrade re-imported a version already in the DB.
                var uri = dbModel.Uri ?? namespaceUri;
                if (string.IsNullOrEmpty(dbModel.Name) || dbModel.Name == dbModel.Uri)
                    dbModel.Name = titleAmbiguous
                        ? DeriveModelName(uri)
                        : await DisambiguateImportNameAsync(match!.Title, uri);
                // Only carry the catalog description when the title isn't ambiguous — otherwise it
                // would describe the bundling spec, not this namespace.
                if (string.IsNullOrEmpty(dbModel.Description) && !titleAmbiguous)
                    dbModel.Description = match!.Description;
                StampImportLicenseIfMissing(dbModel, ms.ToArray(), downloaded.License, downloaded.CopyrightText);
                await _db.SaveChangesAsync();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import dependency '{Uri}' from Cloud Library", namespaceUri);
                return false;
            }
        }

        #endregion
    }
}
