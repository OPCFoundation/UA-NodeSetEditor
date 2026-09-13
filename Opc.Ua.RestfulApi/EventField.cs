using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class EventField
    {
        [JsonPropertyName("browsePath")]
        public string? BrowsePath { get; set; }

        [JsonPropertyName("attributeId")]
        public int? AttributeId { get; set; }

        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }

        [JsonPropertyName("description")]
        public LocalizedText? Description { get; set; }
    }
}
