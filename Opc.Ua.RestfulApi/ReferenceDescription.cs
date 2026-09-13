using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class ReferenceDescription
    {
        [JsonPropertyName("referenceTypeId")]
        public string ReferenceTypeId { get; set; } = string.Empty;

        [JsonPropertyName("referenceTypeName")]
        public string? ReferenceTypeName { get; set; }

        [JsonPropertyName("isForward")]
        public bool IsForward { get; set; }

        [JsonPropertyName("targetNodeId")]
        public string TargetNodeId { get; set; } = string.Empty;

        [JsonPropertyName("targetBrowseName")]
        public string? TargetBrowseName { get; set; }

        [JsonPropertyName("targetDisplayName")]
        public LocalizedText? TargetDisplayName { get; set; }

        [JsonPropertyName("targetNodeClass")]
        public string? TargetNodeClass { get; set; }

        [JsonPropertyName("typeDefinition")]
        public string? TypeDefinition { get; set; }

        [JsonPropertyName("modellingRule")]
        public string? ModellingRule { get; set; }

        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }

        [JsonPropertyName("dataTypeName")]
        public string? DataTypeName { get; set; }

        [JsonPropertyName("valueRank")]
        public int? ValueRank { get; set; }

        /// <summary>
        /// True when this reference is the structural parent reference of one
        /// of its endpoints (the one stored on the Node row as
        /// <c>ParentNodeId</c>+<c>ReferenceTypeId</c>, or as <c>SuperTypeId</c>
        /// for type nodes via HasSubtype). Such references define the
        /// hierarchy and cannot be deleted standalone — a node only ceases
        /// to be a child by being deleted or re-parented.
        /// </summary>
        [JsonPropertyName("isCanonicalParent")]
        public bool IsCanonicalParent { get; set; }
    }
}
