using System.Text.Json.Nodes;
using NodeSetEditor.Server.Model;
using Opc.Ua.Export;
using NodeClass = NodeSetEditor.Server.Model.NodeClass;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Thrown when an operation would create a second model with the same URI and normalized
    /// version (violating the unique <c>(Uri, VersionNorm)</c> index) — e.g. publishing a
    /// working copy to a <c>-beta</c> version that already exists. Maps to HTTP 409.
    /// </summary>
    public class ModelVersionConflictException : Exception
    {
        public ModelVersionConflictException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when a node-graph write targets a model whose workspace link is not a
    /// private, checked-out working copy (<c>IsPrivate &amp;&amp; IsEditable</c>).
    /// Shared/published models and locked checkpoints are read-only — model rows are
    /// shared across workspaces, so an ungated write would propagate to every
    /// workspace linking the same model. Maps to HTTP 403 (BadNotWritable).
    /// </summary>
    public class ModelReadOnlyException : Exception
    {
        public ModelReadOnlyException(string message) : base(message) { }
    }

    public class NodeQueryFilter
    {
        public NodeClass? NodeClass { get; set; }
        public string? TypeDefinitionId { get; set; }

        /// <summary>
        /// Case-insensitive contains match on BrowseName or DisplayName.
        /// </summary>
        public string? NameFilter { get; set; }

        /// <summary>
        /// Exact match on NodeId.
        /// </summary>
        public string? NodeId { get; set; }
    }

    #region Changeset Types

    public enum ChangeKind { Upsert, Delete }

    public class NodeChange
    {
        public ChangeKind Kind { get; set; }
        public string NodeId { get; set; } = null!;

        // Populated for Upsert only:
        public int? NodeClass { get; set; }
        public string? BrowseName { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? ParentNodeId { get; set; }
        public string? SuperTypeId { get; set; }
        public string? TypeDefinitionId { get; set; }
        public string? ModellingRule { get; set; }
        public JsonObject? Attributes { get; set; }
    }

    public class ReferenceChange
    {
        public ChangeKind Kind { get; set; }
        public string SourceNodeId { get; set; } = null!;
        public string ReferenceTypeId { get; set; } = null!;
        public string TargetNodeId { get; set; } = null!;
        public bool IsForward { get; set; }
    }

    public class ModelChangeset
    {
        public Guid WorkspaceId { get; set; }
        public Guid ModelId { get; set; }
        public string ModelUri { get; set; } = null!;

        public List<NodeChange> Nodes { get; set; } = new();
        public List<ReferenceChange> References { get; set; } = new();
    }

    #endregion

    public interface INodeSetStorageService
    {
        // User-scoped workspace operations
        Task<List<Workspace>> GetWorkspacesAsync(string userId, string? userEmail);
        Task<Workspace> CreateWorkspaceAsync(string userId, string? ownerEmail, string name, string? description, List<string>? acl = null);
        Task DeleteWorkspaceAsync(string userId, Guid workspaceId);

        // Id-scoped workspace operations (caller must have already authorized access)
        Task<Workspace?> GetWorkspaceAsync(Guid workspaceId);
        Task<Workspace> UpdateWorkspaceAsync(Guid workspaceId, string? name, string? description, List<string>? acl = null);

        // ACL
        Task<List<string>> GetWorkspaceAclAsync(string userId, Guid workspaceId);
        Task SetWorkspaceAclAsync(string userId, Guid workspaceId, List<string> emails);

        // Model management
        Task<List<ModelInfo>> GetSharedModelsAsync();
        Task<List<ModelInfo>> GetPublishedModelsAsync();

        /// <summary>
        /// Whether a model may be linked into a workspace by an arbitrary authenticated user.
        /// True only for models that are intended to be shared: a published catalog version, a
        /// Cloud Library copy, or a not-yet-imported OPC UA Cloud Library catalog GUID. A user's
        /// own authored or uploaded model is NOT linkable, however its workspace links happen to
        /// be flagged — link flags do not confer shareability, because treating a non-private link
        /// as "shared" let one user's model supersede the Cloud Library for a namespace it does
        /// not own. Without this gate the link endpoint is an IDOR: because model rows are
        /// shared across workspaces and read by id, an authenticated user could link another user's
        /// private model into their own workspace and read its full contents.
        /// </summary>
        Task<bool> IsModelLinkableAsync(Guid modelId);

        Task<List<ModelInfo?>> GetWorkspaceModelsAsync(Guid workspaceId);
        Task<Workspace> UpdateWorkspaceModelsAsync(Guid workspaceId, List<ModelReference> models);

        // Download/Upload
        Task<(Stream FileStream, ModelInfo ModelInfo)> GetModelFileStreamAsync(Guid workspaceId, Guid modelId);
        Task<(Stream FileStream, ModelInfo ModelInfo)> GetSharedModelFileStreamAsync(Guid modelId);
        // license/licenseUrl/copyrightHolder come from the import dialog. The server prefers values
        // embedded in the file's SPDX headers (and OPC Foundation defaults for foundation namespaces);
        // these are the fallback used when the file carries none. Throws when neither source yields a
        // license + copyright, or when a custom/"Other" license has no valid URL.
        Task<UploadResult> HandleUploadChunkAsync(Guid workspaceId, Stream content, string fileName, int chunkIndex, int totalChunks, string? uploadId,
            string? license = null, string? licenseUrl = null, string? copyrightHolder = null);

        // User preferences
        Task<Guid?> GetUserSelectedWorkspaceAsync(string userId);
        Task SetUserSelectedWorkspaceAsync(string userId, Guid workspaceId);
        Task<string?> GetUserThemeModeAsync(string userId);
        Task SetUserThemeModeAsync(string userId, string themeMode);

        /// <summary>
        /// Finds or creates the user's preference row. On first sight, provisions a unique
        /// display <see cref="NodeSetEditor.Model.UserPreference.Name"/> (from the email local
        /// part) and a <see cref="NodeSetEditor.Model.UserPreference.DefaultDomain"/>. On every
        /// call, refreshes <see cref="NodeSetEditor.Model.UserPreference.Email"/>,
        /// <see cref="NodeSetEditor.Model.UserPreference.DisplayName"/>, and
        /// <see cref="NodeSetEditor.Model.UserPreference.TenantId"/> from the live token.
        /// Idempotent — safe to call on every authenticated request.
        /// </summary>
        Task<NodeSetEditor.Model.UserPreference> EnsureUserPreferenceAsync(string userId, string? email, string? displayName = null, string? tenantId = null);

        /// <summary>
        /// Sets the user's display name. Returns (false, message) when the name is empty or
        /// already taken (case-insensitively) by another user; (true, null) on success.
        /// </summary>
        Task<(bool Ok, string? Error)> SetUserNameAsync(string userId, string name);

        /// <summary>Sets (or clears, when null/blank) the user's default namespace domain.</summary>
        Task SetUserDefaultDomainAsync(string userId, string? domain);

        /// <summary>
        /// Sets (or clears, when null/blank) the user's default license. <paramref name="licenseUrl"/>
        /// is the reference URL stored for a custom ("Other") license; it is ignored/cleared for
        /// catalog licenses (whose URL comes from the catalog).
        /// </summary>
        Task SetUserDefaultLicenseAsync(string userId, string? license, string? licenseUrl);

        /// <summary>Sets (or clears, when null/blank) the user's default copyright holder.</summary>
        Task SetUserDefaultCopyrightHolderAsync(string userId, string? copyrightHolder);

        /// <summary>Returns the selectable license catalog (ordered by SortOrder) that drives the selector.</summary>
        Task<List<NodeSetEditor.Model.LicenseOption>> GetLicenseOptionsAsync();

        /// <summary>Records that the user has accepted the Terms of Use (idempotent).</summary>
        Task AcceptTermsAsync(string userId);

        /// <summary>
        /// Resolves a set of user ids to their display Names. Ids without a Name (or without a
        /// preference row) are omitted from the result.
        /// </summary>
        Task<Dictionary<string, string>> GetUserDisplayNamesAsync(IEnumerable<string> userIds);

        // NodeSet operations
        Task<UANodeSet> DownloadNodeSetAsync(Guid workspaceId, string modelUri, string? version = null);
        // isPrivate=true: workspace owns an editable copy (file uploads).
        // isPrivate=false: workspace only links to a shared read-only copy
        // (cloud library imports — a separate "create editable copy" feature
        // will fork to a private model when the user wants to edit).
        // isEditable=true checks the new model out immediately (used when authoring a brand-new
        // model, which starts unlocked). Imports leave it false (locked) — the user must check out.
        // license/licenseUrl/copyrightHolder are stamped onto the new model row at genesis and are
        // immutable thereafter. Callers source them from: the create dialog, embedded SPDX headers
        // (or the import dialog) on upload, or Cloud Library metadata on cloud import.
        // origin records where the CONTENT came from and decides whether the row may satisfy
        // ANOTHER workspace's dependency lookup — only ModelOrigin.CloudLibrary rows may.
        // Cloud Library imports pass CloudLibrary; file uploads and new models pass Upload/Authored.
        Task<ModelInfo> UploadNodeSetAsync(Guid workspaceId, UANodeSet nodeSet, bool isPrivate = true, bool isEditable = false,
            string? license = null, string? licenseUrl = null, string? copyrightHolder = null,
            NodeSetEditor.Model.ModelOrigin origin = NodeSetEditor.Model.ModelOrigin.Upload);
        Task<List<TypeDefinition>> QueryNodesAsync(Guid workspaceId, NodeQueryFilter filter);

        // Model metadata
        // license/licenseUrl/copyrightHolder are editable for private models; the caller validates and
        // resolves the URL before passing them, and only sets them when non-null.
        // enforceReadOnlyReserved=true (the user-facing edit path) rejects edits to models in the
        // reserved http://opcfoundation.org/ namespace even when private; internal importers leave it false.
        Task<ModelInfo> UpdateModelInfoAsync(Guid workspaceId, Guid modelId, string? name, string? version, string? description,
            string? license = null, string? licenseUrl = null, string? copyrightHolder = null,
            bool enforceReadOnlyReserved = false);

        /// <summary>
        /// Delete a model (and its nodes/references) when it has become orphaned — i.e. it is no
        /// longer linked to any workspace and is not a published/shared catalog version. A no-op
        /// for models that are still referenced or published. Used after removing a model from a
        /// workspace so deleted private working copies don't accumulate as unreachable rows.
        /// </summary>
        Task DeleteModelIfOrphanedAsync(Guid modelId);

        /// <summary>
        /// List every stored version of the URI behind <paramref name="modelId"/> that this
        /// workspace can see — its own links plus any published releases — annotated with
        /// provenance and whether each one may be deleted from here.
        /// </summary>
        Task<List<ModelVersionInfo>> GetModelVersionsAsync(Guid workspaceId, Guid modelId);

        /// <summary>
        /// Delete one stored version from this workspace. Only a private version that the
        /// workspace is not currently using and no other workspace references may go; anything
        /// else throws <see cref="InvalidOperationException"/> with the reason.
        /// </summary>
        Task DeleteModelVersionAsync(Guid workspaceId, Guid modelId, Guid versionId);

        // Checkout / Checkin (lock / unlock)
        /// <summary>
        /// Make a model editable in this workspace. If the model is already a private, locked
        /// <c>-alpha</c> working copy it is simply re-enabled (no version change); otherwise a new
        /// <c>-alpha</c> working copy is minted (patch incremented) and the source is left untouched.
        /// Returns the editable model.
        /// </summary>
        Task<ModelInfo> CheckoutModelAsync(Guid workspaceId, Guid modelId);

        /// <summary>
        /// Finish editing a checked-out model. <paramref name="action"/> is one of
        /// "keep" (lock, stays private, working-copy suffix dropped),
        /// "publish" (<c>-alpha</c>→<c>-beta</c>, make public), or
        /// "discard" (delete the working copy, restoring the prior good version).
        /// <paramref name="version"/> applies to keep and publish; when blank the server
        /// derives one. Returns the resulting model, or null when the working copy was discarded.
        /// </summary>
        Task<ModelInfo?> CheckinModelAsync(Guid workspaceId, Guid modelId, string action,
            string? version = null, string? description = null, string? creator = null,
            string? creatorUserId = null);

        // Persistence
        /// <summary>
        /// Whether this storage backend needs an XML stream passed to PersistChangesAsync.
        /// File-backed: true (writes XML files). DB-backed: false (uses changeset rows).
        /// </summary>
        bool RequiresXmlContent { get; }

        /// <summary>
        /// Persist model changes. DB impl uses the changeset for targeted row ops.
        /// File impl uses xmlContent to write the model file.
        /// </summary>
        Task PersistChangesAsync(ModelChangeset changeset, Stream? xmlContent);

        /// <summary>
        /// Save raw model content (used during initial import/upload only).
        /// </summary>
        Task SaveModelContentAsync(Guid workspaceId, Guid modelId, Stream content);
    }
}
