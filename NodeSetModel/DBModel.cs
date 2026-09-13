using System.Text.Json.Nodes;

namespace NodeSetEditor.Model
{
    /// <summary>
    /// OPC UA NodeClass numeric values matching the OPC UA spec (bit-flag encoding).
    /// </summary>
    public static class UaNodeClass
    {
        public const int Object = 1;
        public const int Variable = 2;
        public const int Method = 4;
        public const int ObjectType = 8;
        public const int VariableType = 16;
        public const int ReferenceType = 32;
        public const int DataType = 64;
        public const int View = 128;
    }

    /// <summary>
    /// Stores per-user preferences (e.g. selected workspace).
    /// Keyed by external user ID (Azure AD oid).
    /// </summary>
    public class UserPreference
    {
        public string UserId { get; set; } = null!;
        public Guid? SelectedWorkspaceId { get; set; }
        /// <summary>
        /// 'light' or 'dark' — matches the values used by the React
        /// UserProvider (ThemeModes constants). Null means "not yet set"
        /// and the client falls back to its localStorage value or the
        /// platform default.
        /// </summary>
        public string? ThemeMode { get; set; }

        /// <summary>
        /// Globally-unique display name shown to other users (workspace owner,
        /// shared-model creator). Provisioned on first login from the email's
        /// local part, made unique by appending digits; the user can change it
        /// from the account drawer (uniqueness is re-validated on change).
        /// Null only before the row has been provisioned.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Default domain used to seed the URI of newly created namespaces
        /// (e.g. "example.com" → "urn:opcua:example.com:YYYY-MM:"). Defaults to
        /// the email domain on first login; overridable in the account drawer.
        /// </summary>
        public string? DefaultDomain { get; set; }

        /// <summary>Email address from the identity token — refreshed on every login.</summary>
        public string? Email { get; set; }

        /// <summary>Display name from the identity token (the AAD "name" claim) — refreshed on every login.</summary>
        public string? DisplayName { get; set; }

        /// <summary>Azure AD tenant ID ("tid" claim) — identifies the user's home organisation.</summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Email domain of the authenticated identity — best-effort proxy for the user's
        /// organisation. For B2B guests this reflects the home identity domain, not the
        /// hosting tenant. Refreshed on every login.
        /// </summary>
        public string? TenantDomain { get; set; }

        /// <summary>UTC timestamp when the user accepted the Terms of Use. Null = not yet accepted.</summary>
        public DateTime? TermsAcceptedAt { get; set; }

        /// <summary>
        /// Default license identifier prefilled into the create-model dialog. Either an SPDX
        /// id from the <see cref="LicenseOption"/> catalog (e.g. "MIT") or a custom identifier
        /// when the user's default is "Other / Proprietary". Null until the user sets one.
        /// </summary>
        public string? DefaultLicense { get; set; }

        /// <summary>
        /// Reference URL for <see cref="DefaultLicense"/> when it is a custom ("Other") license.
        /// For catalog licenses the URL is resolved from <see cref="LicenseOption.ReferenceUrl"/>.
        /// </summary>
        public string? DefaultLicenseUrl { get; set; }

        /// <summary>
        /// Default copyright holder prefilled into the create-model dialog
        /// (e.g. "OPC Foundation, Inc."). Null until the user sets one.
        /// </summary>
        public string? DefaultCopyrightHolder { get; set; }
    }

    /// <summary>
    /// A pending email one-time login code for the passwordless "email code" sign-in
    /// path. One row per email (upserted on each request). The code itself is NEVER
    /// stored in plaintext — only a per-code salted SHA-256 hash. Security relies on a
    /// short expiry, a resend throttle, and an attempt lockout rather than the hash
    /// alone (a 6-digit code is trivially brute-forced offline; it is the lockout and
    /// 10-minute lifetime that make an online guess infeasible).
    /// </summary>
    public class LoginCode
    {
        /// <summary>Lowercased email address the code was issued to (primary key).</summary>
        public string Email { get; set; } = null!;

        /// <summary>Base64 salted SHA-256 hash of the 6-digit code.</summary>
        public string CodeHash { get; set; } = null!;

        /// <summary>Base64 per-code random salt mixed into <see cref="CodeHash"/>.</summary>
        public string Salt { get; set; } = null!;

        /// <summary>UTC instant after which the code is no longer accepted.</summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>Failed verify attempts against the current code; lockout at the configured max.</summary>
        public int Attempts { get; set; }

        /// <summary>UTC instant the current code was generated/sent — drives the resend throttle.</summary>
        public DateTime SentAt { get; set; }
    }

    /// <summary>
    /// Catalog of selectable licenses that drives the data-driven license selector.
    /// Seeded with a curated subset of SPDX licenses plus an "Other / Proprietary"
    /// entry (<see cref="IsCustom"/> = true) that lets the user supply a custom
    /// identifier and URL. The per-model choice is stored denormalized on
    /// <see cref="Model.License"/> / <see cref="Model.LicenseUrl"/>, not as a FK.
    /// </summary>
    public class LicenseOption
    {
        public int Id { get; set; }

        /// <summary>SPDX identifier, e.g. "MIT", "Apache-2.0", or "LicenseRef-Proprietary".</summary>
        public string SpdxId { get; set; } = null!;

        /// <summary>Human-readable license name shown in the selector.</summary>
        public string Name { get; set; } = null!;

        /// <summary>Canonical reference URL for the license text. Null for the custom entry.</summary>
        public string? ReferenceUrl { get; set; }

        /// <summary>
        /// True for the single "Other / Proprietary" entry. Selecting it requires the
        /// user to supply a custom license identifier and a valid reference URL.
        /// </summary>
        public bool IsCustom { get; set; }

        /// <summary>Display order in the selector (ascending).</summary>
        public int SortOrder { get; set; }
    }

    public class Workspace
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }

        /// <summary>External user ID (e.g. Azure AD oid) of the workspace owner.</summary>
        public string OwnerUserId { get; set; } = null!;

        /// <summary>Email of the workspace creator.</summary>
        public string? OwnerEmail { get; set; }

        public List<WorkspaceAcl>? Acl { get; set; }
        public List<WorkspaceModel>? Models { get; set; }
    }

    /// <summary>
    /// Grants a user (by email) access to a workspace.
    /// </summary>
    public class WorkspaceAcl
    {
        public Guid WorkspaceId { get; set; }
        public string Email { get; set; } = null!;

        public Workspace? Workspace { get; set; }
    }

    /// <summary>
    /// Where a model version's CONTENT came from. Stamped whenever the content is written
    /// (import / upload / create / checkout) and used to decide whether a row may satisfy
    /// ANOTHER workspace's dependency lookup: only <see cref="CloudLibrary"/> rows are a
    /// trusted global cache of a published namespace. A user's own <see cref="Authored"/>
    /// or <see cref="Upload"/> row stays visible in their own workspace but must never
    /// supersede the UA Cloud Library for anyone else.
    /// </summary>
    public enum ModelOrigin
    {
        /// <summary>Provenance not recorded (rows predating this column) — treated as untrusted.</summary>
        Unknown = 0,
        /// <summary>Downloaded verbatim from the UA Cloud Library (or the built-in UA core nodeset).</summary>
        CloudLibrary = 1,
        /// <summary>Uploaded as a NodeSet file by a user.</summary>
        Upload = 2,
        /// <summary>Created or edited in the editor (new model, checkout working copy).</summary>
        Authored = 3,
    }

    public class Model
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Uri { get; set; }

        /// <summary>Original freeform Version text from NodeSet XML. Preserved for round-trip.</summary>
        public string? Version { get; set; }

        /// <summary>
        /// Normalized SemVer for string comparison and sorting.
        /// Format: "{major:4}{minor:4}{patch:4}{suffix}" where suffix is "-prerelease" or "~" for release.
        /// e.g. "000100050001~" (1.5.1 release), "000100050001-beta" (1.5.1-beta).
        /// </summary>
        public string? VersionNorm { get; set; }

        /// <summary>
        /// Original publication date string from the NodeSet XML.
        /// Stored as-is for round-trip fidelity (freeform text in the spec).
        /// </summary>
        public string? PublicationDate { get; set; }

        /// <summary>Raw NodeSet content (XML or JSON) stored as bytea.</summary>
        public byte[]? Content { get; set; }

        /// <summary>True if this model failed to load into the address space.</summary>
        public bool HasErrors { get; set; }

        /// <summary>
        /// Provenance of this version's content — see <see cref="ModelOrigin"/>. Re-stamped every
        /// time the content is replaced, because a re-import from the Cloud Library over a
        /// previously authored row makes the row a Cloud Library copy (and vice versa).
        /// </summary>
        public ModelOrigin Origin { get; set; }

        /// <summary>
        /// True once this model version has been published (check-in "publish"), making it
        /// available for other users/workspaces to link via the shared-model picker. This is a
        /// property of the model version itself — distinct from <see cref="WorkspaceModel.IsPrivate"/>,
        /// which is the per-workspace visibility of a link. Defaults to false (working copies and
        /// freshly created models are unpublished).
        /// </summary>
        public bool Published { get; set; }

        /// <summary>
        /// Who published this model version: the local part of the publisher's email
        /// (domain stripped, e.g. "randy" from "randy@example.com"). Set at publish time
        /// and surfaced in the shared-model picker as a fallback when the publisher has
        /// no display Name. Null until published.
        /// </summary>
        public string? Creator { get; set; }

        /// <summary>
        /// External user id (Azure AD oid) of the publisher, captured at publish time so
        /// the shared-model picker can resolve the publisher's current display
        /// <see cref="UserPreference.Name"/> at read time (which stays correct even after
        /// the user renames themselves). Null until published.
        /// </summary>
        public string? CreatorUserId { get; set; }

        /// <summary>Extended metadata stored as JSONB (aliases, namespace URIs, etc.).</summary>
        public JsonObject? Metadata { get; set; }

        /// <summary>
        /// License identifier for this model. Either an SPDX id from the
        /// <see cref="LicenseOption"/> catalog (e.g. "MIT") or a custom identifier
        /// (e.g. "LicenseRef-OPC-Specification-1.15") when "Other / Proprietary" was chosen.
        /// Required for every model; set once at genesis (create / import / cloud-import /
        /// checkout-inherit) and immutable thereafter.
        /// </summary>
        public string? License { get; set; }

        /// <summary>
        /// Reference URL for the license. Resolved from <see cref="LicenseOption.ReferenceUrl"/>
        /// for catalog licenses; for a custom ("Other") license the user must supply a valid
        /// absolute http/https URL. Emitted on export as the SPDX "License:" header.
        /// </summary>
        public string? LicenseUrl { get; set; }

        /// <summary>
        /// Copyright holder for this model (e.g. "OPC Foundation, Inc."). Required for every
        /// model; set once at genesis and immutable thereafter. Emitted on export as the
        /// SPDX "SPDX-FileCopyrightText:" header.
        /// </summary>
        public string? CopyrightHolder { get; set; }

        public List<WorkspaceModel>? Workspaces { get; set; }
        public List<Node>? Nodes { get; set; }
        public List<Reference>? References { get; set; }

        /// <summary>
        /// Builds VersionNorm from a SemVer string.
        /// Call after setting Version if you want to update the normalized form separately.
        /// </summary>
        public void SetVersionNorm(string? semver)
        {
            VersionNorm = NormalizeVersion(semver);
        }

        /// <summary>
        /// Normalize a SemVer string to a zero-padded sortable form.
        /// "1.5.1" → "000100050001~", "1.5.1-beta" → "000100050001-beta".
        /// </summary>
        public static string? NormalizeVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            var v = version.Trim();
            string? prerelease = null;
            var dashIdx = v.IndexOf('-');
            if (dashIdx > 0)
            {
                prerelease = v[(dashIdx + 1)..];
                v = v[..dashIdx];
            }

            int major = 0, minor = 0, patch = 0;
            var parts = v.Split('.');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var m)) major = m;
            if (parts.Length >= 2 && int.TryParse(parts[1], out var n)) minor = n;
            if (parts.Length >= 3 && int.TryParse(parts[2], out var p)) patch = p;

            var norm = $"{major:D4}{minor:D4}{patch:D4}";
            return prerelease != null ? $"{norm}-{prerelease}" : $"{norm}~";
        }

        /// <summary>
        /// Compute the working-copy version produced by checking a model out for editing.
        /// Any pre-release suffix is stripped, the patch is incremented, and <c>-alpha</c> is
        /// appended — ALWAYS, including when the source is itself an <c>-alpha</c> checkpoint, so
        /// that every checkout leaves the prior version intact as a backup to discard back to.
        /// e.g. "1.0.0" → "1.0.1-alpha", "1.0.1-alpha" → "1.0.2-alpha", "2.3" → "2.3.1-alpha".
        /// </summary>
        public static string BumpForCheckout(string? version)
        {
            var (major, minor, patch, _) = ParseSemVer(version);
            return $"{major}.{minor}.{patch + 1}-alpha";
        }

        /// <summary>
        /// Compute the published version: replace an <c>-alpha</c> pre-release with <c>-beta</c>,
        /// keeping major.minor.patch. e.g. "1.0.1-alpha" → "1.0.1-beta". A version that is not
        /// <c>-alpha</c> is returned with a <c>-beta</c> suffix applied to its numeric core.
        /// </summary>
        public static string PublishVersion(string? version)
        {
            var (major, minor, patch, _) = ParseSemVer(version);
            return $"{major}.{minor}.{patch}-beta";
        }

        /// <summary>
        /// Strip the <c>-alpha</c> / <c>-beta</c> suffix this editor mints, keeping major.minor.patch.
        /// e.g. "1.0.1-alpha" → "1.0.1", "1.0.1-beta.2" → "1.0.1". Those two labels mean "checked out
        /// for editing" and "published from a working copy"; a model that is finished but not
        /// published is neither, so the marker comes off. Any other pre-release label is the
        /// caller's own and is left alone ("1.0.1-rc1" stays "1.0.1-rc1").
        /// </summary>
        public static string StripWorkingSuffix(string? version)
        {
            var (major, minor, patch, prerelease) = ParseSemVer(version);
            var core = $"{major}.{minor}.{patch}";
            if (prerelease == null) return core;

            // Match "alpha"/"beta" alone or with a counter ("beta.2"), case-insensitively.
            var label = prerelease.Split('.')[0];
            var isWorkingLabel = label.Equals("alpha", StringComparison.OrdinalIgnoreCase)
                || label.Equals("beta", StringComparison.OrdinalIgnoreCase);
            return isWorkingLabel ? core : $"{core}-{prerelease}";
        }

        /// <summary>
        /// Parse a SemVer-ish string into numeric major/minor/patch plus an optional
        /// pre-release label (text after the first '-'). Missing parts default to 0.
        /// </summary>
        private static (int major, int minor, int patch, string? prerelease) ParseSemVer(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return (0, 0, 0, null);

            var v = version.Trim();
            string? prerelease = null;
            var dashIdx = v.IndexOf('-');
            if (dashIdx > 0)
            {
                prerelease = v[(dashIdx + 1)..];
                v = v[..dashIdx];
            }

            int major = 0, minor = 0, patch = 0;
            var parts = v.Split('.');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var m)) major = m;
            if (parts.Length >= 2 && int.TryParse(parts[1], out var n)) minor = n;
            if (parts.Length >= 3 && int.TryParse(parts[2], out var p)) patch = p;
            return (major, minor, patch, prerelease);
        }
    }

    public class WorkspaceModel
    {
        public Guid WorkspaceId { get; set; }
        public Guid ModelId { get; set; }
        public bool IsPrivate { get; set; }

        /// <summary>
        /// True when the model has been explicitly checked out for editing in this workspace.
        /// Editing a model requires both <see cref="IsPrivate"/> and <see cref="IsEditable"/>.
        /// A checked-out model always carries an <c>-alpha</c> version. Defaults to false
        /// (locked); checkout sets it true, check-in (keep/publish) sets it back to false.
        /// </summary>
        public bool IsEditable { get; set; }

        public Workspace? Workspace { get; set; }
        public Model? Model { get; set; }
    }

    public class Node
    {
        public long Id { get; set; }
        public Guid ModelId { get; set; }
        public string? NodeId { get; set; }
        public string? ParentNodeId { get; set; }
        public string? ReferenceTypeId { get; set; }
        public int NodeClass { get; set; }
        public string? BrowseName { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? TypeDefinitionId { get; set; }
        public string? SuperTypeId { get; set; }
        public string? ModellingRule { get; set; }

        /// <summary>NodeClass-specific attributes stored as JSONB.</summary>
        public JsonObject? Attributes { get; set; }

        /// <summary>
        /// Import order preserved for deterministic export. Incremented by 100
        /// to allow inserting new children without reordering existing ones.
        /// </summary>
        public int Ordinal { get; set; }

        public Model? Model { get; set; }
    }

    public class Reference
    {
        public long Id { get; set; }
        public Guid ModelId { get; set; }
        public string? SourceNodeId { get; set; }
        public string? ReferenceTypeId { get; set; }
        public bool IsForward { get; set; }
        public string? TargetNodeId { get; set; }

        /// <summary>Import order for deterministic export.</summary>
        public int Ordinal { get; set; }

        public Model? Model { get; set; }
    }

    public class SubTypeHierarchy
    {
        public string SubTypeNodeId { get; set; } = null!;
        public string SuperTypeNodeId { get; set; } = null!;
        public int Depth { get; set; }
    }

    /// <summary>
    /// Stores a type definition with its full child tree in a denormalized JSONB column.
    /// One row per ObjectType, VariableType, DataType, or ReferenceType in a model.
    /// </summary>
    public class NodeSetType
    {
        public long Id { get; set; }
        public Guid ModelId { get; set; }
        public string NodeId { get; set; } = null!;
        public int NodeClass { get; set; }
        public string? BrowseName { get; set; }
        public string? DisplayName { get; set; }
        public string? SuperTypeId { get; set; }
        public bool IsAbstract { get; set; }

        /// <summary>
        /// Full child tree stored as JSONB. Each child node carries _origin, _sourceType,
        /// _modified markers for sync tracking. Structure mirrors the NodeSet child hierarchy.
        /// </summary>
        public JsonObject? Children { get; set; }

        /// <summary>
        /// Type's own references stored as JSONB array.
        /// </summary>
        public JsonArray? References { get; set; }

        public Model? Model { get; set; }
        public List<TypeDependency>? Dependencies { get; set; }
    }

    /// <summary>
    /// Tracks in-model type dependencies for eager cascade propagation.
    /// When a referenced type changes, all types that depend on it are updated.
    /// Only tracks same-model dependencies — cross-model dependencies are handled
    /// via Model.RequiredModels version comparison.
    /// </summary>
    public class TypeDependency
    {
        public long Id { get; set; }

        /// <summary>The type that contains a child referencing another type.</summary>
        public long TypeId { get; set; }

        /// <summary>The NodeId of the type definition used in the children tree.</summary>
        public string ReferencedTypeNodeId { get; set; } = null!;

        public NodeSetType? Type { get; set; }
    }

    /// <summary>
    /// Lifecycle state of a <see cref="ValidationJob"/> as stored in the DB.
    ///
    /// The UI's "Ready" is NOT a stored state — a document is "Ready" when it has no active
    /// job, which the UI derives from the newest job: <see cref="Cancelled"/> (and no job at
    /// all) map to Ready. <see cref="Cancelled"/> is a deliberate *tombstone* rather than a row
    /// deletion: cancelling a <see cref="Running"/> job must leave something for the in-flight
    /// worker's <c>Push</c> to hit so it no-ops (409) — as opposed to <b>deleting</b> the job
    /// row outright, which the worker sees as gone (410). Starting a new job for a document
    /// first clears any terminal (Completed/Failed/Cancelled) job.
    /// </summary>
    public enum ValidationJobState
    {
        /// <summary>Created by the user, waiting for a worker to <c>Pop</c> it.</summary>
        Queued = 0,

        /// <summary>Locked by a worker (Pop) and running; carries a non-null <see cref="ValidationJob.LockToken"/>.</summary>
        Running = 1,

        /// <summary>Worker finished (Push) and results were stored. Viewable until Reset.</summary>
        Completed = 2,

        /// <summary>Worker reported a non-zero exit code / crash. Results (if any) are still attached.</summary>
        Failed = 3,

        /// <summary>
        /// Cancelled by the user (from Queued or Running) or Reset from Completed. The
        /// <see cref="ValidationJob.LockToken"/> is cleared so any in-flight worker's Push
        /// no-ops. Displayed as "Ready"; cleared when a new job is started.
        /// </summary>
        Cancelled = 4,
    }

    /// <summary>
    /// A Word specification document uploaded by a user, scoped to a **workspace** (documents are
    /// per-workspace; the model to validate against is chosen per job, not per document). The .docx
    /// bytes are stored inline as bytea (max 64 MB, assembled from chunked upload). Deleting the
    /// workspace cascades to its documents.
    /// </summary>
    public class ValidationDocument
    {
        public Guid Id { get; set; }

        /// <summary>Workspace that owns this document (FK → Workspace, cascade).</summary>
        public Guid WorkspaceId { get; set; }

        public string FileName { get; set; } = null!;
        public long SizeBytes { get; set; }
        public string? ContentType { get; set; }

        /// <summary>The raw .docx bytes stored as bytea.</summary>
        public byte[] Content { get; set; } = null!;

        /// <summary>External user id (Azure AD oid) of the uploader.</summary>
        public string UploadedByUserId { get; set; } = null!;
        public DateTime UploadedUtc { get; set; }

        // Validator options, edited per document and snapshotted onto the job at start.
        /// <summary>Validator <c>--profile-group</c> full name (e.g. "UACore 1.05"); null = none.</summary>
        public string? ProfileGroupName { get; set; }
        /// <summary>Validator <c>--verbose</c> flag.</summary>
        public bool Verbose { get; set; }
        /// <summary>Semicolon-joined error codes to suppress (validator <c>--ignore</c>); null = none.</summary>
        public string? SuppressedCodes { get; set; }

        public Workspace? Workspace { get; set; }
        public List<ValidationJob>? Jobs { get; set; }
    }

    /// <summary>
    /// A single validation run of a <see cref="ValidationDocument"/> against its model's
    /// NodeSet, executed by an external worker via the Pop/Push queue contract.
    /// </summary>
    public class ValidationJob
    {
        public Guid Id { get; set; }

        /// <summary>The document being validated (FK → ValidationDocument, cascade).</summary>
        public Guid DocumentId { get; set; }

        /// <summary>Denormalized from the document for worker queries and IDOR re-checks.</summary>
        public Guid WorkspaceId { get; set; }

        /// <summary>
        /// The model selected (via the model's Validate icon) to validate against, saved when the
        /// job is started. Its <see cref="ModelUri"/> is denormalized alongside so the document row
        /// can show the applied model even if the model row is later deleted.
        /// </summary>
        public Guid ModelId { get; set; }
        public string? ModelUri { get; set; }

        // Validator options snapshotted from the document when the job is started, so editing the
        // document's options mid-run doesn't change an in-flight job. Passed to the worker via Pop.
        public string? ProfileGroupName { get; set; }
        public bool Verbose { get; set; }
        /// <summary>Semicolon-joined error codes to suppress (validator <c>--ignore</c>).</summary>
        public string? IgnoreCodes { get; set; }

        public ValidationJobState State { get; set; }

        public string RequestedByUserId { get; set; } = null!;
        public DateTime CreatedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }

        // Summary columns, set on Push (completion).
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int InfoCount { get; set; }
        public string? Summary { get; set; }
        public int? ExitCode { get; set; }
        public string? WorkerMessage { get; set; }

        // Lock: set on Pop, cleared/rotated so a stale worker's Push no-ops.
        public string? WorkerId { get; set; }
        public DateTime? PoppedUtc { get; set; }

        /// <summary>
        /// Opaque token stamped on <c>Pop</c> that binds a claim to its <c>Push</c>. A Push whose
        /// token does not match the current value (job cancelled, or re-popped by a newer run)
        /// is a no-op, so a stale worker can never overwrite a fresher result.
        /// </summary>
        public string? LockToken { get; set; }

        public ValidationDocument? Document { get; set; }
        public ValidationJobResult? Result { get; set; }
    }

    /// <summary>
    /// The stored artifacts of a completed <see cref="ValidationJob"/>: the raw validator log
    /// and the structured entries parsed from the validator's <c>*-validation.json</c> that the
    /// UI renders in the searchable/filterable results list.
    /// </summary>
    public class ValidationJobResult
    {
        /// <summary>PK and FK → ValidationJob (cascade).</summary>
        public Guid JobId { get; set; }

        /// <summary>The verbatim <c>*-validation.log</c> console output (bytea).</summary>
        public byte[]? LogContent { get; set; }

        /// <summary>
        /// Structured findings parsed from the validator's <c>*-validation.json</c>: an array of
        /// <c>{ section, table, severity, code, description }</c>. Stored as <c>json</c> (see the
        /// Metadata rationale) and consumed directly by the results dialog.
        /// </summary>
        public JsonArray? Entries { get; set; }

        public DateTime UploadedUtc { get; set; }

        public ValidationJob? Job { get; set; }
    }

    /// <summary>
    /// Staging row for a single chunk of an in-progress <see cref="ValidationDocument"/> upload.
    /// Chunks are accumulated as separate rows keyed by <see cref="UploadId"/> + <see cref="ChunkIndex"/>
    /// (avoids repeatedly rewriting a growing blob); when all <see cref="TotalChunks"/> have arrived
    /// they are concatenated into a ValidationDocument and the staging rows are deleted.
    /// </summary>
    public class ValidationUploadChunk
    {
        public string UploadId { get; set; } = null!;
        public int ChunkIndex { get; set; }

        // Upload metadata (repeated per chunk; read from any row on assembly).
        public Guid WorkspaceId { get; set; }
        public string FileName { get; set; } = null!;
        public int TotalChunks { get; set; }
        public string UploadedByUserId { get; set; } = null!;
        public DateTime CreatedUtc { get; set; }

        /// <summary>This chunk's raw bytes (bytea).</summary>
        public byte[] Data { get; set; } = null!;
    }

    /// <summary>
    /// Staging row for a single chunk of an in-progress NodeSet import upload. Mirrors
    /// <see cref="ValidationUploadChunk"/>: chunks accumulate keyed by <see cref="UploadId"/> +
    /// <see cref="ChunkIndex"/>; when all <see cref="TotalChunks"/> have arrived they are
    /// concatenated into the full NodeSet file and the staging rows are deleted.
    /// </summary>
    public class NodeSetUploadChunk
    {
        public string UploadId { get; set; } = null!;
        public int ChunkIndex { get; set; }

        // Upload metadata (repeated per chunk; read from any row on assembly).
        public Guid WorkspaceId { get; set; }
        public string FileName { get; set; } = null!;
        public int TotalChunks { get; set; }
        public DateTime CreatedUtc { get; set; }

        /// <summary>This chunk's raw bytes (bytea).</summary>
        public byte[] Data { get; set; } = null!;
    }
}
