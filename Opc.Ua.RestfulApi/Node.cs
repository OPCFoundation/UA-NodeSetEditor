using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum NodeClass
    {
        Object,
        Variable,
        Method,
        ObjectType,
        VariableType,
        ReferenceType,
        DataType,
        View
    }

    public class Node
    {
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("nodeClass")]
        public NodeClass NodeClass { get; set; }

        [JsonPropertyName("browseName")]
        public string BrowseName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public LocalizedText DisplayName { get; set; } = new LocalizedText();

        [JsonPropertyName("description")]
        public LocalizedText? Description { get; set; }

        [JsonPropertyName("writeMask")]
        public int? WriteMask { get; set; }

        [JsonPropertyName("userWriteMask")]
        public int? UserWriteMask { get; set; }

        [JsonPropertyName("value")]
        public object? Value { get; set; }

        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }

        [JsonPropertyName("dataTypeName")]
        public string? DataTypeName { get; set; }

        [JsonPropertyName("valueRank")]
        public int? ValueRank { get; set; }

        [JsonPropertyName("arrayDimensions")]
        public List<int>? ArrayDimensions { get; set; }

        [JsonPropertyName("accessLevel")]
        public int? AccessLevel { get; set; }

        [JsonPropertyName("userAccessLevel")]
        public int? UserAccessLevel { get; set; }

        [JsonPropertyName("minimumSamplingInterval")]
        public double? MinimumSamplingInterval { get; set; }

        [JsonPropertyName("historizing")]
        public bool? Historizing { get; set; }

        [JsonPropertyName("executable")]
        public bool? Executable { get; set; }

        [JsonPropertyName("userExecutable")]
        public bool? UserExecutable { get; set; }

        [JsonPropertyName("isAbstract")]
        public bool? IsAbstract { get; set; }

        [JsonPropertyName("designToolOnly")]
        public bool? DesignToolOnly { get; set; }

        [JsonPropertyName("isProperty")]
        public bool? IsProperty { get; set; }

        [JsonPropertyName("symmetric")]
        public bool? Symmetric { get; set; }

        [JsonPropertyName("inverseName")]
        public LocalizedText? InverseName { get; set; }

        [JsonPropertyName("containsNoLoops")]
        public bool? ContainsNoLoops { get; set; }

        [JsonPropertyName("eventNotifier")]
        public int? EventNotifier { get; set; }

        [JsonPropertyName("typeDefinition")]
        public string? TypeDefinition { get; set; }

        [JsonPropertyName("typeDefinitionName")]
        public string? TypeDefinitionName { get; set; }

        [JsonPropertyName("modellingRule")]
        public string? ModellingRule { get; set; }

        [JsonPropertyName("superTypeId")]
        public string? SuperTypeId { get; set; }

        [JsonPropertyName("superTypeIds")]
        public List<string>? SuperTypeIds { get; set; }

        [JsonPropertyName("parentNodeId")]
        public string? ParentNodeId { get; set; }

        /// <summary>
        /// The namespace URI the node lives in (its NodeId's "nsu=" part, or the core
        /// namespace for a bare NodeId). Clients render "[ModelName]:" prefixes from it.
        /// </summary>
        [JsonPropertyName("modelUri")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ModelUri { get; set; }

        [JsonPropertyName("referenceType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReferenceType { get; set; }

        [JsonPropertyName("referenceTypeId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReferenceTypeId { get; set; }

        [JsonPropertyName("hasNoSubtypes")]
        public bool? HasNoSubtypes { get; set; }

        [JsonPropertyName("hasNoChildren")]
        public bool? HasNoChildren { get; set; }

        [JsonPropertyName("isInherited")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsInherited { get; set; }

        [JsonPropertyName("sourceTypeNodeId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourceTypeNodeId { get; set; }

        [JsonPropertyName("isOverride")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsOverride { get; set; }

        [JsonPropertyName("dataTypeForm")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DataTypeForm { get; set; }

        [JsonPropertyName("documentation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Documentation { get; set; }

        /// <summary>
        /// The conformance units this node belongs to — the <c>&lt;Category&gt;</c> elements of
        /// the NodeSet XML. Named for the XML element rather than the concept so the mapping
        /// stays obvious; omitted when the node names none.
        /// </summary>
        [JsonPropertyName("category")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Category { get; set; }
    }
}
