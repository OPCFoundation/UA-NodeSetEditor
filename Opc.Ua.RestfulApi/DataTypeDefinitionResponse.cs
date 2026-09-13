using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class DataTypeDefinitionResponse
    {
        [JsonPropertyName("form")]
        public string? Form { get; set; }

        [JsonPropertyName("isUnion")]
        public bool? IsUnion { get; set; }

        [JsonPropertyName("isOptionSet")]
        public bool? IsOptionSet { get; set; }

        [JsonPropertyName("fields")]
        public List<DataTypeFieldDescription>? Fields { get; set; }
    }

    public class DataTypeFieldDescription
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public int? Value { get; set; }

        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }

        [JsonPropertyName("dataTypeName")]
        public string? DataTypeName { get; set; }

        [JsonPropertyName("valueRank")]
        public int? ValueRank { get; set; }

        [JsonPropertyName("arrayDimensions")]
        public string? ArrayDimensions { get; set; }

        [JsonPropertyName("maxStringLength")]
        public int? MaxStringLength { get; set; }

        [JsonPropertyName("isOptional")]
        public bool? IsOptional { get; set; }

        [JsonPropertyName("allowSubTypes")]
        public bool? AllowSubTypes { get; set; }

        [JsonPropertyName("description")]
        public LocalizedText? Description { get; set; }

        [JsonPropertyName("isInherited")]
        public bool? IsInherited { get; set; }

        [JsonPropertyName("sourceTypeNodeId")]
        public string? SourceTypeNodeId { get; set; }
    }
}
