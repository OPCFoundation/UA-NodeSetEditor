using System;
using System.Text.Json.Serialization;

namespace NodeSetEditor.Server.Model
{
    /// <summary>
    /// One stored version of a namespace URI, as seen from a particular workspace. A URI
    /// normally has several: the version currently in use, the backup a checkout retained,
    /// and any published releases. The versions dialog lists these so the user can see where
    /// each one came from and clean up the private ones they no longer want.
    /// </summary>
    public class ModelVersionInfo
    {
        /// <summary>Model row id — the handle for deleting this specific version.</summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("publicationDate")]
        public string? PublicationDate { get; set; }

        /// <summary>Provenance: "CloudLibrary", "Upload", "Authored" or "Unknown".</summary>
        [JsonPropertyName("origin")]
        public string? Origin { get; set; }

        /// <summary>True when this workspace holds it as a private (unshared) copy.</summary>
        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }

        /// <summary>True when it has been published and is therefore linkable by others.</summary>
        [JsonPropertyName("isPublished")]
        public bool IsPublished { get; set; }

        /// <summary>True for the version this workspace is actually using for the URI.</summary>
        [JsonPropertyName("isCurrent")]
        public bool IsCurrent { get; set; }

        /// <summary>True when this version is checked out for editing here.</summary>
        [JsonPropertyName("isEditable")]
        public bool IsEditable { get; set; }

        /// <summary>Number of OTHER workspaces holding a link to this version.</summary>
        [JsonPropertyName("otherWorkspaceCount")]
        public int OtherWorkspaceCount { get; set; }

        [JsonPropertyName("canDelete")]
        public bool CanDelete { get; set; }

        /// <summary>Why <see cref="CanDelete"/> is false — shown as the tooltip on the disabled button.</summary>
        [JsonPropertyName("blockedReason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BlockedReason { get; set; }
    }
}
