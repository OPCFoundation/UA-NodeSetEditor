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
