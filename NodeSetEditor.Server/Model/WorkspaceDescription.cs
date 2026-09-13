using System;
using System.Text.Json.Serialization;
using Opc.Ua.RestfulApi;

namespace NodeSetEditor.Server.Model
{
    public class WorkspaceDescription : ApplicationDescription
    {
        [JsonPropertyName("description")]
        public LocalizedText? Description { get; set; }

        [JsonPropertyName("serverConfiguration")]
        public ServerConfiguration? ServerConfiguration { get; set; }

        [JsonPropertyName("owner")]
        public string? Owner { get; set; }

        [JsonPropertyName("isOwner")]
        public bool? IsOwner { get; set; }

        /// <summary>
        /// True if the current user may modify this workspace (its content,
        /// models, metadata, and ACL). Only the owner can write; users the
        /// workspace is shared with are read-only.
        /// </summary>
        [JsonPropertyName("canWrite")]
        public bool? CanWrite { get; set; }

        [JsonPropertyName("acl")]
        public List<string>? Acl { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("modifiedAt")]
        public DateTime? ModifiedAt { get; set; }
    }
}
