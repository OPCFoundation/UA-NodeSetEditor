using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class MethodArgument
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dataType")]
        public string DataType { get; set; } = string.Empty;

        [JsonPropertyName("dataTypeName")]
        public string? DataTypeName { get; set; }

        [JsonPropertyName("valueRank")]
        public int? ValueRank { get; set; }

        [JsonPropertyName("arrayDimensions")]
        public List<int>? ArrayDimensions { get; set; }

        [JsonPropertyName("description")]
        public LocalizedText? Description { get; set; }
    }
}
