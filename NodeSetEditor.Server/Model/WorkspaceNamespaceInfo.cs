using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Opc.Ua.RestfulApi;

namespace NodeSetEditor.Server.Model
{
    /// <summary>
    /// Extended NamespaceInfo with workspace-specific fields.
    /// </summary>
    public class WorkspaceNamespaceInfo : NamespaceInfo
    {
        [JsonPropertyName("id")]
        public Guid? Id { get; set; }

        [JsonPropertyName("isPrivate")]
        public bool? IsPrivate { get; set; }

        /// <summary>True when the model is checked out for editing in this workspace.</summary>
        [JsonPropertyName("isEditable")]
        public bool? IsEditable { get; set; }

        /// <summary>
        /// Where this model's content came from — "CloudLibrary", "Upload", "Authored" or
        /// "Unknown" (see <see cref="NodeSetEditor.Model.ModelOrigin"/>).
        ///
        /// Exposed so the UI can tell a Cloud Library model from one that is merely shared.
        /// They behave identically for editing (both read-only to a non-admin), but only the
        /// Cloud Library one can never be published — the namespace is already published
        /// there — so the two need different wording and a different icon rather than one
        /// generic "read-only" state.
        /// </summary>
        [JsonPropertyName("origin")]
        public string? Origin { get; set; }

        /// <summary>
        /// True when this workspace holds a Cloud Library copy of this namespace — so the
        /// namespace is published there and check-in can never publish it from here.
        ///
        /// Keyed on the URI rather than this row's own provenance, because the private working
        /// copy of a Cloud Library namespace reads as Authored (checkout resets Origin). Use it
        /// to explain and disable, not to authorize: a namespace can be in the Cloud Library
        /// with nothing cached here, and the server's publish guard remains the real check.
        /// </summary>
        [JsonPropertyName("isCloudLibraryNamespace")]
        public bool? IsCloudLibraryNamespace { get; set; }

        [JsonPropertyName("isReadOnly")]
        public bool? IsReadOnly { get; set; }

        [JsonPropertyName("requiredNamespaceUris")]
        public List<string>? RequiredNamespaceUris { get; set; }

        [JsonPropertyName("hasErrors")]
        public bool? HasErrors { get; set; }

        /// <summary>License identifier (SPDX id or custom "LicenseRef-…" id). Set once at genesis.</summary>
        [JsonPropertyName("license")]
        public string? License { get; set; }

        /// <summary>Reference URL for the license (required for custom/"Other" licenses).</summary>
        [JsonPropertyName("licenseUrl")]
        public string? LicenseUrl { get; set; }

        /// <summary>Copyright holder (e.g. "OPC Foundation, Inc."). Set once at genesis.</summary>
        [JsonPropertyName("copyrightHolder")]
        public string? CopyrightHolder { get; set; }

        /// <summary>
        /// Profile group the NodeSet's conformance units are assessed against (a
        /// <c>fullName</c> from profiles.opcfoundation.org, e.g. "UACore 1.05"); null = none.
        /// Edited in the model dialog and shown on the conformance-unit view.
        /// </summary>
        [JsonPropertyName("profileGroupName")]
        public string? ProfileGroupName { get; set; }

        [JsonPropertyName("errorMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorMessage { get; set; }
    }
}
